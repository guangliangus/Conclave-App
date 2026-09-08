using System.Windows.Input;
using Conclave.Application;
using Conclave.Domain;

namespace Conclave.App.ViewModels;

/// <summary>
/// PR 队列里的一行。所有字段都预格式化成字符串，让 XAML 里不必写转换器。
/// </summary>
public sealed class PrRow
{
    public PrRow(PrView view, ICommand reviewCommand)
    {
        ArgumentNullException.ThrowIfNull(view);

        View = view;
        ReviewCommand = reviewCommand;

        RevisionId = view.Revision.Id;
        Repo = view.Pr.Repo;
        Project = view.Pr.Project;
        Title = view.Pr.Title;
        Author = view.Pr.Author;
        Stage = view.Stage;
        Branches = $"{view.Pr.SourceBranch} → {view.Pr.TargetBranch}";
        Diff = view.Pr.FilesChanged > 0
            ? $"{view.Pr.FilesChanged} 文件 / {view.Pr.LinesChanged} 行"
            : "—";
        Quorum = view.Quorum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Seat = view.MySeat >= 0
            ? $"round {view.MySeat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "旁观";
        Outcome = view.Decision switch
        {
            null => "—",
            ReviewDecision.Error => "执行失败",
            var d => $"{d}（{view.Findings.ToString(System.Globalization.CultureInfo.InvariantCulture)} 条）",
        };
        CanReview = view.MySeat >= 0 && view.Decision is null;
    }

    public PrView View { get; }

    public ICommand ReviewCommand { get; }

    public string RevisionId { get; }

    public string Repo { get; }

    public string Project { get; }

    public string Title { get; }

    public string Author { get; }

    public string Stage { get; }

    public string Branches { get; }

    public string Diff { get; }

    public string Quorum { get; }

    public string Seat { get; }

    public string Outcome { get; }

    /// <summary>本节点有席位且还没结论时才能手动开跑。</summary>
    public bool CanReview { get; }
}
