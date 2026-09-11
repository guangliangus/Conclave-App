using CommunityToolkit.Mvvm.ComponentModel;

namespace Conclave.App.ViewModels;

/// <summary>
/// 表格该显示几列 —— 跟着窗口宽度走。
/// </summary>
/// <remarks>
/// <para>
/// 表格的列宽是写死的像素值（表头和行必须用同一串，否则会错列），窗口一窄就只能压
/// 那个吃剩余宽度的列，把它挤成一条缝。所以窄下来的时候改成<b>少显示几列</b>，
/// 按重要性让位。PR 队列的次序是：
/// </para>
/// <list type="table">
/// <item>
/// <term>一直显示</term>
/// <description>PR 号 · 作者 · 状态 · 描述（外加那个「⋯」动作按钮）</description>
/// </item>
/// <item><term><see cref="Mid"/> 起</term><description>再加 结论</description></item>
/// <item><term><see cref="Wide"/> 起</term><description>再加 仓库</description></item>
/// </list>
/// <para>
/// <b>让位的只能是定宽的列。</b>每张表都有一列是 <c>DockPanel</c> 的 LastChildFill
/// （PR 表是描述，评审记录是评审，节点表是节点），它吃的是「别人分完剩下的」——
/// 把它按断点藏掉，剩余宽度不会回流给别的列，那一片就是纯死区。
/// 所以这两个断点只用来砍定宽列，fill 那一列一直在，窄了就是被压窄、截断，
/// 而截断的一句话还剩半句可读，空白则什么都不是。
/// </para>
/// <para>
/// 只有两个断点，不是每列一个：断点越多越难说清「现在为什么少了一列」，
/// 而窗口宽度是人自己拖出来的，他需要的是「拖窄了会掉哪些列」这种可预期的行为。
/// </para>
/// <para>
/// 一个实例被所有行共享（<see cref="MainViewModel.Layout"/>），行 VM 只是把它转出去 ——
/// 每行各存一份布局状态的话，改一次宽度要通知几十个对象。
/// </para>
/// </remarks>
public sealed partial class TableLayout : ObservableObject
{
    /// <summary>结论、TOKEN、在跑、心跳在这个宽度以下让位。</summary>
    private const double MidAt = 820;

    /// <summary>仓库、问题数、近 24H、权重在这个宽度以下让位。</summary>
    private const double WideAt = 1040;

    /// <summary>窗口当前宽度，由 <c>MainWindow.SizeChanged</c> 推进来。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mid))]
    [NotifyPropertyChangedFor(nameof(Wide))]
    public partial double Width { get; set; } = 1100;

    /// <summary>够宽，描述与结论这一档可以留着。</summary>
    public bool Mid => Width >= MidAt;

    /// <summary>很宽，连仓库与那几个分解数字一起留着。</summary>
    public bool Wide => Width >= WideAt;
}
