using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conclave.Infrastructure;

/// <summary>把一个 Conclave 节点装进 <see cref="IServiceCollection"/>。</summary>
public static class ConclaveServiceCollectionExtensions
{
    /// <summary>
    /// 注册节点身份、Acta、适配器与两个后台服务。
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
            var identity = ElectorIdentity.LoadOrCreate(opts.KeyPath);
            sp.GetRequiredService<ILogger<ElectorIdentity>>()
              .LogInformation("节点身份 {ElectorId}（私钥 {Path}）", identity.Id, opts.KeyPath);
            return identity;
        });

        _ = services.AddSingleton<NodeState>();
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
