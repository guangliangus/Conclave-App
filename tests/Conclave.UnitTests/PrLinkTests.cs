using Conclave.Domain;

namespace Conclave.UnitTests;

/// <summary>
/// 「点 PR 号跳浏览器」用的地址。
/// </summary>
/// <remarks>
/// 拼错的后果是一个点开是 404 的链接，而那种错只有人真去点了才发现 ——
/// 界面上看不出任何异常。所以两个来源、缺参数、带斜杠、要转义的名字都钉住。
/// </remarks>
public sealed class PrLinkTests
{
    private static PrMeta Pr(string remoteUrl = "") => new()
    {
        PrId = 4242,
        Project = "Ecom",
        Repo = "checkout",
        Title = "t",
        Author = "someone",
        SrcCommit = new string('a', 40),
        RemoteUrl = remoteUrl,
    };

    [Fact]
    public void The_snapshot_remote_url_wins()
    {
        // 快照里的 remoteUrl 指向的正是评审时真去拉的那个仓库，比按配置拼的更可信。
        var url = PrLink.For(
            Pr("https://tfs.example/Collection/Ecom/_git/checkout"),
            "https://other.example/Collection");

        Assert.Equal("https://tfs.example/Collection/Ecom/_git/checkout/pullrequest/4242", url);
    }

    [Fact]
    public void Without_a_remote_url_it_falls_back_to_the_org_address()
    {
        // az repos pr list 实测不给 remoteUrl，老区块里也没有这个字段。
        var url = PrLink.For(Pr(), "https://tfs.example/Collection");

        Assert.Equal("https://tfs.example/Collection/Ecom/_git/checkout/pullrequest/4242", url);
    }

    [Fact]
    public void A_trailing_slash_does_not_double_up()
        => Assert.Equal(
            "https://tfs.example/Collection/Ecom/_git/checkout/pullrequest/4242",
            PrLink.For(Pr(), "https://tfs.example/Collection/  "));

    [Fact]
    public void Names_with_spaces_are_escaped()
    {
        // 实测这台 ADO Server 上 project 名里有空格（「Lion Travel」这种）。
        var pr = Pr() with { Project = "Lion Travel", Repo = "web site" };

        Assert.Equal(
            "https://tfs.example/Collection/Lion%20Travel/_git/web%20site/pullrequest/4242",
            PrLink.For(pr, "https://tfs.example/Collection"));
    }

    [Fact]
    public void Nothing_to_go_on_yields_null()
    {
        // 宁可让 PR 号点不动（tooltip 说明原因），也不要给一个猜出来的 404 地址 ——
        // 后者比没有链接更糟：人会以为是 PR 被删了。
        Assert.Null(PrLink.For(Pr(), orgUrl: null));
        Assert.Null(PrLink.For(Pr(), "   "));
        Assert.Null(PrLink.For("https://tfs.example/Collection", string.Empty, "repo", 1));
        Assert.Null(PrLink.For("https://tfs.example/Collection", "project", string.Empty, 1));
    }
}
