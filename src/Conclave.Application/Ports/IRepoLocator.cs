namespace Conclave.Application.Ports;

/// <summary>
/// 找出本机已 clone 的 repo。「本机有 clone」是入席的硬规则之一 ——
/// 把 job 派给一台 clone 不到代码的机器，只会白等一个超时。
/// </summary>
public interface IRepoLocator
{
    /// <summary>repo 名 → 本地工作目录。键不区分大小写。</summary>
    IReadOnlyDictionary<string, string> Locate();
}
