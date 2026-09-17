using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Conclave.Application;
using Conclave.Application.Ports;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>
/// 一次评审的实时日志。
/// </summary>
/// <remarks>
/// <para>
/// 日志只在<b>跑评审那个节点</b>的内存里（<see cref="ReviewProgressLog"/>），所以要分两路取：
/// 本节点在评就直接读本地缓冲；别的节点在评就走 <see cref="IMesh.FetchLogAsync"/> 现问。
/// 「我的 PR 被谁评着、评到哪一步了」是作者最想知道的事，而评审是十几分钟的黑盒。
/// </para>
/// <para>
/// 按序号取增量，不是每次全量重来：一次评审几百行，而这里两秒轮一次。
/// 对端报 <see cref="LogChunk.Running"/> = false 就停轮询 —— 评完了日志不再长，
/// 继续问只是白发请求。
/// </para>
/// <para>
/// <b>实时那条路断了不等于没日志。</b> 评审失败、跑了一半进程重启、或者那台机器
/// 已经不在评这一版了 —— 这些情况下增量接口什么都给不出来，而人恰恰是这时候最想看日志。
/// 所以还有第二条路：盘上那份全文（<see cref="ReviewLogArchive"/>，留一天）。
/// 本机有就直接读，本机没有就 <see cref="PullCommand"/> 去 mesh 里挨个问，
/// 拿回来存本地再显示。
/// </para>
/// </remarks>
public sealed partial class ReviewLogViewModel : ViewModelBase, IDisposable
{
    /// <summary>轮询节奏。评审动辄十几分钟，两秒够快，也不会把对端问烦。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IMesh _mesh;
    private readonly ReviewProgressLog _local;
    private readonly ReviewLogArchive _archive;
    private readonly string _revisionId;
    private readonly string? _reviewerId;
    private readonly DispatcherTimer _timer;

    private long _from;
    private bool _polling;

    /// <summary>这次评审一共收到过多少行。<see cref="Lines"/> 有上限，这个没有。</summary>
    private long _received;

    /// <summary>日志已经收尾（对端报 Running=false，或者压根取不到）。恢复时不必再起定时器。</summary>
    private bool _done;

    /// <summary>面板被隐藏了。见 <see cref="Suspend"/>。</summary>
    private bool _suspended;

    public ReviewLogViewModel(
        IMesh mesh,
        ReviewProgressLog local,
        ReviewLogArchive archive,
        string revisionId,
        string? reviewerId,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        _mesh = mesh;
        _local = local;
        _archive = archive;
        _revisionId = revisionId;
        _reviewerId = reviewerId;

        Subject = subject;
        Reviewer = reviewerId is null
            ? "—"
            : reviewerId == mesh.Self.Id
                ? "本节点"
                : Name(reviewerId);

        Status = "正在取日志…";

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = PollAsync();
        _timer.Start();

        _ = PollAsync();
    }

    /// <summary>标题上那句：哪个仓库的哪个 PR。</summary>
    public string Subject { get; }

    /// <summary>谁在评。</summary>
    public string Reviewer { get; }

    /// <summary>面板上显示的行，最多 <see cref="ReviewProgressLog.MaxLines"/> 条，超了从头挤掉。</summary>
    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public void Dispose() => _timer.Stop();

    /// <summary>
    /// 主面板隐藏了，停掉两秒一次的轮询。
    /// </summary>
    /// <remarks>
    /// 这个定时器原先跟着 ViewModel 一直转到评审结束 —— 而主面板的 x 只是 <c>Hide()</c>，
    /// 所以窗口早就不在屏幕上了，它还在每两秒往集合里灌行、连带重建那一列 UI。
    /// 那正是 <see cref="MainViewModel.SetVisible"/> 里说的那个放大器的另一半。
    /// </remarks>
    internal void Suspend()
    {
        _suspended = true;
        _timer.Stop();
    }

    /// <summary>面板又显示出来了，把轮询接回去并立刻补一次增量。</summary>
    internal void Resume()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;

        if (_done)
        {
            return;
        }

        _timer.Start();
        _ = PollAsync();
    }

    /// <summary>
    /// 取一次增量。
    /// </summary>
    /// <remarks>
    /// <c>_polling</c> 这道闸是必要的：对端慢的时候（十几秒才回）定时器会继续 tick，
    /// 重入会让同一段日志按乱序追加两遍。
    /// </remarks>
    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            var chunk = _reviewerId == _mesh.Self.Id
                ? _local.Read(_revisionId, _from)
                : await FetchAsync().ConfigureAwait(true);

            if (chunk is null)
            {
                // 实时那条路断了：没人在评这一版，或者对端离线/是旧版本。
                // 先看盘上有没有 —— 评完的、失败的、跑了一半重启过的，本机都还留着一天。
                _done = true;
                _timer.Stop();

                if (ShowArchived())
                {
                    return;
                }

                Status = _reviewerId is null
                    ? "没有节点在评这一版，本机也没有留档 —— 点「拉取日志」去其它节点找"
                    : $"{Reviewer} 那边取不到实时日志 —— 点「拉取日志」要它留的那份";
                return;
            }

            // 对端把序号倒回去了 —— 同一版重试时 ReviewProgressLog.Begin 会丢掉旧缓冲、
            // 把序号从 0 重新开始，于是这一块是<b>整段重发</b>而不是增量。
            // 不清就会把同样的几百行再追加一遍，而这个集合没有上限。
            if (chunk.From < _from)
            {
                Lines.Clear();
                _received = 0;
            }

            foreach (var line in chunk.Lines)
            {
                Lines.Add(line);
                _received++;
            }

            // 上限跟源端的环形缓冲对齐。这个集合原先<b>只增不减</b> ——
            // ReviewProgressLog 那头只留最后 600 行，而这头是按序号取增量，
            // 所以一次十几分钟的评审推进来多少行，这里就攒多少行
            // （claude --output-format stream-json --verbose 会把每个工具返回体
            // 全文打出来，几千行是常态）。而日志面板的每一行都是一个 TextBlock。
            while (Lines.Count > ReviewProgressLog.MaxLines)
            {
                Lines.RemoveAt(0);
            }

            _from = chunk.Next;
            IsEmpty = Lines.Count == 0;

            // 说的是「这次评审一共出过多少行」而不是 Lines.Count —— 后者到了上限就不动了，
            // 于是一条还在跑的评审看起来会像卡在 600 行。
            Status = chunk.Running
                ? string.Create(CultureInfo.InvariantCulture, $"{Reviewer} 正在评 · {_received} 行")
                : string.Create(CultureInfo.InvariantCulture, $"已结束 · 共 {_received} 行");

            if (!chunk.Running)
            {
                _done = true;
                _timer.Stop();
            }
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// 把本机盘上那份显示出来。
    /// </summary>
    /// <returns>本机没有留档时 false。</returns>
    private bool ShowArchived()
    {
        var archived = _archive.Read(_revisionId);
        if (archived is null)
        {
            return false;
        }

        Render(archived.Text);
        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"本机留档 · 共 {_received} 行 · {archived.Path}");
        return true;
    }

    /// <summary>
    /// 把这一版的日志拉到本地存一份。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三步，一步比一步贵：本机盘上有就直接用（评审是本节点跑的，或者以前拉过）；
    /// 知道是谁评的就只问它；连这个都不知道（评审早就结束了，实时状态里那一格是空的）
    /// 才向 mesh 里其余节点挨个问。挨个问是这条需求的关键 ——
    /// <b>「失败或没跑完的评审」恰恰是查不到评审者的那一类</b>。
    /// </para>
    /// <para>
    /// 还在实时跟着一条日志的时候，拉取<b>只存盘、不换掉界面上的内容</b>：
    /// 那会儿面板上滚着的是最新的几行，拿一份快照盖上去只会让人以为评审停了。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task PullAsync()
    {
        var live = !_done;

        if (_archive.Read(_revisionId) is { } local)
        {
            if (!live)
            {
                Render(local.Text);
            }

            Status = $"本机已有留档 · {local.Path}";
            return;
        }

        var peers = Candidates();
        if (peers.Count == 0)
        {
            Status = "mesh 里没有别的节点可问（单机模式或对端都离线）";
            return;
        }

        Status = string.Create(CultureInfo.InvariantCulture, $"正在向 {peers.Count} 个节点要日志…");

        foreach (var peer in peers)
        {
            var text = await _mesh.FetchFullLogAsync(peer, _revisionId, CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var path = _archive.SavePulled(_revisionId, peer.Id, text);

            if (!live)
            {
                Render(text);
            }

            Status = path is null
                ? $"从 {Name(peer.Id)} 拿到了日志，但存不到本地（看进程日志）"
                : $"已从 {Name(peer.Id)} 拉到本地 · {path}";
            return;
        }

        Status = "各节点都没有这一版的日志 —— 本地只留一天，再往前的要 claude --resume 翻会话";
    }

    /// <summary>问谁要。知道评审者就只问它，不知道就问除自己以外的所有人。</summary>
    private IReadOnlyList<Elector> Candidates()
    {
        var others = _mesh.Members.Where(m => m.Id != _mesh.Self.Id).ToList();

        if (_reviewerId is null)
        {
            return others;
        }

        // 知道是谁评的就只问它 —— 除非它已经不在成员表里了（下线、换了机器），
        // 那就退回挨个问：日志在盘上留着，问得到的概率比不问高。
        var reviewer = others.Where(m => m.Id == _reviewerId).ToList();
        return reviewer.Count > 0 ? reviewer : others;
    }

    /// <summary>
    /// 把一整份日志文本铺到面板上。
    /// </summary>
    /// <remarks>
    /// 只铺最后 <see cref="ReviewProgressLog.MaxLines"/> 行，理由跟增量那条路一样：
    /// 面板的每一行都是一个 TextBlock，而一份完整日志几千行是常态。
    /// 全文在文件里，<see cref="Status"/> 上写着路径。
    /// </remarks>
    private void Render(string text)
    {
        var all = text.ReplaceLineEndings("\n").Split('\n');

        Lines.Clear();
        foreach (var line in all.Skip(Math.Max(0, all.Length - ReviewProgressLog.MaxLines)))
        {
            if (line.Length > 0)
            {
                Lines.Add(line);
            }
        }

        _received = all.Count(l => l.Length > 0);
        IsEmpty = Lines.Count == 0;
    }

    private async Task<LogChunk?> FetchAsync()
    {
        var peer = _mesh.Members.FirstOrDefault(m => m.Id == _reviewerId);
        return peer is null
            ? null
            : await _mesh.FetchLogAsync(peer, _revisionId, _from, CancellationToken.None)
                .ConfigureAwait(true);
    }

    private string Name(string electorId)
    {
        var peer = _mesh.Members.FirstOrDefault(m => m.Id == electorId);
        return peer is null || string.IsNullOrWhiteSpace(peer.AzIdentity)
            ? electorId[..Math.Min(8, electorId.Length)]
            : Labels.ShortAccount(peer.AzIdentity);
    }
}
