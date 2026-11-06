package com.zincxmanager.zxmm;

import java.net.InetSocketAddress;                 //导入InetSocketAddress(取玩家 IP)
import java.util.concurrent.CompletableFuture;     //导入CompletableFuture(异步检查)
import java.util.concurrent.TimeUnit;              //导入TimeUnit(等 ZXMS 的秒)
import java.util.concurrent.TimeoutException;      //导入TimeoutException(没结论就放行)
import java.util.function.Consumer;                //导入Consumer(任务回调)

import net.minecraft.network.chat.Component;       //导入Component(踢人理由)
import net.minecraft.network.protocol.Packet;      //导入Packet(任务回包类型)
import net.minecraft.server.network.ConfigurationTask;                      //导入ConfigurationTask(配置阶段任务)
import net.minecraft.server.network.ServerConfigurationPacketListenerImpl;  //导入配置阶段连接
import net.neoforged.bus.api.SubscribeEvent;       //导入SubscribeEvent(事件订阅)
import net.neoforged.neoforge.network.event.RegisterConfigurationTasksEvent; //导入配置任务注册事件

/**
 * configuration 阶段的登录检查(和 EasyBot 一个路子):
 * 玩家还在「正在加入世界」时就问 ZXMS,不许进就断开登录连接,客户端会显示理由。
 *
 * <p>这里只做「问一句 + 照做」,判断全在 ZXMS;ZXMS 没连上或 3 秒没结论就放行,
 * 交给进服后的原有流程兜底。</p>
 */
public final class LoginCheckFeature {

    /** 任务类型 id。 */
    private static final ConfigurationTask.Type TASK_TYPE =
            new ConfigurationTask.Type(ZxmmMod.MODID + ":login_check");

    /** 最多等 ZXMS 多久(秒):等不到就放行。 */
    private static final long WAIT_SECONDS = 3;

    private LoginCheckFeature() {
    }

    /** 每条连接进 configuration 阶段时登记一个检查任务。 */
    @SubscribeEvent
    public static void onRegisterTasks(RegisterConfigurationTasksEvent event) {
        if (!ZxmmConfig.enabled) {
            return;
        }

        if (event.getListener() instanceof ServerConfigurationPacketListenerImpl listener) {
            event.register(new CheckTask(listener));
        }
    }

    /** 一次登录检查。 */
    private record CheckTask(ServerConfigurationPacketListenerImpl listener) implements ConfigurationTask {

        @Override
        public void start(Consumer<Packet<?>> consumer) {
            CompletableFuture.runAsync(() -> {
                try {
                    if (!checkLogin()) {
                        return;   // 已经在 checkLogin 里断开,别再继续配置流程
                    }

                    listener.finishCurrentTask(TASK_TYPE);
                } catch (Exception e) {
                    ZxmmMod.LOGGER.warn("[ZXMM] 连接阶段检查异常: {}", e.toString());
                    listener.disconnect(Component.literal("账号验证服务异常，请稍后再试"));
                }
            });
        }

        /** 问 ZXMS:true = 放行,false = 已断开。 */
        private boolean checkLogin() throws Exception {
            if (!ZxmmApi.hasLink()) {
                // 与 ZXMS 断链时按「放行」处理:这是 2026-11-01 由项目负责人拍板的有意取舍,
                // 理由是 ZXMS 单点重启期间不能让全服玩家进不来。代价是断链期间进服不验证,
                // 所以这里必须留 Warn 级别痕迹,别让运维以为一直在验证。
                var offlineProfile = listener.getOwner();
                ZxmmMod.LOGGER.warn("[ZXMM] 与 ZXMS 断链,本次进服未验证就放行: {}",
                        offlineProfile == null ? "(未知玩家)" : offlineProfile.getName());
                return true;   // ZXMS 都没连上,别让玩家白等 3 秒
            }

            var profile = listener.getOwner();
            if (profile == null) {
                return true;
            }

            String name = profile.getName();
            var address = listener.getConnection().getRemoteAddress();
            String ip = address instanceof InetSocketAddress inet && inet.getAddress() != null
                    ? inet.getAddress().getHostAddress()
                    : "unknown";

            var future = ZxmmApi.beginLoginWait(name);
            try {
                ZxmmApi.pushJoining(name, ip);

                // 有结论 = 不许进(理由由 ZXMS 给);超时 = 放行
                String reason = future.get(WAIT_SECONDS, TimeUnit.SECONDS);
                if (reason != null && !reason.isEmpty()) {
                    listener.disconnect(Component.literal(reason));
                    return false;
                }
            } catch (TimeoutException ignored) {
                // ZXMS 没给结论:放行,进服后的流程会兜底
            } finally {
                ZxmmApi.endLoginWait(name);
            }

            return true;
        }

        @Override
        public ConfigurationTask.Type type() {
            return TASK_TYPE;
        }
    }
}
