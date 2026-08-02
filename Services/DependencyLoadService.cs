using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Utils;

namespace DependencyLoaderMod.Services;

/// <summary>
/// Static service for managing dependency resolution directories and native library mappings.
/// 用于管理依赖解析目录和原生库映射的静态服务。
/// Other mods call <see cref="AddResolveDirectory"/> and <see cref="AddNativeResolve"/> during their own static initialization
/// to register their dependencies before the DependencyLoaderMod's AssemblyResolve events fire.
/// 其他模组在其自身的静态初始化期间调用 <see cref="AddResolveDirectory"/> 和 <see cref="AddNativeResolve"/>
/// 来在 DependencyLoaderMod 的 AssemblyResolve 事件触发前注册它们的依赖。
/// </summary>
public static class DependencyLoadService
{
    /// <summary>
    /// Placeholder type used as ISptLogger&lt;T&gt; type parameter, since static classes cannot be used as generic arguments.
    /// 占位类型，用作 ISptLogger&lt;T&gt; 的类型参数，因为静态类不能用作泛型参数。
    /// </summary>
    private sealed class LogCategory { }

    // Internal storage for managed assembly resolve directories (deduplicated via HashSet)
    // 托管程序集解析目录的内部存储（通过 HashSet 去重）
    internal static readonly HashSet<string> ResolvedDirectories = new(StringComparer.OrdinalIgnoreCase);

    // Internal storage for native library resolve mappings
    // Assembly name → (native DLL name, native DLL path)
    // 原生库解析映射的内部存储
    // 程序集名称 → (原生 DLL 名称, 原生 DLL 路径)
    internal static readonly Dictionary<string, (string NativeDllName, string NativeDllPath)> NativeResolveMap = new();

    // NuGet fallback configuration - set by DependencyLoaderModPlugin.OnLoad()
    // NuGet 回退配置 - 由 DependencyLoaderModPlugin.OnLoad() 设置
    public static bool EnableNugetFallback { get; set; } = true;
    public static string? ConfiguredNugetSource { get; set; }  // null = auto-detect / null = 自动检测
    public static bool EnableProgressBar { get; set; } = true;

    /// <summary>
    /// Global NuGet download cache directory. When set, all NuGet package downloads are stored here first.
    /// If a package already exists in the cache, it is reused rather than re-downloaded.
    /// 全局 NuGet 下载缓存目录。设置后，所有 NuGet 包下载会先存储到此目录。
    /// 如果缓存中已有该包，则直接复用而不重新下载。
    /// Default: user/cache/DLLCache relative to SPT server working directory.
    /// 默认值：相对于 SPT 服务端工作目录的 user/cache/DLLCache。
    /// </summary>
    public static string CacheDirectory { get; set; } = Path.Combine("user", "cache", "DLLCache");

    /// <summary>
    /// Mirror source list for NuGet package downloads. Ordered by priority.
    /// 用于 NuGet 包下载的镜像源列表。按优先级排序。
    /// </summary>
    private static readonly (string Name, string Url)[] NuGetMirrors = new[]
    {
        // China mainland NuGet mirrors, ordered by priority and reliability.
        // URLs use v3/index.json format for NuGet.Protocol compatibility.
        // Fallback flatcontainer URLs are derived automatically by replacing "v3/index.json" with "v3-flatcontainer/".
        // 中国大陆 NuGet 镜像源，按优先级和可靠性排序。
        // 使用 v3/index.json 格式以兼容 NuGet.Protocol。
        ("Azure China CDN", "https://nuget.cdn.azure.cn/v3/index.json"),
        ("Huawei Cloud", "https://repo.huaweicloud.com/repository/nuget/v3/index.json"),
        ("Tencent Cloud", "https://mirrors.cloud.tencent.com/nuget/v3/index.json"),
        ("TUNA Tsinghua", "https://mirrors.tuna.tsinghua.edu.cn/nuget/v3/index.json"),
        ("Cnblogs", "https://nuget.cnblogs.com/v3/index.json"),
        // Official NuGet source as final fallback
        // 官方 NuGet 源作为最终兜底
        ("NuGet Official", "https://api.nuget.org/v3/index.json")
    };

    private static readonly Lazy<ISptLogger<LogCategory>?> LoggerLazy = new(() =>
    {
        try
        {
            return SPTarkov.Server.Core.DI.ServiceLocator.ServiceProvider?.GetService<ISptLogger<LogCategory>>();
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// Register a managed assembly resolve directory. When the AssemblyResolve event fires,
    /// the service will search for DLLs in this directory.
    /// 注册一个托管程序集解析目录。当 AssemblyResolve 事件触发时，会在此目录中查找 DLL。
    /// </summary>
    /// <param name="directoryPath">Absolute path to the directory containing dependency DLLs / 包含依赖 DLL 的目录的绝对路径</param>
    public static void AddResolveDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        if (!Directory.Exists(directoryPath))
        {
            LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Resolve directory does not exist, skipping: {directoryPath}");
            return;
        }

        var normalizedPath = Path.GetFullPath(directoryPath);
        if (ResolvedDirectories.Add(normalizedPath))
        {
            LoggerLazy.Value?.Info($"[DependencyLoaderMod] Registered resolve directory: {normalizedPath}");
        }
    }

    /// <summary>
    /// Register a native library resolve mapping. When the specified assembly attempts to load the specified native DLL,
    /// the service will load it from the provided path.
    /// 注册原生库解析映射。当指定程序集尝试加载指定原生库时，从指定路径加载。
    /// </summary>
    /// <param name="assemblyName">The assembly name that needs this native library / 需要此原生库的程序集名称</param>
    /// <param name="nativeDllName">The native DLL name to intercept (e.g. "e_sqlite3") / 要拦截的原生 DLL 名称</param>
    /// <param name="nativeDllPath">Absolute path to the native DLL file / 原生 DLL 文件的绝对路径</param>
    public static void AddNativeResolve(string assemblyName, string nativeDllName, string nativeDllPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(nativeDllName) || string.IsNullOrWhiteSpace(nativeDllPath))
            return;

        if (!File.Exists(nativeDllPath))
        {
            LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Native DLL not found, skipping: {nativeDllPath}");
            return;
        }

        NativeResolveMap[assemblyName] = (nativeDllName, Path.GetFullPath(nativeDllPath));
        LoggerLazy.Value?.Info($"[DependencyLoaderMod] Registered native resolve: [{assemblyName}] {nativeDllName} -> {nativeDllPath}");
    }

    /// <summary>
    /// Download a NuGet package to the target directory. Used as a fallback mechanism when dependencies are missing.
    /// 从 NuGet API 下载指定包到目标目录。用于依赖缺失时的回退机制。
    /// </summary>
    /// <param name="packageName">NuGet package name / NuGet 包名称</param>
    /// <param name="version">Package version / 包版本</param>
    /// <param name="targetDir">Target directory to extract the package contents into / 解压包内容的目标目录</param>
    /// <returns>True if the package was downloaded and extracted successfully; otherwise false / 成功下载并解压返回 true；否则返回 false</returns>
    public static async Task<bool> DownloadPackageAsync(string packageName, string version, string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);

            var downloadUrl = $"https://api.nuget.org/v3-flatcontainer/{packageName}/{version}/{packageName}.{version}.nupkg";
            var tempDir = Path.Combine(Path.GetTempPath(), $"DependencyLoaderMod_{packageName}_{version}_{Guid.NewGuid():N}");
            var tempNupkg = Path.Combine(tempDir, $"{packageName}.{version}.nupkg");

            try
            {
                Directory.CreateDirectory(tempDir);

                using var handler = new HttpClientHandler
                {
                    // Disable SSL verification for development environment
                    // 禁用 SSL 验证（开发环境）
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };

                using var httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                LoggerLazy.Value?.Info($"[DependencyLoaderMod] Downloading {packageName} {version} from NuGet...");

                await using (var downloadStream = await httpClient.GetStreamAsync(downloadUrl))
                await using (var fileStream = File.Create(tempNupkg))
                {
                    await downloadStream.CopyToAsync(fileStream);
                }

                LoggerLazy.Value?.Info($"[DependencyLoaderMod] Downloaded {packageName} {version}. Extracting...");

                // Extract the nupkg (which is a ZIP file) to the temp directory
                // 解压 nupkg（实际上是一个 ZIP 文件）到临时目录
                ZipFile.ExtractToDirectory(tempNupkg, tempDir);

                // Find and copy DLLs from lib/net9.0/ or lib/netstandard2.0/ to the target directory root
                // 从 lib/net9.0/ 或 lib/netstandard2.0/ 中找到 DLL 并复制到目标目录根目录
                var libDir = Path.Combine(tempDir, "lib");
                if (Directory.Exists(libDir))
                {
                    var copiedCount = 0;
                    foreach (var frameworkDir in Directory.GetDirectories(libDir))
                    {
                        var frameworkName = Path.GetFileName(frameworkDir);
                        if (frameworkName is "net9.0" or "netstandard2.0")
                        {
                            foreach (var dllFile in Directory.GetFiles(frameworkDir, "*.dll"))
                            {
                                var destFile = Path.Combine(targetDir, Path.GetFileName(dllFile));
                                File.Copy(dllFile, destFile, overwrite: true);
                                copiedCount++;
                                LoggerLazy.Value?.Info($"[DependencyLoaderMod] Copied dependency: {Path.GetFileName(dllFile)}");
                            }
                        }
                    }

                    if (copiedCount == 0)
                    {
                        LoggerLazy.Value?.Warning($"[DependencyLoaderMod] No DLLs found in lib/net9.0/ or lib/netstandard2.0/ for {packageName} {version}.");
                    }
                }
                else
                {
                    LoggerLazy.Value?.Warning($"[DependencyLoaderMod] No lib/ directory found in extracted package {packageName} {version}.");
                }

                LoggerLazy.Value?.Success($"[DependencyLoaderMod] Successfully processed NuGet package: {packageName} {version}");
                return true;
            }
            finally
            {
                // Clean up temporary files
                // 清理临时文件
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception cleanupEx)
                {
                    LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Failed to clean up temp directory {tempDir}: {cleanupEx.Message}");
                }
            }
        }
        catch (HttpRequestException httpEx)
        {
            LoggerLazy.Value?.Warning($"[DependencyLoaderMod] HTTP error downloading {packageName} {version}: {httpEx.Message}");
        }
        catch (TaskCanceledException timeoutEx)
        {
            LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Download timeout for {packageName} {version}: {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Failed to download/extract {packageName} {version}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Detect the best NuGet source URL based on user region and connectivity.
    /// 根据用户区域和网络连通性检测最佳 NuGet 源 URL。
    /// </summary>
    /// <param name="configuredSource">
    /// Optional pre-configured source URL. If provided, it will be used directly without auto-detection.
    /// 可选预设源 URL。如果提供，则直接使用而不进行自动检测。
    /// </param>
    /// <returns>
    /// The detected NuGet source URL, or null if all sources are unreachable.
    /// 检测到的 NuGet 源 URL，如果所有源均不可达则返回 null。
    /// </returns>
    public static async Task<string?> DetectNuGetSourceAsync(string? configuredSource = null)
    {
        // If a source is explicitly configured, use it directly
        // 如果明确配置了源，则直接使用
        if (!string.IsNullOrWhiteSpace(configuredSource))
        {
            Console.Out.WriteLine($"[DependencyLoaderMod] Using configured NuGet source: {configuredSource}");
            return configuredSource;
        }

        // Determine candidate mirrors based on region
        // 根据区域确定候选镜像列表
        (string Name, string Url)[] candidates;

        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            Console.Out.WriteLine($"[DependencyLoaderMod] Detected system region: {region}");

            if (string.Equals(region, "CN", StringComparison.OrdinalIgnoreCase))
            {
                // China region: try mirrors first, fall back to official
                // 中国区域：优先尝试镜像源，最后回退到官方源
                candidates = NuGetMirrors;
            }
            else
            {
                // Non-China region: use official NuGet source directly
                // 非中国区域：直接使用官方 NuGet 源
                candidates = new[] { NuGetMirrors[^1] }; // Last entry is the official source
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"[DependencyLoaderMod] Failed to detect system region: {ex.Message}. Falling back to official NuGet source.");
            candidates = new[] { NuGetMirrors[^1] };
        }

        // Test connectivity for each candidate in priority order
        // Use a known small package URL for connectivity check (NuGet flat container requires a specific package path)
        // 使用一个已知的小包 URL 进行连通性检查（NuGet flat container 需要特定的包路径）
        const string testPackage = "newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

        using var handler = new HttpClientHandler
        {
            // Disable SSL verification for development environment
            // 禁用 SSL 验证（开发环境）
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        foreach (var (name, url) in candidates)
        {
            try
            {
                var baseUrl = url.TrimEnd('/');
                var testUrl = $"{baseUrl}/{testPackage}";
                Console.Out.WriteLine($"[DependencyLoaderMod] Checking NuGet mirror: {name} ({baseUrl}/)");

                using var request = new HttpRequestMessage(HttpMethod.Head, testUrl);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                // Any non-exception response means the host is reachable (NuGet API may return 404 for HEAD, which is still a valid response)
                // 任何非异常响应都意味着主机可达（NuGet API 可能为 HEAD 返回 404，但这仍是有效响应）
                Console.Out.WriteLine($"[DependencyLoaderMod] NuGet mirror selected: {name} ({baseUrl}/)");
                return baseUrl + "/";
            }
            catch (HttpRequestException ex)
            {
                Console.Out.WriteLine($"[DependencyLoaderMod] NuGet mirror {name} unreachable: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                Console.Out.WriteLine($"[DependencyLoaderMod] NuGet mirror {name} timed out (5s)");
            }
        }

        // All sources are unreachable
        // 所有源均不可达
        Console.Out.WriteLine($"[DependencyLoaderMod] WARNING: All NuGet mirrors are unreachable. NuGet fallback will be unavailable.");
        return null;
    }

    /// <summary>
    /// Download a NuGet package with a real-time progress bar showing speed and ETA in the console.
    /// Uses NuGet.Protocol (v3 index.json) first, then falls back to flatcontainer HTTP download.
    /// 从 NuGet 源下载指定包到目标目录，并在控制台显示实时进度条（含下载速度和剩余时间）。
    /// 优先使用 NuGet.Protocol（v3 index.json），失败时回退到 flatcontainer HTTP 下载。
    /// </summary>
    /// <param name="packageName">NuGet package name / NuGet 包名称</param>
    /// <param name="version">Package version / 包版本</param>
    /// <param name="targetDir">Target directory to extract the package contents into / 解压包内容的目标目录</param>
    /// <param name="nugetSourceUrl">
    /// NuGet source URL. Prefers v3/index.json format for NuGet.Protocol;
    /// if flatcontainer format is passed, it is used directly as fallback.
    /// NuGet 源 URL。优先使用 v3/index.json 格式以支持 NuGet.Protocol；
    /// 如果传入 flatcontainer 格式，则直接用作回退下载。
    /// </param>
    /// <param name="showProgress">Whether to show the real-time progress bar in console / 是否在控制台显示实时进度条</param>
    /// <returns>True if the package was downloaded and extracted successfully; otherwise false / 成功下载并解压返回 true；否则返回 false</returns>
    public static async Task<bool> DownloadPackageWithProgressAsync(
        string packageName, string version, string targetDir, string nugetSourceUrl, bool showProgress = true)
    {
        try
        {
            Directory.CreateDirectory(targetDir);

            var downloadUrl = $"{nugetSourceUrl.TrimEnd('/')}/{packageName}/{version}/{packageName}.{version}.nupkg";
            var tempDir = Path.Combine(Path.GetTempPath(), $"DependencyLoaderMod_{packageName}_{version}_{Guid.NewGuid():N}");
            var tempNupkg = Path.Combine(tempDir, $"{packageName}.{version}.nupkg");

            try
            {
                Directory.CreateDirectory(tempDir);

                bool downloaded = false;

                // 1. Try NuGet.Protocol (v3 index.json) first for reliable mirror compatibility
                // 优先尝试 NuGet.Protocol（v3 index.json）以获得可靠的镜像兼容性
                if (nugetSourceUrl.Contains("/v3/index.json", StringComparison.OrdinalIgnoreCase))
                {
                    var indexUrl = nugetSourceUrl.TrimEnd('/');

                    if (showProgress)
                    {
                        Console.Out.WriteLine($"[DependencyLoaderMod] \u2193 {packageName} {version}");
                    }
                    else
                    {
                        LoggerLazy.Value?.Info($"[DependencyLoaderMod] Downloading {packageName} {version} via NuGet.Protocol...");
                    }

                    downloaded = await NuGetDownloadService.TryDownloadPackageAsync(
                        indexUrl, packageName, version, tempNupkg);

                    if (!downloaded)
                    {
                        var fallbackMsg = $"[DependencyLoaderMod] NuGet.Protocol failed for {packageName} {version}. Falling back to flatcontainer...";
                        if (showProgress) Console.Out.WriteLine(fallbackMsg);
                        else LoggerLazy.Value?.Warning(fallbackMsg);
                    }
                }

                // 2. Fallback to flatcontainer HTTP download if protocol failed or URL was already flatcontainer
                // 如果协议下载失败或 URL 本身就是 flatcontainer 格式，回退到 HTTP 下载
                if (!downloaded)
                {
                    using var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    };

                    using var httpClient = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(120) // 2 minute timeout for large packages / 大包 2 分钟超时
                    };

                    if (showProgress)
                    {
                        Console.Out.WriteLine($"[DependencyLoaderMod] \u2193 {packageName} {version}");
                    }
                    else
                    {
                        LoggerLazy.Value?.Info($"[DependencyLoaderMod] Downloading {packageName} {version}...");
                    }

                    // Use ResponseHeadersRead to get Content-Length before full download
                    // 使用 ResponseHeadersRead 在完整下载前获取 Content-Length
                    using var response = await httpClient.SendAsync(
                        new HttpRequestMessage(HttpMethod.Get, downloadUrl),
                        HttpCompletionOption.ResponseHeadersRead
                    );

                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    await using var downloadStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = File.Create(tempNupkg);

                    var buffer = new byte[81920]; // 80KB buffer / 80KB 缓冲区
                    long bytesReadSoFar = 0;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var lastUpdate = stopwatch.ElapsedMilliseconds;

                    // Sliding window for speed calculation (5 samples, 200ms each)
                    // 滑动窗口用于速度计算（5 个采样，每个 200ms）
                    var speedSamples = new (long Bytes, long Timestamp)[5];
                    var sampleIndex = 0;

                    int bytesJustRead;
                    while ((bytesJustRead = await downloadStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesJustRead));
                        bytesReadSoFar += bytesJustRead;
                        var now = stopwatch.ElapsedMilliseconds;

                        // Record sample
                        // 记录采样
                        speedSamples[sampleIndex % speedSamples.Length] = (bytesReadSoFar, now);
                        sampleIndex++;

                        // Update progress bar every ~100ms
                        // 每 ~100ms 更新进度条
                        if (showProgress && now - lastUpdate >= 100)
                        {
                            lastUpdate = now;

                            // Calculate speed from sliding window
                            // 从滑动窗口计算速度
                            var windowStart = Math.Max(0, sampleIndex - speedSamples.Length);
                            var earliestSample = speedSamples[windowStart % speedSamples.Length];
                            var latestSample = speedSamples[(sampleIndex - 1) % speedSamples.Length];

                            double bytesPerMs;
                            double currentSpeedBytesPerSec;
                            if (earliestSample.Timestamp > 0 && latestSample.Timestamp > earliestSample.Timestamp)
                            {
                                var elapsedMs = latestSample.Timestamp - earliestSample.Timestamp;
                                var bytesDelta = latestSample.Bytes - earliestSample.Bytes;
                                bytesPerMs = (double)bytesDelta / elapsedMs;
                                currentSpeedBytesPerSec = bytesPerMs * 1000;
                            }
                            else
                            {
                                bytesPerMs = (double)bytesReadSoFar / Math.Max(1, now);
                                currentSpeedBytesPerSec = bytesPerMs * 1000;
                            }

                            var progressBar = BuildProgressBar(bytesReadSoFar, totalBytes, currentSpeedBytesPerSec);
                            Console.Out.Write($"\r{progressBar}");
                            Console.Out.Flush();
                        }
                    }

                    stopwatch.Stop();

                    if (showProgress)
                    {
                        // Clear the progress bar line
                        // 清除进度条行
                        Console.Out.Write("\r" + new string(' ', Console.BufferWidth > 0 ? Console.BufferWidth - 1 : 80) + "\r");
                        Console.Out.Flush();

                        // Calculate final speed
                        // 计算最终速度
                        var totalSec = stopwatch.Elapsed.TotalSeconds;
                        var finalSpeed = totalSec > 0 ? bytesReadSoFar / totalSec : 0;
                        var sizeStr = FormatSize(bytesReadSoFar);
                        var speedStr = FormatSpeed(finalSpeed);
                        Console.Out.WriteLine($"[DependencyLoaderMod] \u2713 {packageName} {version} ({sizeStr} @ {speedStr})");
                    }
                    else
                    {
                        LoggerLazy.Value?.Info($"[DependencyLoaderMod] Downloaded {packageName} {version} ({bytesReadSoFar} bytes). Extracting...");
                    }
                } // end of if (!downloaded) flatcontainer fallback / 回退下载结束

                // Extraction is shared between NuGet.Protocol and flatcontainer paths
                // The .nupkg file is already at tempNupkg from whichever path succeeded.
                // 解压逻辑在 NuGet.Protocol 和 flatcontainer 两条路径间共享
                // .nupkg 文件已由其中一条路径保存到 tempNupkg

                // Extract the nupkg (which is a ZIP file) to the temp directory
                // 解压 nupkg（实际上是一个 ZIP 文件）到临时目录
                ZipFile.ExtractToDirectory(tempNupkg, tempDir);

                // Find and copy DLLs from lib/ subdirectories to the target directory root
                // 从 lib/ 子目录找到 DLL 并复制到目标目录根目录
                var libDir = Path.Combine(tempDir, "lib");
                if (Directory.Exists(libDir))
                {
                    var copiedCount = 0;
                    foreach (var frameworkDir in Directory.GetDirectories(libDir))
                    {
                        var frameworkName = Path.GetFileName(frameworkDir);
                        // Accept common target frameworks: net9.0, net8.0, net7.0, netstandard2.0, net6.0, net5.0, netcoreapp3.1, net48, net472, net462
                        // 接受常见的 .NET 目标框架
                        if (frameworkName is "net9.0" or "net8.0" or "net7.0" or "netstandard2.0" or "net6.0" or "net5.0" or "netcoreapp3.1" or "net48" or "net472" or "net462")
                        {
                            foreach (var dllFile in Directory.GetFiles(frameworkDir, "*.dll"))
                            {
                                var destFile = Path.Combine(targetDir, Path.GetFileName(dllFile));
                                File.Copy(dllFile, destFile, overwrite: true);
                                copiedCount++;

                                if (showProgress)
                                {
                                    Console.Out.WriteLine($"[DependencyLoaderMod]   Extracting... {frameworkName}/{Path.GetFileName(dllFile)}");
                                }
                                else
                                {
                                    LoggerLazy.Value?.Info($"[DependencyLoaderMod] Copied dependency: {Path.GetFileName(dllFile)}");
                                }
                            }
                        }
                    }

                    if (copiedCount == 0)
                    {
                        var msg = $"[DependencyLoaderMod] No DLLs found in lib/ subdirectories for {packageName} {version}.";
                        if (showProgress) Console.Out.WriteLine(msg);
                        else LoggerLazy.Value?.Warning(msg);
                    }
                }
                else
                {
                    var msg = $"[DependencyLoaderMod] No lib/ directory found in extracted package {packageName} {version}.";
                    if (showProgress) Console.Out.WriteLine(msg);
                    else LoggerLazy.Value?.Warning(msg);
                }

                // Extract native files from runtimes/ (e.g. runtimes/win-x64/native/e_sqlite3.dll)
                // 从 runtimes/ 提取原生文件（例如 runtimes/win-x64/native/e_sqlite3.dll）
                var runtimesDir = Path.Combine(tempDir, "runtimes");
                if (Directory.Exists(runtimesDir))
                {
                    var nativeCopiedCount = 0;
                    foreach (var ridDir in Directory.GetDirectories(runtimesDir))
                    {
                        var ridName = Path.GetFileName(ridDir);
                        var nativeDir = Path.Combine(ridDir, "native");
                        if (Directory.Exists(nativeDir))
                        {
                            // Copy all native files preserving the runtimes/ directory structure
                            // 保持 runtimes/ 目录结构复制所有原生文件
                            var relTargetDir = Path.Combine(targetDir, "runtimes", ridName, "native");
                            Directory.CreateDirectory(relTargetDir);

                            foreach (var nativeFile in Directory.GetFiles(nativeDir))
                            {
                                var destFile = Path.Combine(relTargetDir, Path.GetFileName(nativeFile));
                                File.Copy(nativeFile, destFile, overwrite: true);
                                nativeCopiedCount++;

                                if (showProgress)
                                {
                                    Console.Out.WriteLine($"[DependencyLoaderMod]   Extracting... runtimes/{ridName}/native/{Path.GetFileName(nativeFile)}");
                                }
                                else
                                {
                                    LoggerLazy.Value?.Info($"[DependencyLoaderMod] Copied native dependency: runtimes/{ridName}/native/{Path.GetFileName(nativeFile)}");
                                }
                            }
                        }
                    }

                    if (nativeCopiedCount > 0)
                    {
                        var msg = $"[DependencyLoaderMod] Extracted {nativeCopiedCount} native file(s) from runtimes/ folder.";
                        if (showProgress) Console.Out.WriteLine(msg);
                        else LoggerLazy.Value?.Info(msg);
                    }
                }

                if (showProgress)
                {
                    Console.Out.WriteLine($"[DependencyLoaderMod] Successfully processed NuGet package: {packageName} {version}");
                }
                else
                {
                    LoggerLazy.Value?.Success($"[DependencyLoaderMod] Successfully processed NuGet package: {packageName} {version}");
                }

                return true;
            }
            finally
            {
                // Clean up temporary files
                // 清理临时文件
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception cleanupEx)
                {
                    LoggerLazy.Value?.Warning($"[DependencyLoaderMod] Failed to clean up temp directory {tempDir}: {cleanupEx.Message}");
                }
            }
        }
        catch (HttpRequestException httpEx)
        {
            var msg = $"[DependencyLoaderMod] HTTP error downloading {packageName} {version}: {httpEx.Message}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Warning(msg);
        }
        catch (TaskCanceledException timeoutEx)
        {
            var msg = $"[DependencyLoaderMod] Download timeout for {packageName} {version}: {timeoutEx.Message}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Warning(msg);
        }
        catch (Exception ex)
        {
            var msg = $"[DependencyLoaderMod] Failed to download/extract {packageName} {version}: {ex.Message}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Warning(msg);
        }

        return false;
    }

    /// <summary>
    /// Check if the target directory exists and contains all required dependency DLLs.
    /// 检查目标目录是否存在且包含所有必需的依赖 DLL。
    /// A simple check: directory is not empty and contains at least one .dll file.
    /// 简单检查：目录非空且包含至少一个 .dll 文件。
    /// </summary>
    /// <param name="directoryPath">Path to the dependencies directory / 依赖目录路径</param>
    public static bool HasRequiredDlls(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return false;

        return Directory.GetFiles(directoryPath, "*.dll").Length > 0;
    }

    /// <summary>
    /// Return all NuGet mirror candidates for download. If the user has specified a custom source, use that only.
    /// 返回所有 NuGet 镜像源候选。如果用户指定了自定义源，则仅使用自定义源。
    /// Unlike the old connectivity check, we try all sources and let per-package download handle fallback.
    /// 与旧的连通性检查不同，我们尝试所有源，让逐包下载逻辑处理回退。
    /// This is more robust because a source might have some packages but not others.
    /// 这更健壮，因为某个源可能包含部分包但不包含其他包。
    /// </summary>
    private static Task<List<(string Name, string Url)>> GetReachableSourcesAsync()
    {
        // If user has configured a custom source, use it directly
        // 如果用户配置了自定义源，直接使用
        if (!string.IsNullOrWhiteSpace(ConfiguredNugetSource))
        {
            Console.Out.WriteLine($"[DependencyLoaderMod] Using configured NuGet source: {ConfiguredNugetSource}");
            return Task.FromResult(new List<(string Name, string Url)> { ("Configured Source", ConfiguredNugetSource.TrimEnd('/') + "/") });
        }

        // Return all predefined mirrors - let per-package download handle fallback
        // 返回所有预定义镜像源 - 让逐包下载逻辑处理回退
        var allSources = NuGetMirrors.Select(m => (m.Name, m.Url.TrimEnd('/') + "/")).ToList();

        return Task.FromResult(allSources);
    }

    /// <summary>
    /// Automatically resolve missing NuGet dependencies by reading a mod's deps.json file.
    /// 通过读取模组的 deps.json 文件自动解析缺失的 NuGet 依赖。
    /// </summary>
    /// <param name="depsJsonPath">Path to the mod's *.deps.json file / 模组的 *.deps.json 文件路径</param>
    /// <param name="targetDir">Target directory where dependency DLLs should reside / 依赖 DLL 应存在于的目标目录</param>
    /// <param name="nugetSourceUrl">NuGet source URL (null = auto-detect) / NuGet 源 URL（null 则自动检测）</param>
    /// <param name="showProgress">Whether to show download progress in console / 是否在控制台显示下载进度</param>
    public static async Task AutoResolveFromDepsJsonAsync(
        string depsJsonPath, string targetDir, string? nugetSourceUrl = null, bool showProgress = true)
    {
        if (!File.Exists(depsJsonPath))
        {
            var msg = $"[DependencyLoaderMod] deps.json not found: {depsJsonPath}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Warning(msg);
            return;
        }

        if (!EnableNugetFallback)
        {
            var msg = $"[DependencyLoaderMod] NuGet fallback is disabled. Skipping auto-resolve for {depsJsonPath}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Info(msg);
            return;
        }

        try
        {
            // Detect reachable NuGet sources
            // 检测可用的 NuGet 源
            var candidateSources = await GetReachableSourcesAsync();
            if (candidateSources.Count == 0)
            {
                var msg = $"[DependencyLoaderMod] No NuGet source available. Cannot auto-resolve dependencies from {depsJsonPath}";
                if (showProgress) Console.Out.WriteLine(msg);
                else LoggerLazy.Value?.Warning(msg);
                return;
            }

            var jsonText = await File.ReadAllTextAsync(depsJsonPath);
            var depsJson = JsonNode.Parse(jsonText);
            if (depsJson is null)
            {
                var msg = $"[DependencyLoaderMod] Failed to parse deps.json: {depsJsonPath}";
                if (showProgress) Console.Out.WriteLine(msg);
                else LoggerLazy.Value?.Warning(msg);
                return;
            }

            // Parse libraries section - contains type (package vs project) and NuGet path info
            // 解析 libraries 节 - 包含类型（package vs project）和 NuGet 路径信息
            var libraries = depsJson["libraries"]?.AsObject();
            if (libraries is null || libraries.Count == 0)
            {
                var msg = $"[DependencyLoaderMod] No libraries section found in deps.json: {depsJsonPath}";
                if (showProgress) Console.Out.WriteLine(msg);
                else LoggerLazy.Value?.Warning(msg);
                return;
            }

            // Parse targets section - contains runtime DLL paths for each package
            // 解析 targets 节 - 包含每个包的运行时 DLL 路径
            var targets = depsJson["targets"]?.AsObject();
            JsonNode? targetNode = null;
            if (targets is not null)
            {
                // Use the first target framework (e.g. ".NETCoreApp,Version=v9.0")
                // 使用第一个目标框架
                foreach (var (_, value) in targets)
                {
                    targetNode = value;
                    break;
                }
            }

            if (targetNode is null)
            {
                var msg = $"[DependencyLoaderMod] No targets section found in deps.json: {depsJsonPath}";
                if (showProgress) Console.Out.WriteLine(msg);
                else LoggerLazy.Value?.Warning(msg);
                return;
            }

            // Collect package dependencies: (libraryId, packageName, version, neededDlls)
            // 收集包依赖：(libraryId, packageName, version, neededDlls)
            var missingPackages = new List<(string LibraryId, string PackageName, string Version, string[] NeededDlls)>();

            foreach (var (libraryId, libraryEntry) in libraries)
            {
                var type = libraryEntry["type"]?.GetValue<string>();
                if (type != "package")
                    continue;

                // Skip SPTarkov packages - they are provided by the server itself
                // 跳过 SPTarkov 系列包 - 它们由服务端自身提供
                if (libraryId.StartsWith("SPTarkov.", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get the NuGet path to extract package name and version
                // 获取 NuGet 路径以提取包名和版本
                var nugetPath = libraryEntry["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(nugetPath))
                    continue;

                // Extract version from the library ID (format: "PackageName/Version")
                // 从 library ID 提取版本（格式："PackageName/Version"）
                var parts = libraryId.Split('/');
                if (parts.Length < 2)
                    continue;

                var packageName = parts[0];
                var version = parts[1];

                // Get runtime DLLs from targets section
                // 从 targets 节获取运行时 DLL
                var targetEntry = targetNode[libraryId];
                string[] runtimeDlls;
                if (targetEntry is not null)
                {
                    var dlls = new List<string>();

                    // Check for managed DLLs (runtime section)
                    // 检查托管 DLL（runtime 节）
                    var runtime = targetEntry["runtime"]?.AsObject();
                    if (runtime is not null)
                    {
                        foreach (var (runtimeKey, _) in runtime)
                        {
                            if (runtimeKey.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                dlls.Add(Path.GetFileName(runtimeKey));
                            }
                        }
                    }

                    // Check for native DLLs (runtimeTargets section)
                    // 检查原生 DLL（runtimeTargets 节）
                    var runtimeTargets = targetEntry["runtimeTargets"]?.AsObject();
                    if (runtimeTargets is not null)
                    {
                        foreach (var (runtimeKey, _) in runtimeTargets)
                        {
                            if (runtimeKey.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                runtimeKey.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                                runtimeKey.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                            {
                                // Only check for win-x64 native DLLs (our target platform)
                                // 只检查 win-x64 原生 DLL（我们的目标平台）
                                if (runtimeKey.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                                {
                                    dlls.Add(Path.GetFileName(runtimeKey));
                                }
                            }
                        }
                    }

                    runtimeDlls = dlls.ToArray();
                }
                else
                {
                    runtimeDlls = Array.Empty<string>();
                }

                // Check if this is a meta-package with no DLLs of its own (like Microsoft.Data.Sqlite/9.0.0)
                // which only has dependencies on other packages (Microsoft.Data.Sqlite.Core, SQLitePCLRaw.*)
                // Skip these entirely - their dependent packages (listed separately in deps.json) contain the actual DLLs.
                // 检查是否是仅包含依赖关系的元包（如 Microsoft.Data.Sqlite/9.0.0）
                // 直接跳过 - 其依赖包（deps.json 中单独列出）包含实际 DLL。
                if (runtimeDlls.Length == 0)
                {
                    continue;
                }

                // Check if any of the needed DLLs are missing from targetDir
                // 检查 targetDir 中是否缺少任何必需的 DLL
                var missing = runtimeDlls.Where(dll => !File.Exists(Path.Combine(targetDir, dll))).ToArray();
                if (missing.Length > 0)
                {
                    missingPackages.Add((libraryId, packageName, version, runtimeDlls));
                }
            }

            if (missingPackages.Count == 0)
            {
                var msg = $"[DependencyLoaderMod] All dependencies are already present in {targetDir}";
                if (showProgress) Console.Out.WriteLine(msg);
                else LoggerLazy.Value?.Info(msg);
                return;
            }

            var totalMissing = missingPackages.Count;
            var msgStart = $"[DependencyLoaderMod] Found {totalMissing} missing NuGet package(s). Starting download...";
            if (showProgress) Console.Out.WriteLine(msgStart);
            else LoggerLazy.Value?.Info(msgStart);

            // Download each missing package, trying all reachable sources
            // 依次下载每个缺失的包，尝试所有可用的源
            var downloadedCount = 0;
            foreach (var (_, packageName, version, _) in missingPackages)
            {
                var packageNameLower = packageName.ToLowerInvariant();
                bool success = false;

                foreach (var (sourceName, sourceUrl) in candidateSources)
                {
                    if (showProgress)
                    {
                        Console.Out.WriteLine($"[DependencyLoaderMod]   Trying {sourceName} for {packageName} {version}...");
                    }

                    success = await DownloadPackageWithProgressAsync(
                        packageNameLower, version, targetDir, sourceUrl, showProgress);

                    if (success)
                        break;

                    if (showProgress)
                    {
                        Console.Out.WriteLine($"[DependencyLoaderMod]   {sourceName} failed for {packageName} {version}, trying next source...");
                    }
                }

                if (success)
                {
                    downloadedCount++;
                }
                else
                {
                    var msg = $"[DependencyLoaderMod] Failed to download {packageName} {version} from all sources.";
                    if (showProgress) Console.Out.WriteLine(msg);
                    else LoggerLazy.Value?.Warning(msg);
                }
            }

            var summary = $"[DependencyLoaderMod] Auto-resolve complete: {downloadedCount}/{totalMissing} packages downloaded successfully.";
            if (showProgress) Console.Out.WriteLine(summary);
            else LoggerLazy.Value?.Success(summary);
        }
        catch (Exception ex)
        {
            var msg = $"[DependencyLoaderMod] Error during auto-resolve: {ex.Message}";
            if (showProgress) Console.Out.WriteLine(msg);
            else LoggerLazy.Value?.Error(msg);
        }
    }

    /// <summary>
    /// Build a progress bar string showing progress, size, speed and ETA.
    /// 构建进度条字符串，显示进度、大小、速度和剩余时间。
    /// </summary>
    private static string BuildProgressBar(long bytesRead, long totalBytes, double speedBytesPerSec)
    {
        const int barWidth = 20;
        var progress = totalBytes > 0 ? (double)bytesRead / totalBytes : 0.0;
        var filled = (int)(progress * barWidth);
        var empty = barWidth - filled;

        var bar = new char[barWidth];
        for (var i = 0; i < filled && i < barWidth; i++) bar[i] = '\u2588'; // Full block
        for (var i = filled; i < barWidth; i++) bar[i] = '\u2591'; // Light shade

        var downloadedStr = FormatSize(bytesRead);
        var totalStr = totalBytes > 0 ? FormatSize(totalBytes) : "?";
        var speedStr = FormatSpeed(speedBytesPerSec);

        // Calculate ETA
        // 计算剩余时间
        string etaStr;
        if (totalBytes > 0 && speedBytesPerSec > 0)
        {
            var remainingBytes = totalBytes - bytesRead;
            var etaSec = (int)(remainingBytes / speedBytesPerSec);
            if (etaSec < 60)
                etaStr = $"ETA {etaSec}s";
            else if (etaSec < 3600)
                etaStr = $"ETA {etaSec / 60}m{etaSec % 60}s";
            else
                etaStr = $"ETA >1h";
        }
        else
        {
            etaStr = "ETA --";
        }

        return $"[DependencyLoaderMod]   [{new string(bar)}]  {downloadedStr} / {totalStr}  @ {speedStr}  {etaStr}";
    }

    /// <summary>
    /// Format byte count to human-readable string with automatic unit switching.
    /// 将字节数格式化为人类可读的字符串，自动切换单位。
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    /// <summary>
    /// Format speed (bytes/sec) to human-readable string with automatic unit switching.
    /// 将速度（字节/秒）格式化为人类可读的字符串，自动切换单位。
    /// </summary>
    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
    }
}
