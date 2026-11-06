package com.zincxmanager.zxmm;

import java.io.IOException;                 //导入IOException(读写配置失败)
import java.io.InputStream;                 //导入InputStream(读配置文件)
import java.nio.charset.StandardCharsets;   //导入StandardCharsets(UTF_8)
import java.nio.file.Files;                 //导入Files(读/写/建目录)
import java.nio.file.Path;                  //导入Path(配置路径)
import java.util.List;                      //导入List(废弃键列表)
import java.util.Properties;                //导入Properties(键值容器)

/**
 * 模组配置：放在服务端目录的 <c>config/zxmm.properties</c>。
 *
 * <p>只保留真正会生效的键。以前那几个 {@code zxms.host} / {@code zxms.port} /
 * {@code check.remember} 从来没被读过（ZXMM 不往外连、登录数组在 ZXMS 那边），已删除。</p>
 */
public final class ZxmmConfig {

    /** 配置文件位置(相对服务端工作目录)。 */
    private static final Path FILE = Path.of("config", "zxmm.properties");

    /** 已经废弃的旧键：读到就从文件里删掉。 */
    private static final List<String> OBSOLETE = List.of(
            "zxms.host", "zxms.port", "check.remember", "check.failopen", "check.grace");

    /** ZXMS 连进来的地址。默认只听本机，别改 0.0.0.0。 */
    public static String host = "127.0.0.1";

    /** ZXMS 连进来的端口。 */
    public static int port = ZxmmApi.DEFAULT_PORT;

    /** 和 ZXMS 的共享令牌；空 = 不校验。 */
    public static String token = "";

    /** 总开关：false = 完全不校验(调试用)。 */
    public static boolean enabled = true;

    private ZxmmConfig() {
    }

    /** 读配置；缺项补默认值并写回文件(不覆盖已改过的值)。 */
    public static void load() {
        Properties props = new Properties();
        boolean existed = Files.exists(FILE);

        if (existed) {
            try (InputStream in = Files.newInputStream(FILE)) {
                props.load(in);
            } catch (IOException e) {
                ZxmmMod.LOGGER.warn("[ZXMM] 读配置失败,用默认值: {}", e.toString());
                return;
            }
        }

        host = text(props, "api.host", host);
        port = clamp(number(props, "api.port", port), port);
        token = text(props, "api.token", token);
        enabled = flag(props, "check.enabled", enabled);

        // 文件缺失 / 有旧键 / 还没有新键：重写一份带注释的规范文件
        if (!existed || !props.containsKey("api.token") || OBSOLETE.stream().anyMatch(props::containsKey)) {
            save();
        }
    }

    /** 把当前值写成带注释的配置文件(可读中文，不用 Properties.store 的转义)。 */
    private static void save() {
        String text = """
                # ZXMM 配置：ZXMS 连哪里、断线时怎么处理、要不要校验令牌
                # 改完重启服务器生效；文件只在这里补全，不会覆盖你手改过的值

                # ZXMS 连进来的地址。默认只允许本机；改成 0.0.0.0 等于把「踢人」接口开到公网
                api.host=%s
                api.port=%d

                # 和 ZXMS 的共享令牌：两边都填才校验，空 = 不校验
                api.token=%s

                # 总开关：false = 完全不校验(调试用)
                check.enabled=%s
                """.formatted(host, port, token, enabled);

        try {
            Files.createDirectories(FILE.getParent());
            Files.writeString(FILE, text, StandardCharsets.UTF_8);
        } catch (IOException e) {
            ZxmmMod.LOGGER.warn("[ZXMM] 写配置失败,下次启动还会用默认值: {}", e.toString());
        }
    }

    /** 读文本键。 */
    private static String text(Properties props, String key, String fallback) {
        String value = props.getProperty(key);
        return value == null || value.isBlank() ? fallback : value.trim();
    }

    /** 读整数键，读不出就给兜底值。 */
    private static int number(Properties props, String key, int fallback) {
        try {
            return Integer.parseInt(props.getProperty(key, String.valueOf(fallback)).trim());
        } catch (Exception e) {
            return fallback;
        }
    }

    /** 读布尔键。 */
    private static boolean flag(Properties props, String key, boolean fallback) {
        String value = props.getProperty(key);
        return value == null || value.isBlank() ? fallback : Boolean.parseBoolean(value.trim());
    }

    /** 端口越界就用兜底值(0/负数会让 bind 直接失败)。 */
    private static int clamp(int value, int fallback) {
        return value >= 1 && value <= 65535 ? value : fallback;
    }
}
