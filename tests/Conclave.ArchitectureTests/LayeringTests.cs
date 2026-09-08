using NetArchTest.Rules;
using Shouldly;

namespace Conclave.ArchitectureTests;

/// <summary>
/// 分层依赖只能朝内。
/// </summary>
/// <remarks>
/// 为什么要守卫而不是靠 reviewer：这四条里最容易破的是「Application 不引 Avalonia」。
/// 无头模式（<c>conclave review &lt;pr-id&gt;</c>）和将来的 mesh worker 都在没有 UI 的进程里
/// 跑 <c>DiscoveryService</c> 与 <c>ReviewOrchestrator</c>；一旦某个服务为了弹个提示
/// 引了 <c>Avalonia.Threading.Dispatcher</c>，无头进程会在运行时才炸 —— 编译通过、
/// 测试通过、只有真正跑无头时才挂。
/// </remarks>
public class LayeringTests
{
    [Fact]
    public void Domain_depends_on_nothing_of_ours()
    {
        var result = Types.InAssembly(Layers.Domain)
            .ShouldNot()
            .HaveDependencyOnAny("Conclave.Application", "Conclave.Infrastructure", "Conclave.App")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure()
    {
        var result = Types.InAssembly(Layers.Application)
            .ShouldNot()
            .HaveDependencyOnAny("Conclave.Infrastructure", "Conclave.App")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_does_not_depend_on_any_ui_framework()
    {
        var result = Types.InAssembly(Layers.Application)
            .ShouldNot()
            .HaveDependencyOnAny("Avalonia", "CommunityToolkit.Mvvm")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Domain_and_Application_do_not_reach_for_a_database_driver()
    {
        foreach (var layer in new[] { Layers.Domain, Layers.Application })
        {
            var result = Types.InAssembly(layer)
                .ShouldNot()
                .HaveDependencyOnAny("Microsoft.Data.Sqlite", "Microsoft.EntityFrameworkCore")
                .GetResult();

            result.FailingTypeNames.ShouldBeNull();
        }
    }

    [Fact]
    public void Ports_live_in_one_namespace()
    {
        // 端口散落各处会让「外部依赖有哪些」这个问题失去唯一答案。
        var result = Types.InAssembly(Layers.Application)
            .That().AreInterfaces().And().HaveNameStartingWith("I")
            .And().DoNotHaveName("IElectorAllowList")
            .Should().ResideInNamespace("Conclave.Application.Ports")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }
}
