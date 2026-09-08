using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
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
        return services.AddConclaveNode(opts.ApplyDefaults());
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

        var opts = (options ?? new ConclaveOptions()).ApplyDefaults();
        _ = services.AddSingleton(opts);

        _ = services.AddSingleton(sp =>
        {
            Directory.CreateDirectory(opts.HomeDirectory);
            var identity = ElectorKeyStore.LoadOrCreate(opts.KeyPath);
            var logger = sp.GetRequiredService<ILogger<ElectorIdentity>>();

            logger.LogInformation("节点身份 {ElectorId}（私钥 {Path}）", identity.Id, opts.KeyPath);

            // 把生效配置打出来：配置分四层叠加，出问题时最先要确认的就是「到底生效了哪份」。
            logger.LogInformation(
                "生效配置：轮询 {Poll} · 编排 {Orch} · 并发 {Max} · 自动评审 {Auto} · 投递 {Post} · mesh {Mesh} · project 白名单 {Projects} · repo 根目录 {Roots}",
                opts.PollInterval, opts.OrchestratorInterval, opts.MaxConcurrent,
                opts.AutoReview, opts.PostToAzureDevOps,
                opts.Mesh.Enabled ? $"开（:{opts.Mesh.HttpPort}）" : "关",
                opts.ProjectAllowList.Count > 0 ? string.Join(',', opts.ProjectAllowList) : "全部",
                string.Join(',', opts.RepoSearchRoots));

            return identity;
        });

        _ = services.AddSingleton<NodeState>();
        _ = services.AddSingleton<IElectorAllowList, FileElectorAllowList>();
        _ = services.AddSingleton<IRepoLocator, FileSystemRepoLocator>();
        _ = services.AddSingleton<IPrSource, AzCliPrSource>();
        _ = services.AddSingleton<IReviewRunner, ClaudeReviewRunner>();
        _ = services.AddSingleton<SqliteActa>();
        _ = services.AddSingleton<IActaStore>(sp => sp.GetRequiredService<SqliteActa>());

        _ = services.AddSingleton<IMesh>(sp =>
        {
            var identity = sp.GetRequiredService<ElectorIdentity>();
            var logger = sp.GetRequiredService<ILogger<LocalMesh>>();

            // az 身份取不到就退到 git 的 user.email 本地部分。取不到会让「不评审自己的 PR」
            // 这条硬规则失效，所以要在 UI 上显眼地讲出来，而不是静默继续。
            var azIdentity = string.Empty;
            try
            {
                azIdentity = sp.GetRequiredService<IPrSource>()
                    .GetAuthenticatedIdentityAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "读不到 az 登录身份 —— 「不评审自己的 PR」将失效，请跑 az devops login");
            }

            return new LocalMesh(new Elector
            {
                Id = identity.Id,
                PublicKey = identity.PublicKey,
                AzIdentity = azIdentity,
                MaxConcurrent = opts.MaxConcurrent,
                LastHeartbeat = DateTimeOffset.UtcNow,
            });
        });

        _ = services.AddSingleton<DiscoveryService>();
        _ = services.AddSingleton<ReviewOrchestrator>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
        _ = services.AddHostedService(sp => sp.GetRequiredService<ReviewOrchestrator>());

        return services;
    }
}
