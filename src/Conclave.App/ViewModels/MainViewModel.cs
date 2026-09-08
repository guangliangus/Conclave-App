using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conclave.Application;
using Conclave.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Conclave.App.ViewModels;

/// <summary>主窗口的 ViewModel。只读 <see cref="NodeState"/>，不直接碰 Acta。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly NodeState _state;
    private readonly IMesh _mesh;
    private readonly DiscoveryService _discovery;
    private readonly ReviewOrchestrator _orchestrator;
    private readonly ConclaveOptions _options;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    public partial string NodeId { get; set; } = "—";

    [ObservableProperty]
    public partial string AzIdentity { get; set; } = "—";

    [ObservableProperty]
    public partial string Status { get; set; } = "启动中";

    [ObservableProperty]
    public partial string Capability { get; set; } = "—";

    [ObservableProperty]
    public partial bool AutoReview { get; set; }

    [ObservableProperty]
    public partial bool PostToAzureDevOps { get; set; }

    [ObservableProperty]
    public partial bool IsPolling { get; set; }

    /// <summary>az 身份读不到时给出的醒目警告 —— 此时「不评审自己的 PR」不生效。</summary>
    [ObservableProperty]
    public partial string? IdentityWarning { get; set; }

    public ObservableCollection<PrRow> Pipeline { get; } = [];

    public ObservableCollection<BlockRow> Blocks { get; } = [];

    public MainViewModel(
        NodeState state,
        IMesh mesh,
        DiscoveryService discovery,
        ReviewOrchestrator orchestrator,
        ConclaveOptions options,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(options);

        _state = state;
        _mesh = mesh;
        _discovery = discovery;
        _orchestrator = orchestrator;
        _options = options;
        _logger = logger;

        AutoReview = options.AutoReview;
        PostToAzureDevOps = options.PostToAzureDevOps;

        _state.Changed += OnStateChanged;
        Refresh();
    }

    /// <summary>后台线程触发，必须切回 UI 线程再动集合。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void Refresh()
    {
        var self = _mesh.Self;

        NodeId = self.Id;
        AzIdentity = string.IsNullOrEmpty(self.AzIdentity) ? "(未取到)" : self.AzIdentity;
        IdentityWarning = string.IsNullOrEmpty(self.AzIdentity)
            ? "读不到 az 登录身份：「不评审自己的 PR」这条硬规则当前不生效。请跑 az devops login 后重启。"
            : null;
        Status = _state.Status;
        Capability = string.Format(
            CultureInfo.InvariantCulture,
            "{0} 个本地 repo · {1} 个 project · 在跑 {2}/{3}",
            self.Repos.Count, self.Projects.Count, self.RunningJobs, self.MaxConcurrent);

        Pipeline.Clear();
        foreach (var view in _state.Pipeline
            .OrderBy(v => v.Decision is not null)
            .ThenByDescending(v => v.Pr.PrId))
        {
            Pipeline.Add(new PrRow(view, ReviewCommand));
        }

        Blocks.Clear();
        foreach (var block in _state.RecentBlocks)
        {
            Blocks.Add(new BlockRow(block));
        }
    }

    [RelayCommand]
    private async Task PollAsync()
    {
        if (IsPolling)
        {
            return;
        }

        IsPolling = true;
        try
        {
            await _discovery.PollOnceAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手动轮询失败");
            Status = $"轮询失败：{ex.Message}";
        }
        finally
        {
            IsPolling = false;
        }
    }

    /// <summary>手动开跑一个 Revision 的评审，绕过 <see cref="ConclaveOptions.AutoReview"/>。</summary>
    [RelayCommand]
    private void Review(PrRow? row)
    {
        if (row is null)
        {
            return;
        }

        _orchestrator.RequestReview(row.RevisionId);
        Status = $"已排入评审 {row.RevisionId}，最多 {_options.OrchestratorInterval.TotalSeconds:F0} 秒后开跑";
    }

    partial void OnAutoReviewChanged(bool value)
    {
        _options.AutoReview = value;
        Status = value
            ? "自动评审已开启：新发现的 PR 会自动开跑"
            : "自动评审已关闭：只有手动点「评审」才会跑";
    }

    partial void OnPostToAzureDevOpsChanged(bool value)
    {
        _options.PostToAzureDevOps = value;
        Status = value
            ? "投递已开启：公布结论时会往真实 PR 发评论并投票"
            : "投递已关闭：结论只写进本地 Acta";
    }
}
