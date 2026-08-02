using System.Reflection;
using System.Runtime.InteropServices;
using DependencyLoaderMod.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using System.Text.Json.Nodes;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace DependencyLoaderMod;

/// <summary>
/// Mod metadata - replaces package.json.
/// 模组元数据 - 替代 package.json。
/// Registers global AppDomain assembly resolve events to load dependency DLLs from directories
/// registered by other mods via <see cref="DependencyLoadService"/>.
/// 注册全局 AppDomain 程序集解析事件，从其他模组通过 <see cref="DependencyLoadService"/> 注册的目录加载依赖 DLL。
/// </summary>
public record ModMetadata : IModMetadata
{
    static ModMetadata()
    {
        var modDir = Path.GetDirectoryName(typeof(ModMetadata).Assembly.Location);
        if (modDir is null) return;

        // Register our own dependencies/ directory FIRST so that AssemblyResolve can find
        // NuGet.Protocol.dll and other transitives before SPT's DI scanner inspects our types.
        // 首先注册自身模组的 dependencies/ 目录，确保在 SPT 的 DI 扫描器检查我们的类型之前，
        // AssemblyResolve 就能找到 NuGet.Protocol.dll 和其他传递依赖。
        var ownDepsDir = Path.Combine(modDir, "dependencies");
        if (Directory.Exists(ownDepsDir))
        {
            DependencyLoadService.AddResolveDirectory(ownDepsDir);
        }

        // Managed assembly resolver: searches all registered resolve directories
        // 托管程序集解析器：在所有已注册的解析目录中查找
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var assemblyName = new AssemblyName(args.Name).Name + ".dll";

            foreach (var dir in DependencyLoadService.ResolvedDirectories)
            {
                var assemblyPath = Path.Combine(dir, assemblyName);
                if (File.Exists(assemblyPath))
                {
                    return Assembly.LoadFrom(assemblyPath);
                }
            }

            return null;
        };

        // Native assembly resolver: intercepts DllImport for registered native libraries
        // 原生程序集解析器：拦截已注册原生库的 DllImport
        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
        {
            var loadedAssemblyName = args.LoadedAssembly.GetName().Name;

            if (loadedAssemblyName is not null &&
                DependencyLoadService.NativeResolveMap.TryGetValue(loadedAssemblyName, out var nativeInfo))
            {
                NativeLibrary.SetDllImportResolver(args.LoadedAssembly, (name, assembly, path) =>
                {
                    if (name == nativeInfo.NativeDllName)
                    {
                        if (File.Exists(nativeInfo.NativeDllPath))
                        {
                            return NativeLibrary.Load(nativeInfo.NativeDllPath);
                        }
                    }
                    return IntPtr.Zero;
                });
            }
        };
    }

    public string ModGuid { get; init; } = "com.whfwtf.dependency-loader-mod";
    public string Name { get; init; } = "DependencyLoaderMod";
    public string Author { get; init; } = "WHFWTF";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("0.1.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

/// <summary>
/// Main plugin entry point that runs after mod metadata is loaded.
/// 模组主入口点，在模组元数据加载后运行。
/// Registers its own ./dependencies/ directory and logs successful load.
/// 注册自身模组的 ./dependencies/ 目录并输出加载成功日志。
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload)]
public class DependencyLoaderModPlugin(
    ISptLogger<DependencyLoaderModPlugin> logger
) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // 4.1 已移除 ServiceLocator，改为注入日志到静态服务
        DependencyLoadService.Logger = logger;

        // Register this mod's own ./dependencies/ directory if it exists
        // 注册自身模组的 ./dependencies/ 目录（如果存在）
        var modDir = Path.GetDirectoryName(typeof(ModMetadata).Assembly.Location);
        if (modDir is not null)
        {
            var ownDepsDir = Path.Combine(modDir, "dependencies");
            DependencyLoadService.AddResolveDirectory(ownDepsDir);
        }

        // Load NuGet fallback config
        // 加载 NuGet 回退配置
        try
        {
            var configPath = Path.Combine(modDir, "config.json");
            if (File.Exists(configPath))
            {
                var configJson = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken));
                if (configJson is not null)
                {
                    var enableFallback = configJson["enableNugetFallback"]?.GetValue<bool>() ?? true;
                    var nugetSource = configJson["nugetSource"]?.GetValue<string>() ?? "";
                    var enableProgress = configJson["enableProgressBar"]?.GetValue<bool>() ?? true;

                    DependencyLoaderMod.Services.DependencyLoadService.EnableNugetFallback = enableFallback;
                    DependencyLoaderMod.Services.DependencyLoadService.ConfiguredNugetSource = string.IsNullOrWhiteSpace(nugetSource) ? null : nugetSource;
                    DependencyLoaderMod.Services.DependencyLoadService.EnableProgressBar = enableProgress;

                    logger.Info($"[DependencyLoaderMod] NuGet fallback config: enabled={enableFallback}, source={(string.IsNullOrWhiteSpace(nugetSource) ? "auto" : nugetSource)}, progressBar={enableProgress}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[DependencyLoaderMod] Failed to load NuGet fallback config: {ex.Message}");
        }

        logger.Success("DependencyLoaderMod loaded successfully!");
    }
}
