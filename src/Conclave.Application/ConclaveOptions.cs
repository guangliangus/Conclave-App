namespace Conclave.Application;

/// <summary>节点配置。</summary>
public sealed class ConclaveOptions
{
    /// <summary>私钥、账本、日志的落地目录。</summary>
    public string HomeDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".conclave");

    public string ActaPath => Path.Combine(HomeDirectory, "acta.db");

    public string KeyPath => Path.Combine(HomeDirectory, "elector.key");

    /// <summary>轮询 Azure DevOps 的间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>编排循环的间隔：认领席位、跑评审、公布结论。</summary>
    public TimeSpan OrchestratorInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>认领席位后多久没出 Ballot 就判定弃权，席位让给下一轮。</summary>
    public TimeSpan SeatingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>单个 claude 子进程的墙钟上限。</summary>
    public TimeSpan ReviewTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>本节点同时最多跑几个评审。</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>去这些目录下找已 clone 的 repo。支持 <c>~</c> 前缀。</summary>
    public IList<string> RepoSearchRoots { get; } = [
        "~/projects",
        "~/C#Projects",
        "~/C#Projects/guang",
        "~/goWorkSpace",
    ];

    /// <summary>只轮询这些 project；留空表示全部。</summary>
    public IList<string> ProjectAllowList { get; } = [];

    /// <summary>
    /// 发现 PR 后是否自动开跑评审。
    /// </summary>
    /// <remarks>
    /// P0 默认 false：一开机就把 34 个 project 的活跃 PR 全评一遍会烧掉可观的额度。
    /// 先在 UI 里手动点几个确认链路正常，再打开。
    /// </remarks>
    public bool AutoReview { get; set; }

    /// <summary>
    /// 是否把结论投递回 Azure DevOps（发评论 + 投票）。
    /// </summary>
    /// <remarks>
    /// P0 默认 false，只在本地 Acta 上留结论。确认合并结果质量之前不要往真实 PR 上写。
    /// </remarks>
    public bool PostToAzureDevOps { get; set; }

    public string ClaudeExecutable { get; set; } = "claude";

    public string AzExecutable { get; set; } = "az";

    /// <summary>展开 <c>~</c> 前缀。</summary>
    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~', '/', '\\'));
    }
}
