using System.Globalization;
using System.Windows.Input;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>
/// 一条等本节点确认的指派请求。
/// </summary>
/// <remarks>
/// 用横幅而不是列表里的一行来展示（见 MainWindow.axaml）：它是<b>要人当场做决定</b>的东西，
/// 跟队列那种「看一眼就够」的信息不是一类。混在队列里很容易被划过去，
/// 而请求方那边会一直等着。
/// </remarks>
public sealed class PendingRow
{
    /// <param name="request">这条请求本身。</param>
    /// <param name="accept">同意命令。</param>
    /// <param name="decline">拒绝命令。</param>
    /// <param name="busy">正在答复这一条 —— 两个按钮一起灰掉，见 MainViewModel.RespondAsync。</param>
    public PendingRow(
        AssignmentRequest request, ICommand accept, ICommand decline, bool busy = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        Id = request.Id;
        RevisionId = request.RevisionId;
        From = request.From;
        FromShort = request.From.Length > 8 ? request.From[..8] : request.From;
        Note = request.Note ?? string.Empty;
        HasNote = Note.Length > 0;
        At = request.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Summary = $"{FromShort} 请你评审 {RevisionId}";

        AcceptCommand = accept;
        DeclineCommand = decline;

        // 答复要发一次 HTTP，最长等 Mesh.RequestTimeout。这期间两个按钮一起灰掉：
        // 只灰被点的那个的话，「同意」之后还能点「拒绝」，同一条请求就被答了两遍。
        CanRespond = !busy;
        AcceptTip = busy
            ? "正在答复，等这一下的结果"
            : "接受之后本节点就认领了它，下一轮编排开跑";
        DeclineTip = busy
            ? "正在答复，等这一下的结果"
            : "拒绝之后请求方立刻就能看到，它可以改指派给别的节点";
    }

    public string Id { get; }

    public string RevisionId { get; }

    /// <summary>请求方完整指纹，进 tooltip。</summary>
    public string From { get; }

    public string FromShort { get; }

    public string Note { get; }

    public bool HasNote { get; }

    public string At { get; }

    public string Summary { get; }

    public ICommand AcceptCommand { get; }

    public ICommand DeclineCommand { get; }

    /// <summary>两个按钮灰不灰 —— 上一次答复还没回来时就灰着。</summary>
    public bool CanRespond { get; }

    /// <summary>「同意」的 tooltip；灰着的时候说的是为什么灰。</summary>
    public string AcceptTip { get; }

    /// <summary>「拒绝」的 tooltip。</summary>
    public string DeclineTip { get; }
}
