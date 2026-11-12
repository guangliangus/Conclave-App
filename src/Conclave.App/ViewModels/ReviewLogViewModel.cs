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
/// 跟上的那条日志报 <see cref="LogChunk.Running"/> = false 就停轮询 —— 评完了日志不再长，
/// 继续问只是白发请求。
/// </para>
/// <para>
/// <b>「此刻没有实时日志可跟」不等于「评完了」。</b> 面板打开那一刻，表里那一行可能还是开评之前的
/// 样子（评审者为空 —— 飞书「开始评审」的深链常常比表格先到），本机的内存缓冲也可能还没建
/// （<see cref="ReviewProgressLog.Read"/> 对不存在的 revision 给的是空块、<c>Running: false</c>）。
/// 早先这两种都被当成收尾、定时器永久停掉，面板从此冻在一份留档快照上，只能靠手点「拉取日志」刷新。
/// 所以还没跟上时每一轮都重新问一次「现在谁在评」，只有真跟上过一条日志、它自己报了收尾才停。
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
    private readonly Func<string?> _reviewerOf;
    private readonly DispatcherTimer _timer;

    /// <summary>当前跟的评审者。还没跟上实时日志之前每轮都按 <see cref="_reviewerOf"/> 刷新。</summary>
    private string? _reviewerId;

    private long _from;
    private bool _polling;

    /// <summary>这次评审一共收到过多少行。<see cref="Lines"/> 有上限，这个没有。</summary>
    private long _received;

    /// <summary>已经跟上了一条实时日志。之后不再换评审者，直到它报收尾。</summary>
    private bool _following;

    /// <summary><see cref="Lines"/> 里现在是一份留档快照。切到实时之前要先清掉，否则同样的行会出现两遍。</summary>
    private bool _showingArchive;

    /// <summary>跟上的那条日志已经收尾。恢复时不必再起定时器。</summary>
    private bool _done;

    /// <summary>面板被隐藏了。见 <see cref="Suspend"/>。</summary>
    private bool _suspended;

    /// <remarks>
    /// <c>reviewerOf</c> 是「现在谁在评这一版」，每轮现问。不能是打开面板那一刻的快照：
    /// 那一刻表里那一行可能还没反映出评审已经开始。
    /// </remarks>
    public ReviewLogViewModel(
        IMesh mesh,
        ReviewProgressLog local,
        ReviewLogArchive archive,
        string revisionId,
        Func<string?> reviewerOf,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        ArgumentNullException.ThrowIfNull(reviewerOf);

        _mesh = mesh;
        _local = local;
        _archive = archive;
        _revisionId = revisionId;
        _reviewerOf = reviewerOf;
        _reviewerId = reviewerOf();

        Subject = subject;
        Reviewer = DisplayName(_reviewerId);

        Status = "正在取日志…";

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = PollAsync();
        _timer.Start();

        _ = PollAsync();
    }

    /// <summary>标题上那句：哪个仓库的哪个 PR。</summary>
    public string Subject { get; }

    /// <summary>谁在评。</summary>
    [ObservableProperty]
    public partial string Reviewer { get; set; }

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
            // 还没跟上时每轮都重新问一次：评审可能刚开始，表里那一行还没跟上。
            if (!_following)
            {
                Retarget(_reviewerOf());
            }

            var chunk = _reviewerId is null
                ? null
                : _reviewerId == _mesh.Self.Id
                    ? _local.Read(_revisionId, _from)
                    : await FetchAsync().ConfigureAwait(true);

            if (!_following && (chunk is null || (!chunk.Running && chunk.Lines.Count == 0)))
            {
                // 此刻没有实时日志可跟：还没开跑、表里的评审者还是旧的，或者早就评完了。
                // 都不是「结束」—— 先把盘上那份摆出来，定时器接着转，开跑了自动切到实时。
                Wait();
                return;
            }

            if (chunk is null)
            {
                // 跟到一半对端没了（离线、重启、暂时连不上）。留着已经收到的行，下一轮接着问 ——
                // 评审多半还在跑，只是这一跳没问到。
                Status = $"{Reviewer} 暂时取不到实时日志，正在重试 · 已收到 {_received} 行";
                return;
            }

            if (!_following)
            {
                _following = true;

                // 留档和实时缓冲记的是同一批行，接在快照后面追加会重一遍。
                if (_showingArchive)
                {
                    Lines.Clear();
                    _received = 0;
                    _showingArchive = false;
                }
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

    /// <summary>换跟的评审者（只在还没跟上实时日志时）。</summary>
    private void Retarget(string? reviewerId)
    {
        if (string.Equals(reviewerId, _reviewerId, StringComparison.Ordinal))
        {
            return;
        }

        _reviewerId = reviewerId;
        Reviewer = DisplayName(reviewerId);
    }

    /// <summary>
    /// 没有实时日志可跟时的样子：本机有留档就摆出来，没有就说清楚在等什么。
    /// </summary>
    /// <remarks>
    /// 留档只铺一次：它会随评审往后长，但这时候人等的是实时那条路，
    /// 每两秒把整份文件重读重铺一遍既费又闪。想要最新的全文点「拉取日志」。
    /// </remarks>
    private void Wait()
    {
        if (_showingArchive || ShowArchived())
        {
            return;
        }

        Status = _reviewerId is null
            ? "还没有节点在评这一版 —— 开评后这里会自动显示；早就评完了的话点「拉取日志」去其它节点找"
            : $"等 {Reviewer} 开始输出日志…";
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
        var live = _following && !_done;

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
        _showingArchive = true;
    }

    private async Task<LogChunk?> FetchAsync()
    {
        var peer = _mesh.Members.FirstOrDefault(m => m.Id == _reviewerId);
        return peer is null
            ? null
            : await _mesh.FetchLogAsync(peer, _revisionId, _from, CancellationToken.None)
                .ConfigureAwait(true);
    }

    private string DisplayName(string? electorId)
        => electorId is null
            ? "—"
            : electorId == _mesh.Self.Id
                ? "本节点"
                : Name(electorId);

    private string Name(string electorId)
    {
        var peer = _mesh.Members.FirstOrDefault(m => m.Id == electorId);
        return peer is null || string.IsNullOrWhiteSpace(peer.AzIdentity)
            ? electorId[..Math.Min(8, electorId.Length)]
            : Labels.ShortAccount(peer.AzIdentity);
    }
}
