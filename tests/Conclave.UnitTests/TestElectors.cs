using Conclave.Domain;

namespace Conclave.UnitTests;

internal static class TestElectors
{
    internal static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    internal static Elector Make(
        string id,
        string azIdentity = "someone",
        string[]? repos = null,
        string[]? projects = null,
        int running = 0,
        int reviews24h = 0,
        int maxConcurrent = 2,
        DateTimeOffset? heartbeat = null) => new()
        {
            Id = id,
            PublicKey = "pk-" + id,
            AzIdentity = azIdentity,
            Repos = repos ?? ["cms-apostrophe"],
            Projects = projects ?? ["liontrip-cms"],
            RunningJobs = running,
            Reviews24h = reviews24h,
            MaxConcurrent = maxConcurrent,
            LastHeartbeat = heartbeat ?? Now,
        };

    internal static PrMeta Pr(
        int id = 2721,
        string author = "LIONMAIL\\youngsun",
        bool draft = false,
        int files = 3,
        int lines = 40,
        string[]? paths = null) => new()
        {
            PrId = id,
            Project = "liontrip-cms",
            Repo = "cms-apostrophe",
            Title = "feat: something",
            Author = author,
            SrcCommit = "dc1d1d474a470955b9093e0867362d0d4ec7161a",
            IsDraft = draft,
            SourceBranch = "feature/x",
            TargetBranch = "develop",
            FilesChanged = files,
            LinesChanged = lines,
            ChangedPaths = paths ?? ["src/Foo.cs"],
        };
}
