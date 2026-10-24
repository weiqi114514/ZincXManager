using System.Security.Cryptography;  // 加密与哈希
using System.Text;  // 文本编码与 StringBuilder

namespace ZincXManagerShared.Security;

/// <summary>
/// 会话密钥协商:ECDH P-256,双方交换公钥后各自派生出两把 AES-256 密钥
/// (一把客户端→服务端,一把服务端→客户端,避免两个方向共用 nonce)。
///
/// <para>用途:ZXMC ↔ ZXMS 之间的报文整体加密,抓包拿不到账号密文,也就没法重放。</para>
/// </summary>
public sealed class SessionHandshake
{
    private const string SaltText = "zxm-session-v1";
    private const string InfoText = "zxmc-zxms";

    // 本次握手的 ECDH 实例。
    private readonly ECDiffieHellman _ecdh;

    /// <summary>会话握手:生成 P-256 密钥对,和对端换公钥后导出会话密钥。</summary>
    public SessionHandshake()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>本端公钥(SubjectPublicKeyInfo DER),直接塞进包里发给对端。</summary>
    public byte[] PublicKey => _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

    /// <summary>拿到对端公钥后派生出会话密钥。<paramref name="isServer"/> 决定两把钥匙的方向。</summary>
    public SessionCrypto Complete(byte[] peerPublicKey, bool isServer)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        var secret = _ecdh.DeriveKeyMaterial(peer.PublicKey);
        var okm = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            secret,
            64,
            salt: Encoding.UTF8.GetBytes(SaltText),
            info: Encoding.UTF8.GetBytes(InfoText));

        var c2s = okm[..32];
        var s2c = okm[32..];
        return isServer
            ? new SessionCrypto(sendKey: s2c, recvKey: c2s)
            : new SessionCrypto(sendKey: c2s, recvKey: s2c);
    }
}

/// <summary>
/// 会话加解密:AES-256-GCM。密文格式 = [12 字节 nonce][密文][16 字节 tag]。
/// 发送方向用递增计数器当 nonce,不会重复。
/// </summary>
public sealed class SessionCrypto
{
    /// <summary>包标志位:负载已加密。</summary>
    public const ushort FlagEncrypted = 0x0001;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    // 发送方向的 AES-GCM。
    private readonly AesGcm _send;
    // 接收方向的 AES-GCM。
    private readonly AesGcm _recv;
    // 发送序号(防止重放)。
    private long _counter;

    /// <summary>会话密钥:由 ECDH 共享密钥派生出 AES-256-GCM。</summary>
    public SessionCrypto(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
    }

    /// <summary>加密一段负载,输出 nonce + 密文 + tag。</summary>
    public byte[] Encrypt(byte[] plain)
    {
        var nonce = new byte[NonceSize];
        var n = Interlocked.Increment(ref _counter);
        BitConverter.TryWriteBytes(nonce.AsSpan(NonceSize - sizeof(long)), n);

        var output = new byte[NonceSize + plain.Length + TagSize];
        nonce.CopyTo(output, 0);
        _send.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize));
        return output;
    }

    /// <summary>解密一段负载,校验 tag。</summary>
    public byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new InvalidDataException($"加密负载长度非法:{blob.Length} 字节");
        }

        var cipherLen = blob.Length - NonceSize - TagSize;
        var plain = new byte[cipherLen];
        _recv.Decrypt(
            blob.AsSpan(0, NonceSize),
            blob.AsSpan(NonceSize, cipherLen),
            blob.AsSpan(NonceSize + cipherLen, TagSize),
            plain);
        return plain;
    }
}
