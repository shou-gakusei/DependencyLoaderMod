using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DependencyLoaderMod.Services;

/// <summary>
/// Downloads NuGet packages using the proper NuGet v3 protocol (index.json-based resource discovery).
/// Uses NuGet.Protocol to query mirror index.json for real package download URLs,
/// which is the same protocol used by 'dotnet restore'.
/// 使用标准 NuGet v3 协议（基于 index.json 的资源发现）下载 NuGet 包。
/// 此协议与 'dotnet restore' 使用的协议相同，比 flatcontainer URL 拼凑更可靠。
/// </summary>
internal static class NuGetDownloadService
{
    private static readonly NuGet.Common.ILogger NuGetLogger = NuGet.Common.NullLogger.Instance;

    /// <summary>
    /// Try to download a NuGet package from a v3-compatible source using the official NuGet.Protocol library.
    /// Queries the source's index.json via <see cref="FindPackageByIdResource"/>, downloads the .nupkg,
    /// and saves it to <paramref name="tempNupkgPath"/>.
    /// 尝试使用官方 NuGet.Protocol 库从 v3 兼容源下载 NuGet 包。
    /// 通过 <see cref="FindPackageByIdResource"/> 查询源的 index.json，下载 .nupkg 并保存到指定路径。
    /// </summary>
    /// <param name="sourceIndexUrl">
    /// The v3 index.json URL of the NuGet source (e.g. "https://api.nuget.org/v3/index.json").
    /// NuGet 源的 v3 index.json URL。
    /// </param>
    /// <param name="packageId">
    /// NuGet package ID (case-insensitive, e.g. "Dapper").
    /// NuGet 包 ID（不区分大小写）。
    /// </param>
    /// <param name="version">
    /// Package version string (e.g. "2.1.35").
    /// 包版本字符串。
    /// </param>
    /// <param name="tempNupkgPath">
    /// Full path where the downloaded .nupkg file should be saved.
    /// 下载的 .nupkg 应保存到的完整路径。
    /// </param>
    /// <param name="ct">Cancellation token / 取消令牌</param>
    /// <returns>True if the package was downloaded successfully; false otherwise / 下载成功返回 true；否则返回 false</returns>
    public static async Task<bool> TryDownloadPackageAsync(
        string sourceIndexUrl,
        string packageId,
        string version,
        string tempNupkgPath,
        CancellationToken ct = default)
    {
        try
        {
            var nugetVersion = new NuGetVersion(version);
            var packageSource = new PackageSource(sourceIndexUrl);
            var sourceRepository = Repository.Factory.GetCoreV3(packageSource);
            var findPackageByIdResource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>(ct);

            // Uses CopyNupkgToStreamAsync which internally queries the index.json resources
            // and picks the correct package download URL from the service index.
            // 使用 CopyNupkgToStreamAsync 内部查询 index.json 资源
            // 并从服务索引中选择正确的包下载 URL。
            var cacheContext = new SourceCacheContext();
            using var fileStream = new FileStream(tempNupkgPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var success = await findPackageByIdResource.CopyNupkgToStreamAsync(
                packageId,
                nugetVersion,
                fileStream,
                cacheContext,
                NuGetLogger,
                ct);

            return success;
        }
        catch (Exception ex)
        {
            // Log at debug level - caller handles retry/fallback
            // 在调试级别记录 - 调用方处理重试/回退
            System.Diagnostics.Debug.WriteLine(
                $"[NuGetDownloadService] Failed to download {packageId} {version} from {sourceIndexUrl}: {ex.Message}");
            return false;
        }
    }
}
