using System.Globalization;
using System.Text.Json.Nodes;

namespace Conclave.Application;

/// <summary>
/// 哪些配置可以随 mesh 同步 —— 这是整套配置同步的安全边界。
/// </summary>
/// <remarks>
/// <para>
/// <b>白名单，不是黑名单。</b> 新加一个配置项默认<b>不</b>同步，忘了分类只会少同步一样东西；
/// 反过来做的话，忘了分类就是默默把新键暴露给发布者，而那种疏漏没有任何征兆。
/// <c>SyncableConfigTests</c> 里有一条反向用例：<see cref="ConclaveOptions"/> 上出现没被
/// 归类的属性就让测试挂。
/// </para>
/// <para>
/// <b>发布和接收共用 <see cref="Filter"/>。</b> 收方不能直接信发布方给的 JSON ——
/// 发布者是被各节点显式授权的（<c>ConfigSyncOptions.Publishers</c>），但一台被入侵的发布机
/// 不该等于全组沦陷。收方重新投影一遍，白名单外的键<b>直接丢掉</b>。
/// </para>
/// <para>
/// 划线的标准是：<b>不同步任何能换成「在别人机器上执行代码」的键。</b>
/// </para>
/// </remarks>
public static class SyncableConfig
{
    /// <summary><c>Lark.AppSecret</c> 的信封用途，见 <c>Conclave.Domain.SecretSealing</c>。</summary>
    public const string SecretPurpose = "Lark.AppSecret";

    /// <summary>配置节名，跟 <c>appsettings.json</c> 一致。</summary>
    public const string Section = "Conclave";

    /// <summary>可同步的顶层标量与集合。</summary>
    private static readonly string[] Scalars =
    [
        "PollInterval", "OrchestratorInterval", "SeatingTimeout", "ReviewTimeout", "CheckoutTimeout",
        "LogRetention", "MaxConcurrent", "DiscoveryConcurrency", "FetchDepth",
        "ProjectAllowList", "ProjectDenyList", "ExcludedProjectSuffixes",
        "AllowSelfReview", "MaxReviewAttempts", "RetryOnSameNode", "StickyReviewer",
        "AutoReview", "PostToAzureDevOps", "AzureDevOpsOrgUrl",
    ];

    /// <summary>可同步的嵌套块；值是块内允许的键，<c>null</c> 表示整块允许。</summary>
    private static readonly Dictionary<string, string[]?> Blocks = new(StringComparer.Ordinal)
    {
        ["Quorum"] = null,
        ["ClaudeUsage"] = null,

        // AppSecret 刻意不在这里：它走 Secrets 的加密信封，明文永远不进 Json。
        ["Lark"] = ["Enabled", "AppId", "BaseUrl", "EmailDomain", "RequestTimeout", "UserMap"],
    };

    /// <summary>
    /// 明确<b>不</b>同步的顶层键，连理由一起写在这儿。
    /// </summary>
    /// <remarks>
    /// 前四项是远程代码执行：把它们指向攻击者的二进制或目录，全组节点下一次评审就会执行它。
    /// <c>Update</c> 同理还更糟 —— <c>BundlePath</c> 决定就地替换 <c>.app</c> 时写哪个目录
    /// （实测配错过一次，把整个 <c>src/Conclave.App</c> 换成了 117MB 的发布包），
    /// <c>Repository</c> 决定新版本从哪下载，那是供应链的入口。
    /// 后三项是机器自身的：同步过去必然是错的。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Blocked { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ClaudeExecutable"] = "指向哪个二进制 = 在别人机器上执行代码",
            ["ClaudeModel"] = "每台机器的订阅与各模型额度不一样；统一指定会让额度打满的那台直接失效",
            ["AzExecutable"] = "同上",
            ["GitExecutable"] = "同上",
            ["ExtraToolPaths"] = "补进查找路径 = 在别人机器上执行代码",
            ["Update"] = "BundlePath 决定就地替换写哪个目录，Repository 决定新版本从哪下载",
            ["HomeDirectory"] = "每台机器自己的；而且账本数据库已经打开了",
            ["Mesh"] = "端口与组播地址是机器自身的；TrustAllElectors 是信任闸，能被同步就等于没有",
            ["AzIdentityOverride"] = "让节点谎报 az 身份，「不评审自己的 PR」会静默失效",
            ["ConfigSync"] = "它自己就是这套东西的信任根，能被同步就等于没有",
        };

    /// <summary>可同步的键名，给测试和文档用。</summary>
    public static IReadOnlyList<string> Syncable { get; } = [.. Scalars, .. Blocks.Keys];

    /// <summary>
    /// 把本机配置里可同步的部分摘出来，序列化成 <c>mesh.json</c> 的形状。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 时间间隔一律按 <c>TimeSpan.ToString()</c> 写成 <c>"00:00:30"</c> 这种字符串：
    /// 那正是 <c>appsettings.json</c> 里的写法，配置绑定认得，而且格式确定 ——
    /// 交给序列化器去猜会得到依运行时版本而变的形态，而这段文本是要参与签名的。
    /// </para>
    /// <para>
    /// 摘完还要过一遍 <see cref="Filter"/>：白名单只在那一个地方生效，这里漏写一个键
    /// 只会导致它不同步（安全的方向），多写一个键则会被过滤掉。
    /// </para>
    /// </remarks>
    public static string Serialize(ConclaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var conclave = new JsonObject
        {
            ["PollInterval"] = options.PollInterval.ToString(),
            ["OrchestratorInterval"] = options.OrchestratorInterval.ToString(),
            ["SeatingTimeout"] = options.SeatingTimeout.ToString(),
            ["ReviewTimeout"] = options.ReviewTimeout.ToString(),
            ["CheckoutTimeout"] = options.CheckoutTimeout.ToString(),
            ["LogRetention"] = options.LogRetention.ToString(),
            ["MaxConcurrent"] = options.MaxConcurrent,
            ["DiscoveryConcurrency"] = options.DiscoveryConcurrency,
            ["FetchDepth"] = options.FetchDepth,
            ["ProjectAllowList"] = Array(options.ProjectAllowList),
            ["ProjectDenyList"] = Array(options.ProjectDenyList),
            ["ExcludedProjectSuffixes"] = options.ExcludedProjectSuffixes,
            ["AllowSelfReview"] = options.AllowSelfReview,
            ["MaxReviewAttempts"] = options.MaxReviewAttempts,
            ["RetryOnSameNode"] = options.RetryOnSameNode,
            ["StickyReviewer"] = options.StickyReviewer,
            ["AutoReview"] = options.AutoReview,
            ["PostToAzureDevOps"] = options.PostToAzureDevOps,
            ["AzureDevOpsOrgUrl"] = options.AzureDevOpsOrgUrl,
            ["Quorum"] = new JsonObject
            {
                ["Default"] = options.Quorum.Default,
                ["ReservedMatters"] = options.Quorum.ReservedMatters,
                ["MediumChange"] = options.Quorum.MediumChange,
                ["MediumChangeFiles"] = options.Quorum.MediumChangeFiles,
                ["LargeChange"] = options.Quorum.LargeChange,
                ["LargeChangeFiles"] = options.Quorum.LargeChangeFiles,
            },
            ["ClaudeUsage"] = new JsonObject
            {
                ["CliProbeInterval"] = options.ClaudeUsage.CliProbeInterval.ToString(),
                ["CliProbeTimeout"] = options.ClaudeUsage.CliProbeTimeout.ToString(),
                ["Window"] = options.ClaudeUsage.Window.ToString(),
                ["TokenBudget"] = options.ClaudeUsage.TokenBudget,
                ["CostUsdBudget"] = options.ClaudeUsage.CostUsdBudget.ToString(CultureInfo.InvariantCulture),
            },
            ["Lark"] = new JsonObject
            {
                ["Enabled"] = options.Lark.Enabled,
                ["AppId"] = options.Lark.AppId,
                ["BaseUrl"] = options.Lark.BaseUrl,
                ["EmailDomain"] = options.Lark.EmailDomain,
                ["RequestTimeout"] = options.Lark.RequestTimeout.ToString(),
                ["UserMap"] = Map(options.Lark.UserMap),
            },
        };

        return Filter(
            new JsonObject { [Section] = conclave }.ToJsonString(),
            out _);
    }

    /// <summary>
    /// 把一份收到的配置重新投影一遍，只留白名单里的键。
    /// </summary>
    /// <param name="json">对端给的 <c>{"Conclave":{…}}</c>。</param>
    /// <param name="dropped">被丢掉的键（含块内的，写成 <c>Lark.AppSecret</c> 这种形式）。</param>
    /// <returns>可以安全写进 <c>mesh.json</c> 的 JSON。</returns>
    /// <exception cref="System.Text.Json.JsonException">不是合法 JSON。</exception>
    public static string Filter(string json, out IReadOnlyList<string> dropped)
    {
        var removed = new List<string>();
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new System.Text.Json.JsonException("配置的最外层不是一个对象");

        var incoming = root[Section] as JsonObject
            ?? throw new System.Text.Json.JsonException($"配置里没有 {Section} 节");

        var kept = new JsonObject();

        foreach (var (key, value) in incoming)
        {
            if (Scalars.Contains(key, StringComparer.Ordinal))
            {
                kept[key] = value?.DeepClone();
            }
            else if (Blocks.TryGetValue(key, out var allowedInBlock))
            {
                kept[key] = FilterBlock(key, value, allowedInBlock, removed);
            }
            else
            {
                removed.Add(key);
            }
        }

        dropped = removed;
        return new JsonObject { [Section] = kept }.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>过滤一个嵌套块。<paramref name="allowed"/> 为 null 表示整块放行。</summary>
    private static JsonNode? FilterBlock(
        string blockName, JsonNode? value, string[]? allowed, List<string> removed)
    {
        if (value is not JsonObject block)
        {
            return value?.DeepClone();
        }

        if (allowed is null)
        {
            return block.DeepClone();
        }

        var kept = new JsonObject();
        foreach (var (key, inner) in block)
        {
            if (allowed.Contains(key, StringComparer.Ordinal))
            {
                kept[key] = inner?.DeepClone();
            }
            else
            {
                removed.Add($"{blockName}.{key}");
            }
        }

        return kept;
    }

    private static JsonArray Array(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonObject Map(IDictionary<string, string> values)
    {
        var map = new JsonObject();
        foreach (var (key, value) in values)
        {
            map[key] = value;
        }

        return map;
    }
}
