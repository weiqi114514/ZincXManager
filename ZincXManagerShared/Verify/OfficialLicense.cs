// System:DateTime / Guid / Environment 这些基础类型
using System;  // 基础类型 / 常用静态方法
// System.Collections.Generic:签发时组织 ProductFeatures 的字典
using System.Collections.Generic;  // 泛型集合
// System.IO:读 official.lic / official.key
using System.IO;  // 文件与目录读写
// System.Linq:把校验链结果转成列表
using System.Linq;  // LINQ 查询
// System.Security.Cryptography:SHA-256(授权码指纹与 pepper)
using System.Security.Cryptography;  // 加密与哈希
// System.Text:UTF-8 编解码
using System.Text;  // 文本编码与 StringBuilder
// Standard.Licensing:License 对象与签名/校验入口
using Standard.Licensing;  // 许可证对象与签名
// Standard.Licensing.Validation:ExpirationDate / Signature 校验链与失败类型
using Standard.Licensing.Validation;  // 许可证校验链

namespace ZincXManagerShared.Verify;

/// <summary>官方验证的结论(校验通过 / 哪一种不通过)。</summary>
public sealed class OfficialVerdict
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "ok";
    public string Detail { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>许可证绑定的授权码哈希(小写十六进制)。</summary>
    public string KeyHash { get; init; } = "";
    public string Serial { get; init; } = "";
    public DateTime Expires { get; init; }
    public byte[] Pepper { get; init; } = [];
    public string LicenseText { get; init; } = "";
}

/// <summary>
/// ZXMOS 官方验证:ECDSA 签名的许可证(Standard.Licensing,XML),绑定<b>授权码</b>而不是机器码。
/// 授权码放 exe 同目录的 official.key,许可证里只存它的 SHA-256;换机器不用重签。
/// </summary>
public static class OfficialLicense
{
    public const string FileName = "official.lic";
    public const string KeyFileName = "official.key";
    public const string Product = "ZXMOS";

    // 许可证 features 键:产品名。
    private const string FProduct = "product";
    // 许可证 features 键:授权码哈希。
    private const string FKey = "keyhash";
    // 许可证 features 键:序列号。
    private const string FSerial = "serial";
    // 许可证 features 键:签发日期。
    private const string FIssued = "issued";
    // 许可证 features 键:服务器名。
    private const string FName = "name";

    /// <summary>授权码 → 许可证里存的哈希。</summary>
    public static string HashKey(string licenseKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(licenseKey.Trim()))).ToLowerInvariant();

    /// <summary>本机自检:官方公钥 + 授权码都比对。</summary>
    public static OfficialVerdict Verify(string licenseText, string publicKey, string licenseKey)
        => VerifyCore(licenseText, publicKey, licenseKey, requireKey: true);

    /// <summary>对端校验:只验签名与有效期,不知道对方授权码。</summary>
    public static OfficialVerdict VerifyPeer(string licenseText, string publicKey)
        => VerifyCore(licenseText, publicKey, "", requireKey: false);

    private static OfficialVerdict VerifyCore(string licenseText, string publicKey, string licenseKey, bool requireKey)
    {
        if (string.IsNullOrWhiteSpace(licenseText))
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证是空的" };
        }

        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return new OfficialVerdict { Ok = false, Code = "no-key", Detail = "内置公钥读取失败" };
        }

        License lic;
        try
        {
            lic = License.Load(licenseText);
        }
        catch (Exception ex)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证解析失败:" + ex.Message };
        }

        IReadOnlyList<IValidationFailure> failures;
        try
        {
            failures = lic.Validate()
                .ExpirationDate(DateTime.Now)
                .And()
                .Signature(publicKey)
                .AssertValidLicense()
                .ToList();
        }
        catch (Exception ex)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证校验失败:" + ex.Message };
        }

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                if (failure is LicenseExpiredValidationFailure)
                {
                    return new OfficialVerdict
                    {
                        Ok = false,
                        Code = "expired",
                        Detail = "许可证已于 " + lic.Expiration.ToString("yyyy-MM-dd") + " 到期",
                        Name = Field(lic, FName),
                        Serial = Field(lic, FSerial),
                        Expires = lic.Expiration,
                    };
                }
            }

            return new OfficialVerdict
            {
                Ok = false,
                Code = "bad-signature",
                Detail = "签名对不上(许可证被改过,或者不是官方签发的)",
            };
        }

        var product = Field(lic, FProduct);
        var keyHash = Field(lic, FKey);
        var serial = Field(lic, FSerial);
        var name = Field(lic, FName);

        if (product.Length == 0 || keyHash.Length == 0)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证缺字段(product / keyhash 必须有)" };
        }

        if (!string.Equals(product, Product, StringComparison.OrdinalIgnoreCase))
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "许可证是给 " + product + " 的,不是 " + Product };
        }

        if (requireKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                return new OfficialVerdict
                {
                    Ok = false,
                    Code = "key-missing",
                    Detail = "没找到授权码 " + KeyFileName + "(和 official.lic 放同一个目录)",
                    Name = name,
                    Serial = serial,
                    Expires = lic.Expiration,
                };
            }

            if (!string.Equals(HashKey(licenseKey), keyHash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new OfficialVerdict
                {
                    Ok = false,
                    Code = "key",
                    Detail = "授权码和许可证不匹配(这份 official.lic 不是给这个授权码签的)",
                    Name = name,
                    Serial = serial,
                    Expires = lic.Expiration,
                };
            }
        }

        return new OfficialVerdict
        {
            Ok = true,
            Code = "ok",
            Detail = "许可证有效",
            Name = name,
            KeyHash = keyHash.Trim(),
            Serial = serial,
            Expires = lic.Expiration,
            Pepper = PepperOf(licenseText),
            LicenseText = licenseText,
        };
    }

    /// <summary>取许可证 features 里的一个字段,没有就返回空串。</summary>
    private static string Field(License lic, string name)
        => lic.ProductFeatures?.Get(name)?.Trim() ?? "";

    /// <summary>本机自检:默认读工作目录下的 official.lic / official.key(可用 ZXM_LICENSE / ZXM_KEY 覆盖)。</summary>
    public static OfficialVerdict Check(string? directory = null)
    {
        var dir = directory ?? Directory.GetCurrentDirectory();

        var licensePath = Environment.GetEnvironmentVariable("ZXM_LICENSE");
        if (string.IsNullOrWhiteSpace(licensePath))
        {
            licensePath = Path.Combine(dir, FileName);
        }

        var keyPath = Environment.GetEnvironmentVariable("ZXM_KEY");
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            keyPath = Path.Combine(dir, KeyFileName);
        }

        try
        {
            if (!File.Exists(licensePath))
            {
                return new OfficialVerdict
                {
                    Ok = false,
                    Code = "no-file",
                    Detail = "没找到许可证 " + licensePath + "(把官方签发的 " + FileName + " 放到 exe 同目录)",
                };
            }

            var text = File.ReadAllText(licensePath, Encoding.UTF8);
            var licenseKey = File.Exists(keyPath) ? File.ReadAllText(keyPath, Encoding.UTF8).Trim() : "";
            return Verify(text, OfficialKey.Create(), licenseKey);
        }
        catch (Exception ex)
        {
            return new OfficialVerdict { Ok = false, Code = "bad-format", Detail = "读许可证失败:" + ex.Message };
        }
    }

    /// <summary>签发一张证(给 zxm-lic 用):绑定授权码哈希、名称、序列号、天数(0 = 100 年)。</summary>
    public static string Sign(string privateKey, string passphrase, string licenseKey, string name, int days, string serial)
    {
        var now = DateTime.Now;
        var expires = days <= 0 ? now.AddYears(100) : now.AddDays(days);

        var license = License.New()
            .WithUniqueIdentifier(Guid.NewGuid())
            .As(LicenseType.Standard)
            .ExpiresAt(expires)
            .WithProductFeatures(new Dictionary<string, string>
            {
                [FProduct] = Product,
                [FKey] = HashKey(licenseKey),
                [FSerial] = serial,
                [FIssued] = now.ToString("yyyy-MM-dd"),
                [FName] = name,
            })
            .CreateAndSignWithPrivateKey(privateKey, passphrase);

        return license.ToString();
    }

    /// <summary>由许可证原文算 pepper(第二层校验用)。</summary>
    private static byte[] PepperOf(string licenseText)
        => SHA256.HashData(Encoding.UTF8.GetBytes(licenseText));
}
