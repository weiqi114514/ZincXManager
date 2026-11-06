package com.zincxmanager.zxmm;

import java.io.BufferedReader;              //导入BufferedReader(按行读)
import java.io.IOException;                 //导入IOException(关连接时可能抛)
import java.io.InputStreamReader;           //导入InputStreamReader(字节流转文本)
import java.io.OutputStream;                //导入OutputStream(写回执)
import java.net.InetSocketAddress;          //导入InetSocketAddress(绑监听地址)
import java.net.ServerSocket;               //导入ServerSocket(监听端口)
import java.net.Socket;                     //导入Socket(单条连接)
import java.net.SocketTimeoutException;     //导入SocketTimeoutException(等令牌超时)
import java.nio.charset.StandardCharsets;   //导入StandardCharsets(UTF_8)
import java.util.ArrayList;                   //导入ArrayList(在线玩家列表)
import java.util.List;                      //导入List(列表接口)
import java.util.Locale;                    //导入Locale(名字统一转小写)
import java.util.Map;                       //导入Map(登录等待表)
import java.util.concurrent.CompletableFuture;   //导入CompletableFuture(延迟执行踢人/登录等待)
import java.util.concurrent.ConcurrentHashMap;   //导入ConcurrentHashMap(线程安全的登录等待表)
import java.util.concurrent.CopyOnWriteArrayList; //导入CopyOnWriteArrayList(连接列表,遍历时不怕被改)
import java.util.concurrent.TimeUnit;       //导入TimeUnit(等主线程用的 SECONDS)
import java.util.function.Supplier;         //导入Supplier(mc.submit 的入参)

import net.minecraft.network.chat.Component;                 //导入Component(踢人理由)
import net.minecraft.server.MinecraftServer;                 //导入MinecraftServer(回主线程/玩家表)
import net.minecraft.server.level.ServerPlayer;              //导入ServerPlayer(找在线玩家/坐标)

/**
 * ZXMM ↔ ZXMS 的**专用通道**：ZXMM 监听(默认只听 127.0.0.1)，ZXMS 连上来并一直保持。
 *
 * <p>一条连接双向跑：</p>
 * <pre>
 * ZXMS → ZXMM   hello|令牌                       (配了 api.token 才有这一步)
 * ZXMM → ZXMS   evt|joined|玩家名|IP            玩家进服事件
 * ZXMS → ZXMM   req|id|方法|参数…                下发任务
 * ZXMM → ZXMS   res|id|ok|结果 / res|id|err|原因  任务结果
 * </pre>
 *
 * <p>ZXMM 这边**没有任何流程**：不查表、不判断、不缓存，只负责"上报进服"和"执行被调用的方法"。
 * ZXMS 没连上时什么都不做:不攒事件、不踢人、不提示 —— 玩家照常进服。</p>
 */
public final class ZxmmApi {

    /** 默认监听端口(ZXMM ↔ ZXMS 专用，和 25565 / 21000 都不共用)。 */
    public static final int DEFAULT_PORT = 21200;

    /** 已连上的 ZXMS 连接(通常一条，重连瞬间可能有两条)。 */
    private static final List<Socket> LINKS = new CopyOnWriteArrayList<>();

    /** 正在 configuration 阶段、等 ZXMS 结论的连接(名字小写 → 结论;有结论 = 不许进)。 */
    private static final Map<String, CompletableFuture<String>> LOGIN_WAITS = new ConcurrentHashMap<>();

    /** 当前 MinecraftServer(服务器启动后注入)。 */
    private static volatile MinecraftServer server;

    /** 监听只起一次：二次启动 / 重载时不能再 bind 同一个端口。 */
    private static volatile boolean started;

    private ZxmmApi() {
    }

    /** 注入服务器实例。 */
    public static void attach(MinecraftServer mcServer) {
        server = mcServer;
    }

    /**
     * 起监听线程 + 兜底看门狗。
     *
     * @param host      监听地址(默认 127.0.0.1，不对外网开放)
     * @param port      监听端口
     * @param token     共享令牌，空 = 不校验
     */
    public static void start(String host, int port, String token) {
        if (started) {
            ZxmmMod.LOGGER.info("[ZXMM] 专用通道已在 {}:{} 上运行，不再重复监听", host, port);
            return;
        }

        started = true;

        Thread listener = new Thread(() -> listen(host, port, token), "zxmm-api");
        listener.setDaemon(true);
        listener.start();

        ZxmmMod.LOGGER.info("[ZXMM] 与 ZXMS 的专用通道已启动，监听 {}:{}，令牌{}",                host, port, token.isEmpty() ? "未启用" : "已启用");
    }

    /** 把"玩家进服"推给所有已连上的 ZXMS；一条都没连上就什么都不做(不攒、不提示、不踢)。 */
    public static void pushJoined(String player, String ip) {
        if (LINKS.isEmpty()) {
            return;
        }

        if (deliver("evt|joined|" + player + "|" + ip)) {
            ZxmmMod.LOGGER.info("[ZXMM] 已上报进服: {}", player);
        }
    }

    /** 有没有已连上的 ZXMS(没有就别让玩家白等登录检查)。 */
    public static boolean hasLink() {
        return !LINKS.isEmpty();
    }

    /** 登记一个正在连接(configuration 阶段)的玩家;ZXMS 要踢他时,这个 future 会带着理由完成。 */
    public static CompletableFuture<String> beginLoginWait(String player) {
        var future = new CompletableFuture<String>();
        LOGIN_WAITS.put(player.toLowerCase(Locale.ROOT), future);
        return future;
    }

    /** 结束等待(不管有没有结论)。 */
    public static void endLoginWait(String player) {
        LOGIN_WAITS.remove(player.toLowerCase(Locale.ROOT));
    }

    /** 把"玩家正在连接(configuration 阶段)"推给 ZXMS;没连上就什么都不做(不等待、不提示)。 */
    public static void pushJoining(String player, String ip) {
        if (LINKS.isEmpty()) {
            return;
        }

        deliver("evt|joining|" + player + "|" + ip);
    }

    /** 监听并保持连接；bind/accept 失败 3 秒后重试，不会一辈子哑掉。 */
    private static void listen(String host, int port, String token) {
        while (true) {
            try (ServerSocket serverSocket = new ServerSocket()) {
                serverSocket.setReuseAddress(true);
                serverSocket.bind(new InetSocketAddress(host, port));

                while (true) {
                    Socket client = serverSocket.accept();
                    client.setTcpNoDelay(true);
                    client.setKeepAlive(true);

                    Thread worker = new Thread(() -> serve(client, token), "zxmm-api-conn");
                    worker.setDaemon(true);
                    worker.start();
                }
            } catch (Exception e) {
                ZxmmMod.LOGGER.warn("[ZXMM] 专用通道监听 {}:{} 失败({})，3 秒后重试", host, port, e.toString());
                try {
                    Thread.sleep(3000);
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    return;
                }
            }
        }
    }

    /** 单条连接：先验令牌(配了才有)，再逐行读 ZXMS 的任务，执行完回一行结果。 */
    private static void serve(Socket client, String token) {
        String peer = String.valueOf(client.getRemoteSocketAddress());

        try (Socket socket = client;
             BufferedReader in = new BufferedReader(
                     new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8))) {

            OutputStream out = socket.getOutputStream();

            // 有令牌就必须先收到 hello|令牌，否则本机任何程序都能冒充 ZXMS 下发踢人
            if (!token.isEmpty()) {
                String hello;
                socket.setSoTimeout(5000);
                try {
                    hello = in.readLine();
                } finally {
                    socket.setSoTimeout(0);
                }

                if (hello == null || !hello.trim().equals("hello|" + token)) {
                    ZxmmMod.LOGGER.warn("[ZXMM] 拒绝未通过令牌校验的连接: {}", peer);
                    return;
                }
            }

            LINKS.add(socket);
            ZxmmMod.LOGGER.info("[ZXMM] ZXMS 已连上专用通道: {}", peer);

            String line;
            while ((line = in.readLine()) != null) {
                String reply = execute(line.trim());
                if (reply != null) {
                    out.write((reply + "\n").getBytes(StandardCharsets.UTF_8));
                    out.flush();
                }
            }
        } catch (SocketTimeoutException e) {
            ZxmmMod.LOGGER.warn("[ZXMM] 连接 {} 5 秒内没上报令牌，已断开", peer);
        } catch (Exception e) {
            ZxmmMod.LOGGER.info("[ZXMM] 专用通道连接结束: {}", e.toString());
        } finally {
            LINKS.remove(client);
            try {
                client.close();
            } catch (IOException ignored) {
                // 已经断了，关不掉无所谓
            }
        }
    }

    /** 执行一条任务。 */
    private static String execute(String line) {
        String[] parts = line.split("\\|", -1);

        // 期望格式: req|id|方法|参数…
        if (parts.length < 3 || !"req".equals(parts[0])) {
            return null;
        }

        String id = parts[1];
        String method = parts[2];

        try {
            return switch (method) {
                case "kick" -> {
                    String who = arg(parts, 3);
                    String why = rest(parts, 4);
                    Boolean done = onServer(() -> kick(who, why));
                    yield done == null ? fail(id, "服务器主线程忙，踢人没执行")
                            : done ? reply(id, "已踢出")
                            : fail(id, "没有这个在线玩家");
                }
                case "list" -> reply(id, listPlayers());
                case "info" -> reply(id, playerInfo(arg(parts, 3)));
                case "say" -> {
                    String text = rest(parts, 3);
                    yield text.isEmpty() ? fail(id, "广播内容是空的") : reply(id, say(text));
                }
                case "online" -> reply(id, online(arg(parts, 3)) ? "1" : "0");
                case "ping" -> reply(id, "pong");
                default -> fail(id, "没有这个方法: " + method);
            };
        } catch (Exception e) {
            return fail(id, e.getMessage() == null ? e.toString() : e.getMessage());
        }
    }

    /** 成功回包。 */
    private static String reply(String id, String result) {
        return "res|" + id + "|ok|" + result;
    }

    /** 失败回包：ZXMS 靠 ok/err 判断这件事到底有没有做成。 */
    private static String fail(String id, String reason) {
        return "res|" + id + "|err|" + reason;
    }

    /** 取一个参数(越界给空串)。 */
    private static String arg(String[] parts, int index) {
        return index < parts.length ? parts[index] : "";
    }

    /** 把第 index 段之后的全部拼回去(理由/广播里的 '|' 不该被截掉)。 */
    private static String rest(String[] parts, int index) {
        if (index >= parts.length) {
            return "";
        }

        StringBuilder sb = new StringBuilder(parts[index]);
        for (int i = index + 1; i < parts.length; i++) {
            sb.append('|').append(parts[i]);
        }

        return sb.toString();
    }

    /** 进服后至少等这么多 tick 再踢:客户端还在加载世界时断开,它只显示 Internal Exception,看不到理由。 */
    private static final int KICK_SETTLE_TICKS = 40;

    /** 踢人(必须在服务器主线程调用)。刚进服的玩家先等一会儿,让客户端把世界加载完,理由才显示得出来。 */
    private static boolean kick(String player, String reason) {
        String text = reason == null || reason.isEmpty() ? "被服务器移出" : reason;

        // 还在 configuration 阶段(没有 ServerPlayer):直接完成那条登录等待,断开登录连接
        var waiter = LOGIN_WAITS.remove(player.toLowerCase(Locale.ROOT));
        if (waiter != null) {
            waiter.complete(text);
            ZxmmMod.LOGGER.info("[ZXMM] 在连接阶段拒绝 {}：{}", player, text);
            return true;
        }

        ServerPlayer target = findPlayer(player);
        if (target == null) {
            return false;
        }

        int waited = target.tickCount;   //玩家进服后过了多少 tick
        if (waited < KICK_SETTLE_TICKS) {
            long delayMs = (KICK_SETTLE_TICKS - waited) * 50L;
            ZxmmMod.LOGGER.info("[ZXMM] 玩家刚进服 {} tick,推迟 {} ms 再踢: {}", waited, delayMs, player);
            CompletableFuture.delayedExecutor(delayMs, TimeUnit.MILLISECONDS)
                    .execute(() -> onServer(() -> kickNow(player, text)));
            return true;
        }

        return kickNow(player, text);
    }

    /** 真的断开连接(必须在服务器主线程调用)。 */
    private static boolean kickNow(String player, String reason) {
        ServerPlayer target = findPlayer(player);
        if (target == null) {
            return false;
        }

        String text = reason == null || reason.isEmpty() ? "被服务器移出" : reason;
        target.connection.disconnect(Component.literal(text));

        ZxmmMod.LOGGER.info("[ZXMM] 按 ZXMS 指令踢出 {}：{}", player, text);
        return true;
    }

    /** 在线玩家列表(逗号分隔)。 */
    private static String listPlayers() {
        String names = onServer(() -> {
            List<String> list = new ArrayList<>();
            for (ServerPlayer player : server.getPlayerList().getPlayers()) {
                list.add(player.getGameProfile().getName());
            }

            return String.join(",", list);
        });

        return names == null ? "" : names;
    }

    /** 玩家信息：名|维度|X|Y|Z；不在线给空串。 */
    private static String playerInfo(String player) {
        String info = onServer(() -> {
            ServerPlayer target = findPlayer(player);
            if (target == null) {
                return "";
            }

            return target.getGameProfile().getName()
                    + "|" + target.level().dimension().location()
                    + "|" + Math.round(target.getX())
                    + "|" + Math.round(target.getY())
                    + "|" + Math.round(target.getZ());
        });

        return info == null ? "" : info;
    }

    /** 全服广播。 */
    private static String say(String text) {
        Boolean done = onServer(() -> {
            server.getPlayerList().broadcastSystemMessage(Component.literal(text), false);
            return true;
        });

        return done == null ? "服务器主线程忙，没广播" : "已广播";
    }

    /** 玩家在不在线。 */
    private static boolean online(String player) {
        Boolean found = onServer(() -> findPlayer(player) != null);
        return Boolean.TRUE.equals(found);
    }

    /** 按名字找在线玩家(必须在服务器主线程调用)。 */
    private static ServerPlayer findPlayer(String name) {
        MinecraftServer mc = server;
        if (mc == null || name == null || name.isEmpty()) {
            return null;
        }

        return mc.getPlayerList().getPlayerByName(name);
    }

    /**
     * 把一段"碰 MC 状态"的活儿丢回服务器主线程执行。
     *
     * <p>玩家表、玩家坐标都是主线程独占的，从 API 线程直接读会撞上 ConcurrentModificationException；
     * 这里统一 {@code submit} 回主线程，最多等 3 秒。</p>
     */
    private static <T> T onServer(Supplier<T> task) {
        MinecraftServer mc = server;
        if (mc == null) {
            return null;
        }

        if (mc.isSameThread()) {
            return task.get();
        }

        try {
            return mc.submit(task).get(3, TimeUnit.SECONDS);
        } catch (Exception e) {
            ZxmmMod.LOGGER.warn("[ZXMM] 主线程任务没做完: {}", e.toString());
            return null;
        }
    }

    /** 往所有已连上的 ZXMS 写一行；一条都没写成功返回 false。 */
    private static boolean deliver(String line) {
        boolean any = false;

        for (Socket socket : LINKS) {
            try {
                OutputStream out = socket.getOutputStream();
                out.write((line + "\n").getBytes(StandardCharsets.UTF_8));
                out.flush();
                any = true;
            } catch (Exception e) {
                LINKS.remove(socket);
                ZxmmMod.LOGGER.warn("[ZXMM] 写专用通道失败，连接已移出: {}", e.toString());
                try {
                    socket.close();
                } catch (IOException ignored) {
                    // 已经断了
                }
            }
        }

        return any;
    }

}
