using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ConsoleAppFramework;
using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.Distribution;
using EmptyEngine.Editor.Inspection;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Editor.ViewModels;
using EmptyEngine.Editor.ViewModels.Build;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>生成エディタ実行ファイルのブートストラップ</summary>
/// <remarks>参照先アセンブリが提供するシリアライザ・インポータ・インスペクタを利用できる。</remarks>
public static class EditorBootstrap
{
    private static Type[]? _concreteTypes;
    private static int _scannedAssemblyCount;

    /// <param name="cliOutput">CLI のヘルプと引数エラーの出力先。省略なら標準出力と標準エラー</param>
    public static void Run(string[] args, Action<string>? cliOutput = null)
    {
        ConsoleApp.Log = cliOutput ?? Console.WriteLine;
        ConsoleApp.LogError = cliOutput ?? Console.Error.WriteLine;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("EmptyEngine", LogLevel.Information);
        BuildServices(builder.Services);

        using WebApplication web = builder.Build();
        var cli = ConsoleApp.Create();
        cli.Add("", ([Range(1, 65535)] int uiPort = EditorEnvironment.DefaultUiPort) => EditorWebRunner.Run(web, uiPort));
        cli.Add<Commands>();

        // CLI と UI で同じサービスと寿命を共有する。
        IServiceProvider? previous = ConsoleApp.ServiceProvider;
        ConsoleApp.ServiceProvider = web.Services;
        try
        {
            cli.Run(args, startHost: false, stopHost: false, disposeServiceProvider: false);
        }
        finally
        {
            ConsoleApp.ServiceProvider = previous;
        }
    }

    internal sealed class Commands(
        AssetImportService imports,
        DistributionPipeline distribution,
        EditorStateStore state,
        ILogger<Commands> logger)
    {
        /// <summary>Import all assets.</summary>
        public async Task Import() => await imports.ImportAllAsync();

        /// <summary>Deploy the imported assets to a distribution directory.</summary>
        /// <param name="destination">Distribution directory.</param>
        public void DeployAssets([Argument] string destination) =>
            distribution.Deploy(destination);

        /// <summary>Create a distribution build.</summary>
        /// <param name="name">Name of a declared build. Defaults to the first one.</param>
        /// <param name="output">-o, Output directory. Defaults to the one remembered by the editor.</param>
        public async Task<int> Build([Argument] string? name = null, string? output = null)
        {
            if (!distribution.CanBuild)
            {
                logger.LogError(
                    "No builds declared ({BuildsKey} in the game's *.Editor.targets). Nothing to build.",
                    EditorEnvironment.BuildsKey);
                return 1;
            }

            BuildTarget? target = name is null ? distribution.Targets[0] : distribution.FindTarget(name);
            if (target is null)
            {
                logger.LogError(
                    "No build named '{Name}'. Declared: {Declared}",
                    name, string.Join(", ", distribution.Targets.Select(t => t.Name)));
                return 1;
            }

            string? destination = string.IsNullOrWhiteSpace(output)
                ? state.State.BuildOutputs.GetValueOrDefault(target.Name)
                : output;
            if (string.IsNullOrWhiteSpace(destination))
            {
                logger.LogError(
                    "No destination for build '{Name}'. Pass -o <directory>, " +
                    "or set it once in the editor's Build pane (it is remembered per build).",
                    target.Name);
                return 1;
            }

            await imports.ImportAllAsync();
            await distribution.BuildAsync(target, destination);
            return 0;
        }
    }

    /// <summary>CLI と Web が共有するサービスの登録</summary>
    private static void BuildServices(IServiceCollection services)
    {
        LoadReferencedAssemblies();

        AppContext.SetData(SceneWire.EditorWebSocketAppContextKey, EditorEnvironment.RuntimeHubEndpoint.ToString());

        services.AddSingleton(provider => CreateSingle<IHierarchyBlobSerializer>(provider));
        services.AddSingleton(provider => CreateSingle<ISceneArtifactStore>(provider));
        services.AddSingleton(provider => CreateSingle<IAssetArtifactStore>(provider));
        services.AddSingleton(provider => CreateSingle<IDistributionBuilder>(provider));

        foreach (Type importer in FindImplementations(typeof(IAssetImporter)))
            services.AddSingleton(typeof(IAssetImporter), importer);

        services.AddSingleton(provider => new TypeCatalog(
            EditorEnvironment.TypeCatalogPath, provider.GetRequiredService<ILogger<TypeCatalog>>()));
        services.AddSingleton<ISchemaSource>(provider => provider.GetRequiredService<TypeCatalog>());
        services.AddSingleton(provider => new EditorArtifacts(
            provider.GetRequiredService<ISceneArtifactStore>(),
            provider.GetRequiredService<IAssetArtifactStore>()));
        services.AddSingleton<AssetCatalog>();
        services.AddSingleton(provider =>
        {
            string assetsRoot = EditorEnvironment.AssetsRoot
                ?? throw new InvalidOperationException(
                    $"'{EditorEnvironment.AssetsRootKey}' is not declared. The game's *.Editor.targets must set " +
                    "GamePath and import EmptyEngine.Editor/build/EmptyEngine.Editor.targets (see EditorEnvironment).");
            return new ProjectAssetLayout(
                assetsRoot,
                EditorEnvironment.ReadPackageAssets(provider.GetRequiredService<ILogger<ProjectAssetLayout>>()));
        });
        services.AddSingleton(provider =>
        {
            var artifacts = provider.GetRequiredService<EditorArtifacts>();
            var imports = new AssetImportService(
                provider.GetRequiredService<AssetCatalog>(),
                provider.GetRequiredService<ProjectAssetLayout>().Sources,
                provider.GetServices<IAssetImporter>(),
                EditorEnvironment.ImportStampsDirectory,
                provider.GetRequiredService<ILogger<AssetImportService>>());
            imports.SetArtifacts(artifacts);
            return imports;
        });
        services.AddSingleton<Func<string, EditorArtifacts>>(provider => exeDir => new EditorArtifacts(
            CreateSingle<ISceneArtifactStore>(provider, new DistributionRoot(exeDir)),
            CreateSingle<IAssetArtifactStore>(provider, new DistributionRoot(exeDir))));
        services.AddSingleton(provider => new DistributionPipeline(
            provider.GetRequiredService<ProjectAssetLayout>().AssetsRootPath,
            provider.GetRequiredService<EditorArtifacts>(),
            provider.GetRequiredService<Func<string, EditorArtifacts>>(),
            provider.GetRequiredService<IDistributionBuilder>(),
            provider.GetRequiredService<ILogger<DistributionPipeline>>()));

        services.AddSingleton(provider => new EditorStateStore(
            EditorEnvironment.EditorStatePath, provider.GetRequiredService<ILogger<EditorStateStore>>()));
        services.AddSingleton(provider => new EditHistoryStore(
            EditorEnvironment.EditHistoryDirectory, provider.GetRequiredService<ILogger<EditHistoryStore>>()));
        services.AddSingleton<EditorDispatcher>();
        services.AddSingleton<EditHistoryViewModel>();
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton<BuildViewModel>();
        services.AddSingleton<SourceAssetRefresher>();
        services.AddSingleton(_ => new InspectorRegistry(FindAnnotated<InspectorForAttribute>()));
        services.AddSingleton(provider => EditorWebRunner.CreateSceneClient(
            provider.GetRequiredService<EditorViewModel>(), provider.GetRequiredService<EditorDispatcher>(),
            provider.GetRequiredService<IHierarchyBlobSerializer>(), provider.GetRequiredService<AssetCatalog>(),
            provider.GetRequiredService<AssetImportService>(), provider.GetRequiredService<ProjectAssetLayout>(),
            provider.GetRequiredService<EditHistoryViewModel>(),
            provider.GetRequiredService<EditHistoryStore>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EditorWebRunner)),
            provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton(provider => EditorWebRunner.CreatePolling(
            provider.GetRequiredService<SceneClient>(), provider.GetRequiredService<EditorViewModel>(),
            provider.GetRequiredService<EditorDispatcher>(),
            provider.GetRequiredService<IHierarchyBlobSerializer>(),
            provider.GetRequiredService<ILogger<RuntimeScenePoller>>()));
        services.AddRazorComponents().AddInteractiveServerComponents();

        EditorWebRunner.AddEditorApi(services);
    }

    private static void LoadReferencedAssemblies()
    {
        foreach (string dll in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            try { Assembly.LoadFrom(dll); }
            catch { }
        }
    }

    private static T CreateSingle<T>(IServiceProvider provider, params object[] args) where T : class
    {
        Type[] candidates = FindImplementations(typeof(T));
        if (candidates.Length == 0)
            throw new InvalidOperationException("No implementation found for " + typeof(T).FullName + ".");
        if (candidates.Length > 1)
            throw new InvalidOperationException("Multiple implementations found for " + typeof(T).FullName + ": " + string.Join(", ", candidates.Select(t => t.FullName)));
        return (T)ActivatorUtilities.CreateInstance(provider, candidates[0], args);
    }

    /// <summary>属性 <typeparamref name="TAttribute"/> の付いた型をロード済みアセンブリから集める</summary>
    /// <remarks>対象の型と属性を返す。型のインスタンスは作成しない。</remarks>
    internal static (Type Component, TAttribute[] Attributes)[] FindAnnotated<TAttribute>()
        where TAttribute : Attribute
    {
        return ConcreteTypes()
            .Select(type => (Component: type, Attributes: type.GetCustomAttributes<TAttribute>(inherit: false).ToArray()))
            .Where(found => found.Attributes.Length > 0)
            .OrderBy(found => found.Component.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static Type[] FindImplementations(Type contract)
    {
        return ConcreteTypes()
            .Where(contract.IsAssignableFrom)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>ロード済みアセンブリの具象型</summary>
    private static Type[] ConcreteTypes()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (_concreteTypes is { } cached && _scannedAssemblyCount == assemblies.Length) return cached;

        Type[] types = assemblies
            .SelectMany(GetTypesSafe)
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .ToArray();

        _scannedAssemblyCount = assemblies.Length;
        _concreteTypes = types;
        return types;
    }

    private static IEnumerable<Type> GetTypesSafe(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
