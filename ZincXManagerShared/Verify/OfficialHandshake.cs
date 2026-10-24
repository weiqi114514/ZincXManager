// System.Security.Cryptography:HMACSHA256(证明里的防重放校验)与随机 nonce
using System.Security.Cryptography;  // 加密与哈希
// System.Text:UTF-8 编解码(证明里的许可证原文)
using System.Text;  // 文本编码与 StringBuilder

namespace ZincXManagerShared.Verify;

/// <summary>
/// 官方验证<b>第二层</b>的握手(协议 <c>520 OfficialChallenge</c> / <c>521 OfficialProof</c>)。
///
/// <para><b>流程:</b>验证方(官方发行的 ZXMS / ZXMC)在登录成功之后发一个一次性 nonce;
/// 对方(ZXMOS)回 <c>Base64(许可证原文)|nonce|HMAC-SHA256(pepper, nonce)</c>。</para>
///
/// <list type="number">
///   <item><b>验签</b> —— 这张证必须是用官方私钥签的(验证方只有公钥,伪造不出来),
///         所以"自己拿源码重编译一个不带 OFFICIAL_BUILD 的 ZXMOS"拿不出证;</item>
///   <item><b>nonce</b> —— 必须回我这次发的那个,拿着上次的证明重放没用;</item>
///   <item><b>HMAC</b> —— pepper 由许可证本身算出来,证明对方是**真的过了第一层校验**
///         (而不是把一段证文抄过来);</item>
///   <item>只有<b>已经登录成功</b>的连接才给证明(不然谁都能来收集许可证)。</item>
/// </list>
///
/// <para><b>老实说</b>:这一层挡住的是"随手重编译/拷个 exe 就开服";对方若拿一张真的证 +
/// 自己照抄 HMAC 算法,离线还是能骗过 —— 那要靠第三层(在线短时令牌 + 吊销)。</para>
/// </summary>
public static class OfficialHandshake
{
    /// <summary>证明负载里的段分隔符(许可证是 键=值 文本,本身没有 |,所以安全)。</summary>
    public const char Separator = '|';

    /// <summary>造一个一次性 nonce(32 位十六进制)。</summary>
    public static string NewNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// ZXMOS 侧:用本机已验过的许可证 + 收到的 nonce 拼出证明。
    /// 拿不出来时返回 <c>err|&lt;原因代号&gt;</c>(对端据此知道"对方给不出官方证明")。
    /// </summary>
    public static string BuildProof(string nonce)
    {
        var verdict = OfficialGuard.Last;
        if (!verdict.Ok)
        {
            return "err|" + (verdict.Code.Length > 0 ? verdict.Code : "not-verified");
        }

        if (verdict.LicenseText.Length == 0)
        {
            return "err|no-license-text";
        }

        var mac = Mac(verdict.Pepper, nonce);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(verdict.LicenseText))
               + Separator + nonce
               + Separator + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>验证方:校验对方给的证明。<paramref name="expectedNonce"/> = 我这次发出去的那个。</summary>
    public static OfficialVerdict Verify(string payload, string expectedNonce)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Bad("empty", "对方没有给出证明");
        }

        if (payload.StartsWith("err|", StringComparison.OrdinalIgnoreCase))
        {
            var code = payload[4..].Trim();
            return Bad(code.Length > 0 ? code : "err",
                code == "development"
                    ? "对方是开发构建,没有官方许可证"
                    : "对方拿不出官方证明(" + code + ")");
        }

        var parts = payload.Split(Separator);
        if (parts.Length != 3)
        {
            return Bad("bad-format", "证明格式不对");
        }

        var nonce = parts[1].Trim();
        var macHex = parts[2].Trim();

        // ① 先看 nonce:不是我这次发的就直接拒(挡住"拿上次的证明来重放")
        if (!string.Equals(nonce, expectedNonce, StringComparison.OrdinalIgnoreCase))
        {
            return Bad("stale-nonce", "证明里的 nonce 不是这次要的那个(可能在重放旧证明)");
        }

        string licenseText;
        try
        {
            licenseText = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        }
        catch
        {
            return Bad("bad-format", "证明里的许可证不是合法 Base64");
        }

        // ② 验签:必须是用官方私钥签发的许可证(验证方只有公钥,伪造不出来)
        var verdict = OfficialLicense.VerifyPeer(licenseText, OfficialKey.Create());
        if (!verdict.Ok)
        {
            return verdict;
        }

        // ③ HMAC:证明对方真的过了第一层校验(靠 pepper),而不是把一段证文抄过来
        byte[] presented;
        try
        {
            presented = Convert.FromHexString(macHex);
        }
        catch
        {
            return Bad("bad-format", "证明里的 HMAC 不是合法十六进制");
        }

        var want = Mac(verdict.Pepper, nonce);
        if (presented.Length != want.Length || !CryptographicOperations.FixedTimeEquals(presented, want))
        {
            return Bad("bad-mac", "证明里的 HMAC 对不上(对方没有真正通过第一层校验)");
        }

        return new OfficialVerdict
        {
            Ok = true,
            Code = "ok",
            Detail = "对端官方验证通过",
            Name = verdict.Name,
            KeyHash = verdict.KeyHash,
            Serial = verdict.Serial,
            Expires = verdict.Expires,
            Pepper = verdict.Pepper,
            LicenseText = licenseText,
        };
    }

    /// <summary>用 pepper + nonce 算 HMAC-SHA256。</summary>
    private static byte[] Mac(byte[] pepper, string nonce)
        => HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(nonce));

    /// <summary>构造一个"不通过"的结论。</summary>
    private static OfficialVerdict Bad(string code, string detail)
        => new() { Ok = false, Code = code, Detail = detail };
}
