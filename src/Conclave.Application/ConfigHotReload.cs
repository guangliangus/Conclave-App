namespace Conclave.Application;

/// <summary>
/// 把重新读出来的配置拷进<b>活着的</b> <see cref="ConclaveOptions"/> 单例。
/// </summary>
/// <remarks>
/// <para>
/// 热更新能这么便宜地做成，靠的是消费方的写法：<c>ReviewOrchestrator</c> 和
/// <c>DiscoveryService</c> 全程是 <c>_options.MaxReviewAttempts</c> 这样<b>在调用点现读</b>，
/// 没有往自己的字段里缓存。所以只要往同一个实例里把值换掉，它们下一次循环就用上了，
/// 消费方一行都不用改。
/// </para>
/// <para>
/// 调用方<b>必须</b>先绑到一个临时实例再交给这里，不能直接把配置绑进活着的那个：
/// 文件写到一半时绑出来的是一堆默认值，直接绑等于把配置清空，而且不会有任何报错。
/// 顺带也绕开了集合的追加语义 —— .NET 的配置绑定往 <see cref="IList{T}"/> 里追加而不是
/// 替换，往活实例上重绑一次 <see cref="ConclaveOptions.ProjectAllowList"/> 就叠一份。
/// 这里整个集合对象直接换掉，不存在追加。
/// </para>
/// </remarks>
public static class ConfigHotReload
{
    /// <summary>
    /// 把 <paramref name="fresh"/> 里可热更的字段拷进 <paramref name="live"/>。
    /// </summary>
    /// <returns>
    /// 改了、但<b>要重启才生效</b>的键名。空表示这次改动全部已经生效。
    /// </returns>
    public static IReadOnlyList<string> Apply(ConclaveOptions live, ConclaveOptions fresh)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(fresh);

        var pending = Pending(live, fresh);

        live.LogRetention = fresh.LogRetention;
        live.PollInterval = fresh.PollInterval;
        live.OrchestratorInterval = fresh.OrchestratorInterval;
        live.SeatingTimeout = fresh.SeatingTimeout;
        live.ReviewTimeout = fresh.ReviewTimeout;
        live.CheckoutTimeout = fresh.CheckoutTimeout;
        live.MaxConcurrent = fresh.MaxConcurrent;
        live.DiscoveryConcurrency = fresh.DiscoveryConcurrency;
        live.FetchDepth = fresh.FetchDepth;
        live.ExcludedProjectSuffixes = fresh.ExcludedProjectSuffixes;
        live.AllowSelfReview = fresh.AllowSelfReview;
        live.MaxReviewAttempts = fresh.MaxReviewAttempts;
        live.RetryOnSameNode = fresh.RetryOnSameNode;
        live.StickyReviewer = fresh.StickyReviewer;
        live.AutoReview = fresh.AutoReview;
        live.PostToAzureDevOps = fresh.PostToAzureDevOps;
        live.AzureDevOpsOrgUrl = fresh.AzureDevOpsOrgUrl;
        live.ClaudeExecutable = fresh.ClaudeExecutable;
        live.AzExecutable = fresh.AzExecutable;
        live.GitExecutable = fresh.GitExecutable;

        // 整个对象换掉，不是逐字段拷：新实例是刚绑出来的，集合也是它自己的那一份，
        // 换过去就不可能跟旧值叠在一起。消费方读的是 live.X，所以下一次读就是新对象。
        live.ProjectAllowList = fresh.ProjectAllowList;
        live.ProjectDenyList = fresh.ProjectDenyList;
        live.ExtraToolPaths = fresh.ExtraToolPaths;
        live.Quorum = fresh.Quorum;
        live.ClaudeUsage = fresh.ClaudeUsage;
        live.Lark = fresh.Lark;
        live.Update = fresh.Update;

        return pending;
    }

    /// <summary>
    /// 改了但热更不了的那几样。
    /// </summary>
    /// <remarks>
    /// 它们在启动时就被别的东西吃掉了：<see cref="ConclaveOptions.HomeDirectory"/> 决定了
    /// 已经打开的 SQLite 文件，<see cref="MeshOptions"/> 的端口与组播地址对应已经 listen 的
    /// socket、<see cref="MeshOptions.TrustAllElectors"/> 在装配时就选定了放行策略的实现，
    /// <see cref="ConclaveOptions.AzIdentityOverride"/> 已经烤进本节点广播出去的 Elector。
    /// <para>
    /// 所以<b>刻意不拷</b>，只报出来。拷了会让内存里的值跟实际行为对不上，而启动日志和
    /// 面板都是照内存里那份显示的 —— 那比「没生效」更糟：没生效还看得出来，对不上看不出来。
    /// </para>
    /// </remarks>
    private static List<string> Pending(ConclaveOptions live, ConclaveOptions fresh)
    {
        var pending = new List<string>();

        if (!string.Equals(live.HomeDirectory, fresh.HomeDirectory, StringComparison.Ordinal))
        {
            pending.Add(nameof(ConclaveOptions.HomeDirectory));
        }

        if (!string.Equals(live.AzIdentityOverride, fresh.AzIdentityOverride, StringComparison.Ordinal))
        {
            pending.Add(nameof(ConclaveOptions.AzIdentityOverride));
        }

        if (MeshDiffers(live.Mesh, fresh.Mesh))
        {
            pending.Add(nameof(ConclaveOptions.Mesh));
        }

        return pending;
    }

    /// <summary>
    /// mesh 参数整块比，不拆开报。
    /// </summary>
    /// <remarks>
    /// 拆开报会让人以为「只有端口要重启、间隔可以热更」，而实际上 <c>MeshService</c> 的
    /// 心跳循环、组播 socket 和放行策略是在启动时一起搭起来的。整块要重启，说整块。
    /// </remarks>
    private static bool MeshDiffers(MeshOptions live, MeshOptions fresh)
        => live.Enabled != fresh.Enabled
        || live.TrustAllElectors != fresh.TrustAllElectors
        || live.BeaconPort != fresh.BeaconPort
        || live.HttpPort != fresh.HttpPort
        || live.Fanout != fresh.Fanout
        || live.BeaconInterval != fresh.BeaconInterval
        || live.RequestTimeout != fresh.RequestTimeout
        || !string.Equals(live.MulticastAddress, fresh.MulticastAddress, StringComparison.Ordinal);
}
