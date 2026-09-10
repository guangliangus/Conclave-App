using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Conclave.Application;
using Conclave.Application.Ports;

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
/// </remarks>
public sealed partial class ReviewLogViewModel : ViewModelBase, IDisposable
{
    /// <summary>轮询节奏。评审动辄十几分钟，两秒够快，也不会把对端问烦。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IMesh _mesh;
    private readonly ReviewProgressLog _local;
    private readonly string _revisionId;
    private readonly string? _reviewerId;
    private readonly DispatcherTimer _timer;

    private long _from;
    private bool _polling;

    public ReviewLogViewModel(
        IMesh mesh,
        ReviewProgressLog local,
        string revisionId,
        string? reviewerId,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);

        _mesh = mesh;
        _local = local;
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

    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public void Dispose() => _timer.Stop();

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
                Status = _reviewerId is null
                    ? "没有节点在评这一版"
                    : $"{Reviewer} 那边取不到日志（可能已经评完，或者版本较旧）";
                _timer.Stop();
                return;
            }

            foreach (var line in chunk.Lines)
            {
                Lines.Add(line);
            }

            _from = chunk.Next;
            IsEmpty = Lines.Count == 0;

            Status = chunk.Running
                ? string.Create(CultureInfo.InvariantCulture, $"{Reviewer} 正在评 · {Lines.Count} 行")
                : string.Create(CultureInfo.InvariantCulture, $"已结束 · 共 {Lines.Count} 行");

            if (!chunk.Running)
            {
                _timer.Stop();
            }
        }
        finally
        {
            _polling = false;
        }
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
