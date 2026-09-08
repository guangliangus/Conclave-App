using System.Security.Cryptography;
using System.Text;

namespace Conclave.Domain;

/// <summary>
/// 节点身份：一对 ECDsa P-256 密钥。私钥用于签自己写的区块，公钥随区块一起广播。
/// </summary>
/// <remarks>
/// 用 ECDsa P-256 而不是 Ed25519 —— 实测 .NET 10 的 System.Security.Cryptography
/// 不含 Ed25519（只有后量子的 MLDsa/SlhDsa），为一个签名算法引入
/// NSec/BouncyCastle 的原生依赖不值得。P-256 是 BCL 内置，零依赖。
/// </remarks>
public sealed class ElectorIdentity : IDisposable
{
    private readonly ECDsa _key;

    private ElectorIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Id = FingerprintOf(PublicKey);
    }

    /// <summary>公钥指纹，16 个十六进制字符。节点在 mesh 里的地址。</summary>
    public string Id { get; }

    /// <summary>SubjectPublicKeyInfo 的 base64。</summary>
    public string PublicKey { get; }

    /// <summary>
    /// 读取 <paramref name="keyPath"/> 处的私钥，不存在则生成并以 0600 写入。
    /// </summary>
    public static ElectorIdentity LoadOrCreate(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath))
        {
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
            return new ElectorIdentity(key);
        }

        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(keyPath, key.ExportPkcs8PrivateKey());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new ElectorIdentity(key);
    }

    /// <summary>仅供测试：生成一个不落盘的临时身份。</summary>
    public static ElectorIdentity CreateEphemeral()
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public string Sign(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Convert.ToBase64String(
            _key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
    }

    public static bool Verify(string publicKeyBase64, string payload, string signatureBase64)
    {
        if (string.IsNullOrEmpty(publicKeyBase64) || string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // 公钥或签名不是合法 base64 / DER —— 当作验签失败，不要让恶意区块把节点打挂。
            return false;
        }
    }

    /// <summary>公钥指纹：SHA-256 前 8 字节的十六进制。</summary>
    public static string FingerprintOf(string publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBase64);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyBase64));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public void Dispose() => _key.Dispose();
}
