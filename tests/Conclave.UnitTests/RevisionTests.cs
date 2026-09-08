using Conclave.Domain;

namespace Conclave.UnitTests;

public class RevisionTests
{
    [Fact]
    public void Id_combines_pr_and_short_commit()
    {
        var rev = new Revision("liontrip-cms", 2721, "dc1d1d474a470955b9093e0867362d0d4ec7161a");

        Assert.Equal("2721@dc1d1d47", rev.Id);
        Assert.Equal(("liontrip-cms", 2721), rev.PullRequest);
    }

    [Fact]
    public void New_push_produces_a_different_revision_on_the_same_chain()
    {
        var before = new Revision("p", 1, "aaaaaaaaaaaaaaaa");
        var after = new Revision("p", 1, "bbbbbbbbbbbbbbbb");

        // 幂等键变了 —— 所以作者 push 之后会自动重评。
        Assert.NotEqual(before.Id, after.Id);

        // 但仍指向同一个 PR —— 编排层据此只处理最新那个未结论的版本。
        Assert.Equal(before.PullRequest, after.PullRequest);
    }

    [Fact]
    public void Short_commit_tolerates_a_commit_shorter_than_eight_chars()
    {
        var rev = new Revision("p", 1, "abc");
        Assert.Equal("1@abc", rev.Id);
    }
}
