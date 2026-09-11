namespace Conclave.Application.Ports;

/// <summary>
/// 本节点实际会用哪个 claude。
/// </summary>
/// <remarks>
/// 只为了把版本号摊到心跳里（见 <c>Elector.ClaudeVersion</c>）。实现在基础设施层
/// （<c>Conclave.Infrastructure.ClaudeCli</c>）：一台机器上常有不止一份 claude，
/// 它会把每份都验一次版本再挑最新的那份 —— 这里报的是<b>挑中的那份</b>。
/// </remarks>
public interface IClaudeCli
{
    /// <summary>
    /// 挑中那份 claude 的版本号；一份都找不到、或者版本验不出来时是空串。
    /// </summary>
    /// <remarks>
    /// 异步是因为它可能要起一次子进程问版本。实现自己带缓存，按心跳调用不会每次都 fork。
    /// <b>不抛</b>：拿不到版本只是少显示一格，不该让心跳这一轮整个失败。
    /// </remarks>
    Task<string> VersionAsync(CancellationToken ct);
}
