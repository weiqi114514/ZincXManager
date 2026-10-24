using System;  // 基础类型 / 常用静态方法
using System.Collections.Generic;  // 泛型集合
using System.IO;  // 文件与目录读写
using System.Text;  // 文本编码与 StringBuilder
using System.Text.Json;  // JSON 序列化
using ZincXManagerClient.Models;  // 客户端自身
using ZincXManagerShared.Logging;  // 共享库
using ZincXManagerShared.Network;  // 共享库

namespace ZincXManagerClient;

/// <summary>
/// 本地消息存档:收到的公告 / 推送存进 <c>messages.json</c>,下次开客户端还在(和 config.ini 同一个目录)。
///
/// <para><b>保留期按来源分三档:</b></para>
/// <list type="bullet">
///   <item><c>ZXMOS-…</c>(官方服务端):固定最多 <see cref="ZxmosKeepDays"/> 天,客户端不能改</item>
///   <item><c>ZXMS-…</c>(子服务端):按子服务端的设置(它连上时用 202 配置包告诉客户端,默认 7 天)</item>
///   <item><c>ZXMC</c>(客户端本地产的):按高级设置里的天数(默认 60 天)</item>
/// </list>
/// </summary>
internal static class MessageStore
{
    /// <summary>存档文件名(相对客户端工作目录)。</summary>
    public const string FilePath = "messages.json";

    /// <summary>官方(ZXMOS)消息的保留上限(天):服务端策略,客户端不可改。</summary>
    public const int ZxmosKeepDays = 20;

    /// <summary>子服务端消息的默认保留天数(没收到 202 时用)。</summary>
    public const int DefaultZxmsKeepDays = 7;

    /// <summary>本地消息的默认保留天数。</summary>
    public const int DefaultLocalKeepDays = 60;

    /// <summary>这条来源该留多少天。</summary>
    public static int KeepDaysOf(string sourceTag, int zxmsDays, int localDays)
    {
        if (sourceTag.StartsWith("ZXMOS", StringComparison.OrdinalIgnoreCase))
        {
            return ZxmosKeepDays;
        }

        if (sourceTag.StartsWith("ZXMS", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(zxmsDays, 1, 365);
        }

        return Math.Clamp(localDays, 1, 3650);
    }

    /// <summary>读存档(没有文件 / 读坏了都返回空表)。</summary>
    public static List<MessageItem> Load()
    {
        var list = new List<MessageItem>();

        if (!File.Exists(FilePath))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath, Encoding.UTF8));

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var options = new List<string>();
                if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        var text = o.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            options.Add(text!);
                        }
                    }
                }

                var raw = Text(item, "raw");
                DateTime.TryParse(Text(item, "receivedAt"), out var receivedAt);
                if (receivedAt == default)
                {
                    receivedAt = DateTime.Now;
                }

                var serverId = item.TryGetProperty("serverId", out var sid) && sid.TryGetInt32(out var sn) ? sn : 0;

                // 老存档(升级前存的)没有 serverId:从卡片原文第 8 段补回来,不然补发去重认不出这条
                if (serverId <= 0 && raw.Length > 0 && MessageCard.TryParse(raw, out var parsedCard))
                {
                    serverId = parsedCard.Id;
                }

                list.Add(new MessageItem
                {
                    Id = item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0,
                    ServerId = serverId,
                    Kind = ParseKind(Text(item, "kind")),
                    Title = Text(item, "title"),
                    Body = Text(item, "body"),
                    Source = Text(item, "source"),
                    SourceTag = Text(item, "sourceTag"),
                    RawCard = raw,
                    ReceivedAt = receivedAt,
                    KeepDays = item.TryGetProperty("keepDays", out var k) && k.TryGetInt32(out var kd) ? kd : DefaultLocalKeepDays,
                    PrimaryAction = Text(item, "primary"),
                    SecondaryAction = Text(item, "secondary"),
                    Options = options,
                    SelectedOption = options.Count > 0 ? options[0] : "",
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 读本地消息存档失败,按空表处理: " + ex.Message);
        }

        return list;
    }

    /// <summary>写存档(手写 JSON,不依赖反射,AOT 安全)。</summary>
    public static void Save(IEnumerable<MessageItem> items)
    {
        try
        {
            var json = new StringBuilder("[\n");
            var index = 0;
            var list = new List<MessageItem>(items);

            foreach (var m in list)
            {
                json.Append("  {\"id\":").Append(m.Id)
                    .Append(",\"serverId\":").Append(m.ServerId)
                    .Append(",\"kind\":\"").Append(Escape(KindText(m.Kind))).Append('"')
                    .Append(",\"title\":\"").Append(Escape(m.Title)).Append('"')
                    .Append(",\"body\":\"").Append(Escape(m.Body)).Append('"')
                    .Append(",\"source\":\"").Append(Escape(m.Source)).Append('"')
                    .Append(",\"sourceTag\":\"").Append(Escape(m.SourceTag)).Append('"')
                    .Append(",\"primary\":\"").Append(Escape(m.PrimaryAction)).Append('"')
                    .Append(",\"secondary\":\"").Append(Escape(m.SecondaryAction)).Append('"')
                    .Append(",\"keepDays\":").Append(m.KeepDays)
                    .Append(",\"receivedAt\":\"").Append(m.ReceivedAt.ToString("o")).Append('"')
                    .Append(",\"raw\":\"").Append(Escape(m.RawCard)).Append('"')
                    .Append(",\"options\":[");

                for (var i = 0; i < m.Options.Count; i++)
                {
                    json.Append('"').Append(Escape(m.Options[i])).Append(i == m.Options.Count - 1 ? "\"" : "\",");
                }

                json.Append("]}");
                json.Append(++index == list.Count ? "\n" : ",\n");
            }

            json.Append(']');
            File.WriteAllText(FilePath, json.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Log(LogLevel.Warning, "[ZXMC] 保存本地消息存档失败: " + ex.Message);
        }
    }

    /// <summary>内部类型代号 → 显示用的类型名。</summary>
    private static string KindText(MessageKind kind) => kind switch
    {
        MessageKind.Update => MessageCard.Update,
        MessageKind.Choice => MessageCard.Choice,
        MessageKind.Input => MessageCard.Input,
        MessageKind.Select => MessageCard.Select,
        _ => MessageCard.Text,
    };

    /// <summary>显示用的类型名 → 内部类型代号。</summary>
    private static MessageKind ParseKind(string kind) => kind switch
    {
        MessageCard.Update => MessageKind.Update,
        MessageCard.Choice => MessageKind.Choice,
        MessageCard.Input => MessageKind.Input,
        MessageCard.Select => MessageKind.Select,
        _ => MessageKind.Text,
    };

    /// <summary>取消息的纯文本正文(不带格式段)。</summary>
    private static string Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>把文本里的分隔符转义,存盘后还能原样还原。</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}