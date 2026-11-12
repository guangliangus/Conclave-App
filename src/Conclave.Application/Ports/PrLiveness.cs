namespace Conclave.Application.Ports;

/// <summary>
/// 评审跑着的时候，正在评的那一版在 Azure DevOps 上还值不值得评下去。
/// </summary>
/// <remarks>
/// 见 <see cref="IPrSource.CheckLivenessAsync"/>。问不出来一律是 <see cref="Active"/>。
/// </remarks>
public enum PrLiveness
{
    /// <summary>PR 还是 active，源提交也没变 —— 或者问不出来。</summary>
    Active,

    /// <summary>PR 已经不是 active：被合了、被撤了。</summary>
    Closed,

    /// <summary>
    /// PR 还开着，但源分支有了新推送：正在评的这一版已经过时。
    /// </summary>
    /// <remarks>
    /// 结论是按 revision（PR 号@源提交）记的，新推送就是新 revision，旧版本随之从队列里退场，
    /// 它的票没人会公布 —— 评完也不会发到 PR 上。
    /// </remarks>
    Superseded,
}
