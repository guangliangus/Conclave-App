namespace Conclave.Domain;

/// <summary>
/// 一个节点在 mesh 里的公开状态。P1 起由心跳广播；P0 阶段 mesh 里只有自己。
/// </summary>
public sealed record Elector
{
    /// <summary>公钥指纹，见 <see cref="ElectorIdentity.Id"/>。</summary>
    public required string Id { get; init; }

    public required string PublicKey { get; init; }

    /// <summary>P1 起的 gRPC 地址；P0 为空。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>本节点 <c>az</c> 登录身份，用于「不评审自己的 PR」。</summary>
    public required string AzIdentity { get; init; }

    /// <summary>本机已 clone 的 repo 名。没 clone 就没法评审，是硬规则。</summary>
    public IReadOnlyList<string> Repos { get; init; } = [];

    /// <summary>本节点 <c>az</c> 身份有读权限的 project。</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    public int RunningJobs { get; init; }

    public int MaxConcurrent { get; init; } = 2;

    /// <summary>近 24 小时已出的 Ballot 数。用于公平性权重。</summary>
    public int Reviews24h { get; init; }

    public DateTimeOffset LastHeartbeat { get; init; }

    /// <summary>心跳窗口。90 秒 = 60 秒轮询间隔 + 30 秒余量。</summary>
    public static TimeSpan HeartbeatWindow => TimeSpan.FromSeconds(90);

    public bool IsAlive(DateTimeOffset now) => now - LastHeartbeat <= HeartbeatWindow;

    public bool HasRepo(string repo)
        => Repos.Contains(repo, StringComparer.OrdinalIgnoreCase);

    public bool HasProject(string project)
        => Projects.Contains(project, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 加权 HRW 里的权重：忙的少拿（负载均衡），最近干得多的少拿（长期公平）。
    /// </summary>
    public double Weight => 1.0 / (1 + RunningJobs) / (1 + (Reviews24h * 0.1));
}
