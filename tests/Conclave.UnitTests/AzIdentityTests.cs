using Conclave.Domain;

namespace Conclave.UnitTests;

public class AzIdentityTests
{
    [Theory]
    [InlineData("LIONMAIL\\youngsun", "youngsun")]
    [InlineData("youngsun@liontravel.com", "youngsun")]
    [InlineData("youngsun", "youngsun")]
    [InlineData("  LIONMAIL\\YoungSun  ", "youngsun")]
    [InlineData("lionmail/youngsun", "youngsun")]
    public void Normalize_strips_domain_and_lowercases(string raw, string expected)
        => Assert.Equal(expected, AzIdentity.Normalize(raw));

    [Fact]
    public void SamePerson_matches_the_three_forms_azure_devops_uses()
    {
        // 实测这台 ADO Server 的 createdBy.uniqueName 是 NT 域形式，
        // 而 connectionData 给的是裸账号名 —— 两者必须认成同一个人。
        Assert.True(AzIdentity.SamePerson("LIONMAIL\\guangliangli", "guangliangli"));
        Assert.True(AzIdentity.SamePerson("guangliangli@liontravel.com", "LIONMAIL\\guangliangli"));
    }

    [Fact]
    public void SamePerson_is_false_when_either_side_is_missing()
    {
        // az 身份取不到时宁可让自己有资格评审（会被人看出来），也不能误判成「所有 PR 都是自己的」。
        Assert.False(AzIdentity.SamePerson(null, "youngsun"));
        Assert.False(AzIdentity.SamePerson(string.Empty, string.Empty));
    }

    [Fact]
    public void SamePerson_distinguishes_different_people()
        => Assert.False(AzIdentity.SamePerson("LIONMAIL\\youngsun", "LIONMAIL\\guangliangli"));
}
