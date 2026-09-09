using Conclave.Domain;

namespace Conclave.Application;

/// <summary>一个 Revision 在 UI 上的一行。</summary>
/// <param name="Revision">幂等键。</param>
/// <param name="Pr">Summons 当时的 PR 快照。</param>
/// <param name="Stage">人读的阶段名：Summoned / Seated / Reviewing / Voted / Promulgated。</param>
/// <param name="Quorum">应有席位数。</param>
/// <param name="BallotCount">已收到的票数。</param>
/// <param name="Decision">已公布则是最终结论，否则 null。</param>
/// <param name="Findings">已公布则是合并后的问题数，否则 0。</param>
/// <param name="MySeat">本节点在这个 Revision 上的轮次；-1 表示没席位。</param>
public sealed record PrView(
    Revision Revision,
    PrMeta Pr,
    string Stage,
    int Quorum,
    int BallotCount,
    ReviewDecision? Decision,
    int Findings,
    int MySeat);

/// <summary>
/// 后台服务与 UI 之间唯一的通信面。
/// </summary>
/// <remarks>
/// 后台写、UI 读，通过 <see cref="Changed"/> 通知。刻意不让 UI 直接调用后台服务的方法，
/// 也不让后台持有任何 Avalonia 类型 —— Application 层必须能在没有 UI 的进程里跑起来。
/// </remarks>
public sealed class NodeState
{
    private readonly Lock _gate = new();
    private IReadOnlyList<PrView> _prs = [];
    private IReadOnlyList<Block> _blocks = [];
    private string _status = "启动中";
    private string? _toolProblem;

    public event EventHandler? Changed;

    /// <summary>
    /// 外部工具找不到时的说明；null 表示一切正常。
    /// </summary>
    /// <remarks>
    /// 与「az 没登录」区分开：从 Finder 启动 .app 时 PATH 里没有 az / claude，
    /// 那时提示「请跑 az devops login」会把人带到错误的方向。
    /// </remarks>
    public string? ToolProblem
    {
        get { lock (_gate) { return _toolProblem; } }
    }

    public void SetToolProblem(string? problem)
    {
        lock (_gate)
        {
            _toolProblem = problem;
        }

        Raise();
    }

    public IReadOnlyList<PrView> Pipeline
    {
        get { lock (_gate) { return _prs; } }
    }

    public IReadOnlyList<Block> RecentBlocks
    {
        get { lock (_gate) { return _blocks; } }
    }

    public string Status
    {
        get { lock (_gate) { return _status; } }
    }

    public void SetPipeline(IReadOnlyList<PrView> prs)
    {
        lock (_gate)
        {
            _prs = prs;
        }

        Raise();
    }

    public void SetRecentBlocks(IReadOnlyList<Block> blocks)
    {
        lock (_gate)
        {
            _blocks = blocks;
        }

        Raise();
    }

    public void SetStatus(string status)
    {
        lock (_gate)
        {
            _status = status;
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
