using Conclave.Application;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conclave.UnitTests;

/// <summary>
/// 一个临时的 <c>~/.conclave</c>，用完即删。
/// </summary>
/// <remarks>
/// 落盘那几条（<see cref="ReviewLogArchive"/> 的留档与保留期）没法只在内存里测 ——
/// 它们要钉的恰恰是「文件在不在、什么时候被删掉」。所以每个用例一个独立目录，
/// 而不是共用一个：并行跑的用例共用目录时，<c>Sweep</c> 会把别人的文件也扫掉。
/// </remarks>
internal sealed class TempHome : IDisposable
{
    public TempHome(string tag)
    {
        Options = new ConclaveOptions
        {
            HomeDirectory = Path.Combine(
                Path.GetTempPath(),
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"conclave-{tag}-{Guid.NewGuid():N}")),
        };

        Archive = new ReviewLogArchive(Options, NullLogger<ReviewLogArchive>.Instance);
    }

    public ConclaveOptions Options { get; }

    public ReviewLogArchive Archive { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Options.HomeDirectory))
            {
                Directory.Delete(Options.HomeDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不该让测试变红。
        }
    }
}
