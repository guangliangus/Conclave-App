using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Conclave.Infrastructure.Mesh;
using Conclave.Infrastructure.Update;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>把一个 Conclave 节点装进 <see cref="IServiceCollection"/>。</summary>
public static class ConclaveServiceCollectionExtensions
{
    /// <summary>
    /// 从配置读出 <see cref="ConclaveOptions"/> 并注册一个节点。
    /// </summary>
    /// <remarks>
    /// 读 <c>Conclave</c> 配置节。绑定后仍会经 <see cref="AddConclaveNode(IServiceCollection, ConclaveOptions)"/>，
    /// 所以配置路径与测试路径注册的是同一套东西。
    /// </remarks>
    public static IServiceCollection AddConclaveNode(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var opts = new ConclaveOptions();
        configuration.GetSection("Conclave").Bind(opts);
        return services.AddConclaveNode(opts);
    }

    /// <summary>
    /// 注册节点身份、Acta、适配器与后台服务。
    /// </summary>
    /// <remarks>
    /// <see cref="DiscoveryService"/> 与 <see cref="ReviewOrchestrator"/> 同时以单例和
    /// HostedService 注册 —— UI 要能直接调它们的 <c>PollOnceAsync</c> / <c>RequestReview</c>，
    /// 而 <c>AddHostedService&lt;T&gt;()</c> 自己会另建一个实例。
    /// </remarks>
    public static IServiceCollection AddConclaveNode(
        this IServiceCollection services, ConclaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opts = options ?? new ConclaveOptions();
        _ = services.AddSingleton(opts);

        _ = services.AddSingleton(sp =>
        {
            Directory.CreateDirectory(opts.HomeDirectory);
            var identity = ElectorKeyStore.LoadOrCreate(opts.KeyPath);
            var logger = sp.GetRequiredService<ILogger<ElectorIdentity>>();

            logger.LogInformation("节点身份 {ElectorId}（私钥 {Path}）", identity.Id, opts.KeyPath);

            if (opts.AllowSelfReview)
            {
                // 跟 AzIdentityOverride、TrustAllElectors 一样单独警告：这三个开关关掉的
                // 都是硬规则，而失效是静默的，藏在下面那行「生效配置」里没人会注意到。
                logger.LogWarning(
                    "已允许作者评审自己的 PR（AllowSelfReview）—— 只该用于本机联调。"
                    + "本节点发现的 PR 会带着这个策略广播给整个 mesh");
            }

            // 把生效配置打出来：配置分四层叠加，出问题时最先要确认的就是「到底生效了哪份」。
            logger.LogInformation(
                "生效配置：轮询 {Poll} · 编排 {Orch} · 并发 {Max} · 自动评审 {Auto} · 投递 {Post} · mesh {Mesh} · 飞书通知 {Lark} · project 白名单 {Projects} · project 黑名单 {Denied} · 工作区 {Work} · 额度预算 {Budget} · quorum {Quorum} · 重试上限 {Attempts} · 复审归属 {Sticky}",
                opts.PollInterval, opts.OrchestratorInterval, opts.MaxConcurrent,
                opts.AutoReview, opts.PostToAzureDevOps,
                opts.Mesh.Enabled ? $"开（:{opts.Mesh.HttpPort}）" : "关",
                DescribeLark(opts.Lark),
                opts.ProjectAllowList.Count > 0 ? string.Join(',', opts.ProjectAllowList) : "全部",
                opts.ProjectDenyList.Count > 0 ? string.Join(',', opts.ProjectDenyList) : "无",
                opts.WorkspaceRoot,
                DescribeBudget(opts.ClaudeUsage),
                DescribeQuorum(opts.Quorum),
                opts.MaxReviewAttempts,
                opts.StickyReviewer ? "开" : "关");

            if (opts.ClaudeUsage.IsUnbounded)
            {
                // 不配预算等于把「额度过 80% 不入席」这条硬规则关掉了。默认是配了的，
                // 所以走到这里说明有人显式清空了 —— 必须说出来，否则会以为规则还在生效。
                logger.LogWarning(
                    "Claude 额度预算未配（TokenBudget 与 CostUsdBudget 都是 0），"
                    + "本节点报出的用量恒为 0，「用量超 {Max:P0} 不入席」不会触发",
                    Domain.Elector.MaxUtilization);
            }

            return identity;
        });

        _ = services.AddSingleton<ExecutableResolver>();
        _ = services.AddSingleton<ClaudeCli>();
        _ = services.AddSingleton<IClaudeCli>(sp => sp.GetRequiredService<ClaudeCli>());
        _ = services.AddSingleton<NodeState>();
        // 装配处二选一，调用处（HttpMesh / SqliteActa）不用关心当前是哪种放行策略
        _ = opts.Mesh.TrustAllElectors
            ? services.AddSingleton<IElectorAllowList, OpenElectorAllowList>()
            : services.AddSingleton<IElectorAllowList, FileElectorAllowList>();
        _ = services.AddSingleton<GitWorkspaceFactory>();
        // 探针刻意是单例：它自带 60 秒缓存，多个读者（UI 面板 + 发现循环）共用一份，
        // 否则每个读者各起一个 claude 子进程。
        _ = services.AddSingleton<ClaudeCliUsageProbe>();
        _ = services.AddSingleton<IUsageMeter, ClaudeUsageMeter>();
        _ = services.AddSingleton<IPrSource, AzCliPrSource>();
        // 单例：评审日志的写方（runner）和读方（mesh 接口、界面）必须是同一份缓冲。
        _ = services.AddSingleton<ReviewProgressLog>();
        _ = services.AddSingleton<IReviewRunner, ClaudeReviewRunner>();
        _ = services.AddSingleton<SqliteActa>();
        _ = services.AddSingleton<IActaStore>(sp => sp.GetRequiredService<SqliteActa>());
        _ = services.AddSingleton<IReviewLog, SqliteReviewLog>();

        // 显式 new 而不是 AddSingleton<INotifier, LarkNotifier>()：那个构造函数最后一个参数是
        // 只给测试用的 HttpMessageHandler，让容器去猜它该不该注入没有好处。
        _ = services.AddSingleton<INotifier>(sp => new LarkNotifier(
            sp.GetRequiredService<ConclaveOptions>(),
            sp.GetRequiredService<ILogger<LarkNotifier>>()));

        _ = services.AddSingleton(sp =>
        {
            var identity = sp.GetRequiredService<ElectorIdentity>();
            var logger = sp.GetRequiredService<ILogger<LocalMesh>>();

            // az 身份取不到就退到 git 的 user.email 本地部分。取不到会让「不评审自己的 PR」
            // 这条硬规则失效，所以要在 UI 上显眼地讲出来，而不是静默继续。
            var azIdentity = string.Empty;

            if (!string.IsNullOrWhiteSpace(opts.AzIdentityOverride))
            {
                // 谎报身份会让「不评审自己的 PR」失效，而那种失效是静默的 ——
                // 所以单独警告一次，别让它藏在一行生效配置里。
                azIdentity = opts.AzIdentityOverride.Trim();
                logger.LogWarning(
                    "az 身份被覆盖成 {Identity}（AzIdentityOverride）—— 只该用于本机联调，"
                    + "「不评审自己的 PR」这条硬规则会按这个假身份比对",
                    azIdentity);

                return new Elector
                {
                    Id = identity.Id,
                    PublicKey = identity.PublicKey,
                    AzIdentity = azIdentity,
                    MaxConcurrent = opts.MaxConcurrent,
                    LastHeartbeat = DateTimeOffset.UtcNow,
                    AppVersion = AppInfo.Version,
                    ProtocolVersion = Beacon.ProtocolVersion,
                };
            }

            try
            {
                azIdentity = sp.GetRequiredService<IPrSource>()
                    .GetAuthenticatedIdentityAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (FileNotFoundException ex)
            {
                // 跟「没登录」是两回事，别把人指向 az devops login。
                // 从 Finder 启动 .app 时 LaunchServices 只给最小 PATH，az 根本不在里面。
                logger.LogError(ex, "找不到 az 可执行文件 —— 「不评审自己的 PR」将失效");
                sp.GetRequiredService<NodeState>().SetToolProblem(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "读不到 az 登录身份 —— 「不评审自己的 PR」将失效，请跑 az devops login");
            }

            return new Elector
            {
                Id = identity.Id,
                PublicKey = identity.PublicKey,
                AzIdentity = azIdentity,
                MaxConcurrent = opts.MaxConcurrent,
                LastHeartbeat = DateTimeOffset.UtcNow,
                AppVersion = AppInfo.Version,
                ProtocolVersion = Beacon.ProtocolVersion,
            };
        });

        // mesh 关掉时用 LocalMesh（成员只有自己），席位分配的代码两边完全一样 ——
        // 于是 P0 的行为和 P1 的行为走的是同一条编排逻辑。
        if (opts.Mesh.Enabled)
        {
            _ = services.AddSingleton(sp => new HttpMesh(
                sp.GetRequiredService<Elector>(),
                sp.GetRequiredService<ElectorIdentity>(),
                sp.GetRequiredService<IElectorAllowList>(),
                sp.GetRequiredService<ConclaveOptions>(),
                sp.GetRequiredService<ILogger<HttpMesh>>()));
            _ = services.AddSingleton<IMesh>(sp => sp.GetRequiredService<HttpMesh>());
            _ = services.AddHostedService<MeshService>();
        }
        else
        {
            _ = services.AddSingleton<IMesh>(sp => new LocalMesh(sp.GetRequiredService<Elector>()));
        }

        _ = services.AddSingleton<DiscoveryService>();
        _ = services.AddSingleton<ReviewOrchestrator>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<ReviewOrchestrator>());

        // 更新：查是后台定时的（可关），装是人点的（入口一直在，所以装的那半边总是注册）。
        _ = services.AddSingleton<IUpdateSource>(sp => new GitHubReleaseSource(
            sp.GetRequiredService<ConclaveOptions>(),
            sp.GetRequiredService<ILogger<GitHubReleaseSource>>()));
        _ = services.AddSingleton<IUpdateInstaller, MacUpdateInstaller>();
        if (opts.Update.Enabled)
        {
            _ = services.AddSingleton<UpdateService>();
            _ = services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        }

        return services;
    }

    /// <summary>
    /// 飞书通知在启动日志里的一行说明。
    /// </summary>
    /// <remarks>
    /// 「开着但换不出收件人」是最常见的静默失效 —— 忘了配 EmailDomain 也没写 UserMap 时，
    /// 每条通知只会在结论出来的那一刻留下一条 Warning，而那时人早就不看日志了。
    /// 所以启动时就要说清楚收件人从哪来。
    /// </remarks>
    private static string DescribeLark(LarkOptions lark)
    {
        if (!lark.Enabled)
        {
            return "关";
        }

        if (!lark.IsConfigured)
        {
            return "开但缺 AppId/AppSecret（不会发）";
        }

        var routes = new List<string>(2);
        if (lark.EmailDomain.Length > 0)
        {
            routes.Add($"@{lark.EmailDomain.TrimStart('@')}");
        }

        if (lark.UserMap.Count > 0)
        {
            routes.Add($"UserMap {lark.UserMap.Count} 条");
        }

        return routes.Count > 0 ? $"开（{string.Join(" + ", routes)}）" : "开但没有收件人来源（不会发）";
    }

    private static string DescribeQuorum(Domain.QuorumPolicy quorum)
    {
        if (quorum.IsSingleReview)
        {
            return "只评一次";
        }

        var peak = Math.Max(
            quorum.Default,
            Math.Max(quorum.ReservedMatters, Math.Max(quorum.MediumChange, quorum.LargeChange)));

        return $"最多 {peak} 遍（指纹 {quorum.Fingerprint()}）";
    }

    private static string DescribeBudget(ClaudeUsageOptions usage)
    {
        if (usage.IsUnbounded)
        {
            return "未配（额度规则未生效）";
        }

        var parts = new List<string>(2);
        if (usage.TokenBudget > 0)
        {
            parts.Add($"{usage.TokenBudget / 1_000_000.0:F1}M token");
        }

        if (usage.CostUsdBudget > 0)
        {
            parts.Add($"${usage.CostUsdBudget:F2}");
        }

        return string.Join(" / ", parts) + $" 每 {usage.Window.TotalDays:0.#} 天";
    }
}
