using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Conclave.Application;

/// <summary>
/// 盯着配置文件，改了就热更新，不用重启。
/// </summary>
/// <remarks>
/// <para>
/// 动机是 mesh 配置同步：同步下来的文件如果还要人去重启每台机器，同步本身就没什么价值了。
/// 单独用也成立 —— 改一台机器的 <c>~/.conclave/appsettings.json</c> 立刻生效。
/// </para>
/// <para>
/// 三件事少一件就会静默吃掉配置，而且表现都是「文件改了、什么也没发生」：
/// </para>
/// <list type="number">
/// <item>
/// <b>防抖。</b> 一次保存常常触发两三次回调 —— 编辑器写临时文件再改名、同步器先截断再写，
/// 文件监视器把「写到一半」也报出来。不防抖就会在半截文件上重载一次。
/// </item>
/// <item>
/// <b>空配置不当真。</b> 文件正在被截断时读出来是一个什么都没有的配置节，照单全收就是
/// 把配置清空。所以先看这一节还有没有子项，没有就跳过这一轮 —— 真要清空配置的人
/// 不会把整个 <c>Conclave</c> 节删掉。
/// </item>
/// <item>
/// <b>先绑临时实例再拷。</b> 见 <see cref="ConfigHotReload"/>。
/// </item>
/// </list>
/// <para>
/// 解析失败不在这里处理：文件源在装配时就配了忽略加载异常（见 <c>Program.CreateBuilder</c>），
/// 半截的 JSON 根本不会走到这里。
/// </para>
/// </remarks>
public sealed class ConfigReloadService : IHostedService, IDisposable
{
    /// <summary>防抖窗口。一次保存的多次回调会被并成一次重载。</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly IConfiguration _config;
    private readonly ConclaveOptions _live;
    private readonly NodeState _state;
    private readonly ILogger<ConfigReloadService> _logger;
    private readonly object _gate = new();
    private readonly Timer _debounce;

    private IDisposable? _subscription;
    private bool _stopped;

    public ConfigReloadService(
        IConfiguration config,
        ConclaveOptions live,
        NodeState state,
        ILogger<ConfigReloadService> logger)
    {
        _config = config;
        _live = live;
        _state = state;
        _logger = logger;
        _debounce = new Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // OnChange 每次会重新注册 —— 变更令牌是一次性的，自己 Watch 一次就只收得到第一次。
        _subscription = ChangeToken.OnChange(_config.GetReloadToken, OnFileChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopped = true;
        }

        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnFileChanged()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            // 每次变更都把闹钟往后推：连着来的几次回调只会在最后一次之后响一回。
            _ = _debounce.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>读一遍当前配置，把可热更的部分换进活着的那个 <see cref="ConclaveOptions"/>。</summary>
    /// <remarks>internal 是为了让测试能直接驱动它，不必真去碰文件系统和防抖计时器。</remarks>
    internal void Reload()
    {
        try
        {
            var section = _config.GetSection("Conclave");

            // 文件正被截断时这一节是空的。照单全收等于清空配置，而且没有任何报错 ——
            // 真心要清空的人不会把整个 Conclave 节删掉，所以空了就当没看见。
            if (!section.GetChildren().Any())
            {
                _logger.LogWarning("配置文件读出来是空的，这一轮热更新跳过（多半是正写到一半）");
                return;
            }

            var fresh = new ConclaveOptions();
            section.Bind(fresh);

            var pending = ConfigHotReload.Apply(_live, fresh);

            if (pending.Count == 0)
            {
                _logger.LogInformation("配置已热更新");
                _state.Notify(NoticeKind.Ok, "配置已热更新", "改动已生效，不用重启");
                return;
            }

            var keys = string.Join("、", pending);
            _logger.LogWarning("配置已热更新，但 {Keys} 要重启才生效", keys);
            _state.Notify(NoticeKind.Warn, "配置已热更新", $"{keys} 改了，但要重启才生效");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 热更新失败绝不能把进程带倒：旧配置还在内存里，节点照常跑。
            _logger.LogError(ex, "热更新配置失败，继续用当前这份");
            _state.Notify(NoticeKind.Bad, "配置热更新失败", ex.Message);
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _debounce.Dispose();
    }
}
