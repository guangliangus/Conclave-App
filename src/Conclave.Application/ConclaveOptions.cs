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

    /// <summary>
    /// 没有任何配置时用的 repo 搜索根目录。
    /// </summary>
    /// <remarks>
    /// 默认值刻意不写在属性初始化器里。.NET 的配置绑定对集合属性是**追加**语义
    /// （接口类型的集合有没有 setter 都一样），所以「属性带默认值 + 配置文件里再列一遍」
    /// 会得到两份 —— 实测 .app 启动日志里这个列表确实重复了一遍。
    /// 改为绑定后由 <c>AddConclaveNode</c> 判空回填，语义变成「配置给了就用配置，没给才用默认」。
    /// </remarks>
    public static IReadOnlyList<string> DefaultRepoSearchRoots { get; } = [
        "~/projects",
        "~/C#Projects",
        "~/C#Projects/guang",
        "~/goWorkSpace",
    ];

    /// <summary>去这些目录下找已 clone 的 repo。支持 <c>~</c> 前缀。留空则用 <see cref="DefaultRepoSearchRoots"/>。</summary>
    public IList<string> RepoSearchRoots { get; set; } = [];

    /// <summary>把留空的集合回填成默认值。绑定完配置后调一次。</summary>
    public ConclaveOptions ApplyDefaults()
    {
        if (RepoSearchRoots.Count == 0)
        {
            RepoSearchRoots = [.. DefaultRepoSearchRoots];
        }

        return this;
    }

    /// <summary>只轮询这些 project；留空表示全部。</summary>
    public IList<string> ProjectAllowList { get; set; } = [];

    /// <summary>P1 mesh 的参数。</summary>
    public MeshOptions Mesh { get; set; } = new();

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
