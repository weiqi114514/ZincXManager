using System.Security.Cryptography;
using System.Text;
using ZincXManagerShared.Account;

namespace ZincXManagerShared.Verify;

/// <summary>官方验证的结论(校验通过 / 哪一种不通过)。</summary>
public sealed class OfficialVerdict
{
    /// <summary>过了没有。</summary>
    public bool Ok { get; init; }

    /// <summary>不通过的原因代号(<c>ok</c> / <c>no-file</c> / <c>bad-format</c> / <c>bad-signature</c> / <c>machine</c> / <c>expired</c> / <c>no-key</c>)。</summary>
    public string Code { get; init; } = "ok";

    /// <summary>给人看的一句话(管理员自己看,不发给对端)。</summary>
    public string Detail { get; init; } = "";

    /// <summary>许可证上写的服务器名。</summary>
    public string Name { get; init; } = "";

    /// <summary>许可证绑定的机器码(小写十六进制)。</summary>
    public string Machine { get; init; } = "";

    /// <summary>序列号。</summary>
    public string Serial { get; init; } = "";

    /// <summary>到期时间(<see cref="DateTime.MinValue"/> = 永久)。</summary>
    public DateTime Expires { get; init; }

    /// <summary>
    /// 只有校验通过才有:由许可证算出来的 32 字节 pepper。
    /// 给"第二层"用 —— 把验证结果混进握手 / 运行逻辑,光把检查注释掉是没用的。
    /// </summary>
    public byte[] Pepper { get; init; } = [];

    /// <summary>通过校验的那张许可证原文(第二层要把它出示给对端)。</summary>
    public string LicenseText { get; init; } = "";
}

/// <summary>
/// ZXMOS 的官方(正版)验证:一张由官方私钥签发的 <c>official.lic</c>,绑定机器码、带有效期。
///
/// <para><b>信任模型(别指望混淆能挡住破解,要认清秘密在哪):</b></para>
/// <list type="number">
///   <item>签名用的 <b>私钥永不出厂</b> —— 只在你自己的签发工具里(<c>zxm-lic</c>);</item>
///   <item>程序里只内置 <b>公钥</b>(打散存放,见 <see cref="OfficialKey"/>),它只能<b>验签</b>,不能签发;</item>
///   <item>许可证绑定 <b>机器码</b> —— 拷到别的机器上验不过;</item>
///   <item>够狠的一步是"结果参与运行"(见 <see cref="OfficialVerdict.Pepper"/>) —— 光把 <c>if</c> 删掉,后面就处处对不上。</item>
/// </list>
///
/// <para>许可证是纯文本 <c>键=值</c>,一行一个字段,签名盖在"规范化后的负载"上,所以手改任何一个字段都会验签失败。</para>
/// </summary>
public static class OfficialLicense
{
    /// <summary>许可证文件名(放 exe 同目录)。</summary>
    public const string FileName = "official.lic";

    /// <summary>产品名(许可证第 1 段,防止 ZXMS 的证书拿来给 ZXMOS 用)。</summary>
    public const string Product = "ZXMOS";

    /// <summary>
    /// 规范化负载 —— <b>签发和校验必须用同一个函数</b>,不然签名对不上。
    /// 格式:<c>产品|名称|机器码|签发日|到期日|序列号</c>(机器码统一小写)。
    /// </summary>
    public static string Canonical(string product, string name, string machine, string issued, string expires, string serial)
        => string.Join('|', product.Trim(), name.Trim(), machine.Trim().ToLowerInvariant(),
            issued.Trim(), expires.Trim(), serial.Trim());

    /// <summary>用一张许可证 + 一把公钥 + 本机机器码做校验(自己验自己:机器码必须对)。</summary>
    public static OfficialVerdict Verify(string licenseText, RSA? key, string machine)
        => VerifyCore(licenseText, key, machine, true);

    /// <summary>
    /// 对端校验(第二层):只验"这张证是不是官方签发的 + 过没过期",**不比对机器码** ——
    /// 验证方根本不知道对方机器的机器码,那是服务端自己的事(它自己那关在启动时已经过了)。
    /// </summary>
    public static OfficialVerdict VerifyPeer(string licenseText, RSA? key)
        => VerifyCore(licenseText, key, "", false);

    private static OfficialVerdict VerifyCore(string licenseText, RSA? key, string machine, bool requireMachine)
    {
        if (key is null)
        {
            return new OfficialVerdict { Ok = false, Code = "no-key", Detail = "内置公钥读取失败" };
        }

        if (!TryRead(licenseText, out var fields))
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证格式不对(要 键=值 的行)" };
        }

        var product = Field(fields, "product");
        var name = Field(fields, "name");
        var licMachine = Field(fields, "machine");
        var issued = Field(fields, "issued");
        var expires = Field(fields, "expires");
        var serial = Field(fields, "serial");
        var signature = Field(fields, "sig");

        if (product.Length == 0 || licMachine.Length == 0 || signature.Length == 0)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证缺字段(product / machine / sig 必须有)" };
        }

        if (!string.Equals(product, Product, StringComparison.OrdinalIgnoreCase))
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = $"许可证是给 {product} 的,不是 {Product}" };
        }

        var payload = Canonical(product, name, licMachine, issued, expires, serial);

        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signature);
        }
        catch
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "sig 不是合法的 Base64" };
        }

        var signed = false;
        try
        {
            signed = key.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            signed = false;
        }

        if (!signed)
        {
            // 签名不对 = 有人改过字段 / 自己拿别的钥匙签的
            return new OfficialVerdict { Ok = false, Code = "bad-signature", Detail = "签名对不上(许可证被改过,或者不是官方签发的)" };
        }

        // 机器码:绑机器,拷走没用(对端校验时跳过这一步 —— 验证方不知道对方机器的机器码)
        var here = MachineCode.Value.Trim().ToLowerInvariant();
        if (requireMachine && !string.Equals(licMachine.Trim().ToLowerInvariant(), here, StringComparison.OrdinalIgnoreCase))
        {
            return new OfficialVerdict
            {
                Ok = false,
                Code = "machine",
                Detail = $"许可证是给另一台机器的(证上 {Short(licMachine)} / 本机 {(machine.Length > 0 ? Short(machine) : Short(here))})",
                Name = name,
                Machine = licMachine,
                Serial = serial,
            };
        }

        // 有效期:空 = 永久
        var expiresAt = DateTime.MinValue;
        if (expires.Length > 0 && DateTime.TryParse(expires, out var parsed))
        {
            expiresAt = parsed.Date.AddDays(1).AddSeconds(-1);   // 到期日当天还能用
            if (DateTime.Now > expiresAt)
            {
                return new OfficialVerdict
                {
                    Ok = false,
                    Code = "expired",
                    Detail = $"许可证已于 {parsed:yyyy-MM-dd} 到期",
                    Name = name,
                    Machine = licMachine,
                    Serial = serial,
                    Expires = expiresAt,
                };
            }
        }

        return new OfficialVerdict
        {
            Ok = true,
            Code = "ok",
            Detail = "官方验证通过",
            Name = name,
            Machine = licMachine,
            Serial = serial,
            Expires = expiresAt,
            Pepper = PepperOf(sig, name, licMachine, serial),
            LicenseText = licenseText,
        };
    }

    /// <summary>
    /// 本机自检:默认读工作目录下的 <c>official.lic</c>(可用环境变量 <c>ZXM_LICENSE</c> 指别的路径)。
    /// </summary>
    public static OfficialVerdict Check(string? directory = null)
    {
        var path = Environment.GetEnvironmentVariable("ZXM_LICENSE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(directory ?? Directory.GetCurrentDirectory(), FileName);
        }

        try
        {
            if (!File.Exists(path))
            {
                return new OfficialVerdict
                {
                    Ok = false,
                    Code = "no-file",
                    Detail = $"没找到许可证 {path}(把官方签发的 {FileName} 放到 exe 同目录)",
                };
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            using var key = OfficialKey.Create();
            return Verify(text, key, MachineCode.Value);
        }
        catch (Exception ex)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "读许可证失败:" + ex.Message };
        }
    }

    /// <summary>把签名 + 许可信息搅成 pepper(第二层校验用;换了证就换 pepper)。</summary>
    private static byte[] PepperOf(byte[] sig, string name, string machine, string serial)
    {
        var seed = Encoding.UTF8.GetBytes(Canonical(Product, name, machine, "", "", serial));
        var mixed = new byte[seed.Length + sig.Length];
        seed.CopyTo(mixed, 0);
        Array.Copy(sig, 0, mixed, seed.Length, sig.Length);
        return SHA256.HashData(mixed);
    }

    /// <summary>读 键=值 的行;<c>#</c> 开头的行和空行跳过,<c>[节]</c> 也跳过。</summary>
    private static bool TryRead(string text, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            var cut = line.IndexOf('=');
            if (cut <= 0)
            {
                continue;
            }

            fields[line[..cut].Trim()] = line[(cut + 1)..].Trim();
        }

        return fields.Count > 0;
    }

    private static string Field(Dictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : "";

    private static string Short(string machine)
        => machine.Length > 8 ? machine[..8] + "…" : machine;
}
