package com.zincxmanager.zxmm;

import java.net.InetSocketAddress;              //导入InetSocketAddress(取玩家远端地址)
import java.net.SocketAddress;                  //导入SocketAddress(同上,拿来判类型)

import com.mojang.logging.LogUtils;             //导入LogUtils(取 logger)

import net.minecraft.server.level.ServerPlayer;                                    //导入ServerPlayer(进服事件里的玩家)
import net.neoforged.bus.api.IEventBus;                                             //导入IEventBus(mod 总线)
import net.neoforged.bus.api.SubscribeEvent;                                       //导入SubscribeEvent(事件订阅注解)
import net.neoforged.fml.common.Mod;                                               //导入Mod(@Mod 注解)
import net.neoforged.neoforge.common.NeoForge;                                     //导入NeoForge(事件总线)
import net.neoforged.neoforge.event.entity.player.PlayerEvent;                      //导入PlayerEvent(含进服事件)
import net.neoforged.neoforge.event.server.ServerStartedEvent;                      //导入ServerStartedEvent(服务器起来了)
import net.neoforged.neoforge.event.server.ServerStartingEvent;                     //导入ServerStartingEvent(服务器启动中)

import org.slf4j.Logger;                        //导入Logger(日志接口)

/**
 * ZXMM：**只做两件事** —— 把"玩家进服"报给 ZXMS，和给 ZXMS 提供执行接口。
 *
 * <p>这里**没有任何判断和流程**：不查表、不缓存、不决定放不放行。
 * 登录数组、持久登录表、绑定关系、踢不踢人，全在 ZXMS 那边。</p>
 *
 * <ul>
 *   <li>进服事件 → 走 21200 的长连接推一条 {@code evt|joined|玩家|IP}，发完就不管了</li>
 *   <li>ZXMS 连 21200 → 调 kick / list / info / say / online / ping 这些执行函数</li>
 * </ul>
 */
@Mod(ZxmmMod.MODID)
public final class ZxmmMod {

    /** 模组 id。 */
    public static final String MODID = "zxmm";

    /** 用户可见的完整版本号(带 b 前缀；toml 里的机器版是纯数字 0.3.2)。 */
    public static final String VERSION = "b0.3.2a";

    /** 日志。 */
    public static final Logger LOGGER = LogUtils.getLogger();

    public ZxmmMod(IEventBus modEventBus) {
        ZxmmConfig.load();
        NeoForge.EVENT_BUS.register(this);
        modEventBus.register(LoginCheckFeature.class);   // configuration 阶段任务走 mod 总线
        LOGGER.info("[ZXMM] 已加载 {}，专用通道 {}:{}，校验{}，令牌{}",
                VERSION, ZxmmConfig.host, ZxmmConfig.port,
                ZxmmConfig.enabled ? "开" : "关",
                ZxmmConfig.token.isEmpty() ? "未启用" : "已启用");
    }

    /** 服务器启动：重读配置(改完配置重启即可生效)。 */
    @SubscribeEvent
    public void onServerStarting(ServerStartingEvent event) {
        ZxmmConfig.load();
        LOGGER.info("[ZXMM] 服务端启动 {}，专用通道 {}:{}", VERSION, ZxmmConfig.host, ZxmmConfig.port);
    }

    /** 服务器起来了：把 MinecraftServer 交给执行接口，开始听 ZXMS 的调用。 */
    @SubscribeEvent
    public void onServerStarted(ServerStartedEvent event) {
        ZxmmApi.attach(event.getServer());
        ZxmmApi.start(ZxmmConfig.host, ZxmmConfig.port, ZxmmConfig.token);
    }

    /**
     * 玩家进服：**只上报事件**，不做任何判断。
     *
     * <p>ZXMS 收到后会自己查登录数组 / 持久表 / 绑定关系，该放行就不动，
     * 该踢就通过执行接口调 <c>kick</c>。</p>
     */
    @SubscribeEvent
    public void onPlayerLogin(PlayerEvent.PlayerLoggedInEvent event) {
        if (!(event.getEntity() instanceof ServerPlayer player)) {
            return;
        }

        if (!ZxmmConfig.enabled) {
            return;
        }

        String name = player.getGameProfile().getName();
        String ip = remoteIp(player);

        LOGGER.info("[ZXMM] 上报玩家进服: {} ({})", name, ip);
        ZxmmApi.pushJoined(name, ip);
    }

    /**
     * 取玩家远端 IP。
     *
     * <p>只留纯 IP：ZXMS 要拿它查归属地、写持久登录表，
     * {@code /127.0.0.1:51234} 这种带端口和斜杠的串在那边一句都认不出来。</p>
     */
    private static String remoteIp(ServerPlayer player) {
        try {
            SocketAddress address = player.connection.getRemoteAddress();
            if (address instanceof InetSocketAddress inet && inet.getAddress() != null) {
                return inet.getAddress().getHostAddress();
            }

            return address == null ? "unknown" : address.toString();
        } catch (Throwable ignored) {
            return "unknown";
        }
    }
}
