using Conclave.Domain;

namespace Conclave.UnitTests;

internal static class TestElectors
{
    internal static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    internal static Elector Make(
        string id,
        string azIdentity = "someone",
        string[]? projects = null,
        int running = 0,
        int reviews24h = 0,
        int maxConcurrent = 2,
        double utilization = 0,
        DateTimeOffset? heartbeat = null,
        string appVersion = "1.0.0-test") => new()
        {
            Id = id,
            PublicKey = "pk-" + id,
            AzIdentity = azIdentity,
            Projects = projects ?? ["liontrip-cms"],
            RunningJobs = running,
            Reviews24h = reviews24h,
            MaxConcurrent = maxConcurrent,
            Utilization = utilization,
            LastHeartbeat = heartbeat ?? Now,
            ProtocolVersion = Beacon.ProtocolVersion,
            AppVersion = appVersion,
        };

    internal static PrMeta Pr(
        int id = 2721,
        string author = "LIONMAIL\\youngsun",
        bool draft = false,
        int files = 3,
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
            ChangedPaths = paths ?? ["src/Foo.cs"],
            RemoteUrl = "https://az.invalid/LionTechShanghai/liontrip-cms/_git/cms-apostrophe",
        };
}
