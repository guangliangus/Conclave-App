using Conclave.Domain;

namespace Conclave.Infrastructure;

/// <summary>
/// 节点私钥的落盘。
/// </summary>
/// <remarks>
/// 从 <see cref="ElectorIdentity"/> 里拆出来，好让领域层保持无 I/O ——
/// 那是 <c>Conclave.ArchitectureTests</c> 里「Domain 全纯」那条守卫的前提。
/// </remarks>
public static class ElectorKeyStore
{
    /// <summary>读取 <paramref name="keyPath"/> 处的私钥，不存在则生成并以 0600 写入。</summary>
    public static ElectorIdentity LoadOrCreate(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        if (File.Exists(keyPath))
        {
            return ElectorIdentity.FromPkcs8(File.ReadAllBytes(keyPath));
        }

        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var identity = ElectorIdentity.Create();
        File.WriteAllBytes(keyPath, identity.ExportPkcs8());

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return identity;
    }
}
