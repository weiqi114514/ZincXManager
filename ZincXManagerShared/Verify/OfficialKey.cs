// System:Convert 做 base64 还原
using System;  // 基础类型 / 常用静态方法
// System.Collections.Generic:逐段还原公钥时用 List<byte>
using System.Collections.Generic;  // 泛型集合
// System.Text:公钥是 UTF-8 文本,还原后按 UTF-8 拼回
using System.Text;  // 文本编码与 StringBuilder

namespace ZincXManagerShared.Verify;

internal static class OfficialKey
{
    // 打散公钥用的异或种子。
    private const byte Seed = 0x5B;
    // 还原后的公钥缓存。
    private static string? _cached;

    // 打散后的公钥分片。
    private static readonly string[] Parts =
    [
        "Fh0wLB4sAhMQNAESITFrGBoKAhIQNAESITFrHxoKOB8=",
        "CjwaHgJ0AwguYytwKDERDTVtDjEyYmlrai9rcDBtGg0=",
        "PTBtIgsaKwkQL3Q1bCErGAMSFggCOTgScGgULgFwFwE=",
        "Mjo3CT80AxweMAxvGiFjDmhuCDAvEDIRLSxmZg==",
    ];

    /// <summary>还原内置公钥文本(逐段 base64 解码后异或还原)。</summary>
    public static string Create()
    {
        if (_cached is not null) { return _cached; }

        var bytes = new List<byte>();
        foreach (var part in Parts)
        {
            foreach (var b in Convert.FromBase64String(part)) { bytes.Add((byte)(b ^ Seed)); }
        }

        _cached = Encoding.UTF8.GetString(bytes.ToArray());
        return _cached;
    }
}
