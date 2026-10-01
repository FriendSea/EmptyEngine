using EmptyEngine.Core;
using EmptyEngine.Editor.Api;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.Sync;
using EmptyEngine.Editor.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using System.Threading.Channels;

namespace EmptyEngine.Editor.Hosting;

/// <summary>エディタ UI のローカル web サーバとしての起動</summary>
internal static class EditorWebRunner
{
    /// <summary>エディタ操作 API とリファレンス画面の基点パス。</summary>
    internal const string ApiPrefix = "/api/editor";

    /// <summary>listen 完了後に stdout へ出す名乗りの 1 行</summary>
    internal const string ListeningPrefix = "Editor UI listening on ";

    /// <summary>ランタイムへ状態を問い合わせる間隔の既定値</summary>
    public static TimeSpan DefaultPollingInterval { get; } = TimeSpan.FromMilliseconds(400);

    public static void Run(WebApplication app, int uiPort = EditorEnvironment.DefaultUiPort)
    {
        app.Urls.Clear();
        app.Urls.Add(EditorEnvironment.UiUrl(uiPort));
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EditorWebRunner));
        var catalog = app.Services.GetRequiredService<AssetCatalog>();
        var viewModel = app.Services.GetRequiredService<EditorViewModel>();
        var dispatcher = app.Services.GetRequiredService<EditorDispatcher>();
        var stateStore = app.Services.GetRequiredService<EditorStateStore>();
        var historyStore = app.Services.GetRequiredService<EditHistoryStore>();
        var polling = app.Services.GetRequiredService<RuntimeScenePoller>();
        app.Services.GetRequiredService<SourceAssetRefresher>().StartWatching();
        CancellationToken stopping = app.Lifetime.ApplicationStopping;

        using CancellationTokenRegistration saveState = stopping.Register(stateStore.Save);

        // 埋め込み先が未指定でも無制限にならないよう、CSP を常に設定する。
        string frameAncestors = BuildFrameAncestors(EditorEnvironment.FrameAncestors);

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Remove("X-Frame-Options");
                context.Response.Headers["Content-Security-Policy"] = frameAncestors;
                return Task.CompletedTask;
            });
            await next();
        });

        app.UseAntiforgery();
        app.MapGet("/healthz", () => Results.Ok("ok"));

        MapEditorApi(app, viewModel, dispatcher, catalog, new HierarchyApi(viewModel));

        app.MapStaticAssets();
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();

        VerifyStaticAssets(app, logger);

        try
        {
            _ = dispatcher.InvokeAsync(() => StartSessionAsync(viewModel, polling, historyStore, logger, stopping));
            app.Start();
            Console.WriteLine(ListeningPrefix + (app.Urls.FirstOrDefault() ?? EditorEnvironment.UiUrl(uiPort)) + "/");
            app.WaitForShutdown();
        }
        finally
        {
            app.Lifetime.StopApplication();
        }
    }

    /// <summary>この UI を iframe に入れてよい相手を並べた CSP のディレクティブ</summary>
    /// <remarks><c>'self'</c> を必ず含む。追加の許可先には指定された宣言を使い、制御文字を含む宣言は無視する。</remarks>
    internal static string BuildFrameAncestors(string? declaration)
    {
        List<string> sources = ["'self'"];
        if (declaration is not null && !declaration.Any(char.IsControl))
        {
            foreach (string source in declaration.Split(
                [';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!sources.Contains(source, StringComparer.Ordinal)) sources.Add(source);
            }
        }

        return "frame-ancestors " + string.Join(' ', sources);
    }

    /// <summary>エディタ操作 API、OpenAPI 文書とリファレンス画面を登録する。</summary>
    internal static void MapEditorApi(
        IEndpointRouteBuilder routes,
        EditorViewModel viewModel,
        EditorDispatcher dispatcher,
        AssetCatalog catalog,
        HierarchyApi hierarchy)
    {
        MapEditorControlApi(routes, viewModel, dispatcher, catalog);
        MapHierarchyApi(routes, hierarchy, dispatcher);
        MapEditorSelectionApi(routes, viewModel, dispatcher, catalog);

        routes.MapOpenApi();
        routes.MapScalarApiReference(ApiPrefix, options => options.Title = "EmptyEngine Editor API");
    }

    /// <summary>外部操作 API の文書生成を登録する。</summary>
    /// <remarks><see cref="MapEditorApi"/> と対で使う。</remarks>
    internal static void AddEditorApi(IServiceCollection services) => services.AddOpenApi();

    /// <summary>ローカルのエージェントや開発ツールからエディタを操作するための受け口</summary>
    private static void MapEditorControlApi(
        IEndpointRouteBuilder routes,
        EditorViewModel viewModel,
        EditorDispatcher dispatcher,
        AssetCatalog catalog)
    {
        routes.MapGet("/api/editor/play-mode", async () =>
        {
            bool isPlaying = await dispatcher.InvokeAsync(() => viewModel.IsPlaying);
            return Results.Ok(new { isPlaying });
        }).DisableAntiforgery();

        routes.MapPost("/api/editor/play-mode", async ([FromQuery] bool playing) =>
        {
            bool changed = await dispatcher.InvokeAsync(() => viewModel.SetPlayMode(playing));
            return Results.Ok(new { isPlaying = playing, changed });
        }).DisableAntiforgery();

        routes.MapGet("/api/editor/scenes", async () =>
        {
            IReadOnlyList<LoadedSceneInfo> scenes =
                await dispatcher.InvokeAsync(viewModel.GetLoadedScenes);
            return Results.Ok(scenes);
        }).DisableAntiforgery();

        routes.MapPost("/api/editor/scenes", async ([FromQuery] string asset) =>
        {
            if (string.IsNullOrWhiteSpace(asset))
                return Results.BadRequest(new { error = "Query parameter 'asset' is required." });

            LoadedSceneInfo? loaded = await dispatcher.InvokeAsync(() =>
            {
                string? key = ResolveSceneAssetKey(catalog, asset);
                return key is null ? null : viewModel.TryLoadScene(key);
            });
            return loaded is null
                ? Results.NotFound(new { error = $"Scene asset '{asset}' was not found." })
                : Results.Ok(loaded);
        }).DisableAntiforgery();

        routes.MapDelete("/api/editor/scenes/{sceneId}", async ([FromRoute] string sceneId) =>
        {
            bool unloaded = await dispatcher.InvokeAsync(() => viewModel.TryUnloadScene(sceneId));
            return unloaded
                ? Results.Ok(new { sceneId, unloaded = true })
                : Results.NotFound(new { error = $"Loaded scene instance '{sceneId}' was not found." });
        }).DisableAntiforgery();
    }

    /// <summary>ヒエラルキーを読み取り専用の JSON で覗く受け口</summary>
    /// <remarks>web UI はこれを通らない（Blazor は ViewModel の木を直に束縛する）。読むのは道具だけ。</remarks>
    private static void MapHierarchyApi(
        IEndpointRouteBuilder routes,
        HierarchyApi hierarchy,
        EditorDispatcher dispatcher)
    {
        routes.MapGet("/api/editor/hierarchy", async () =>
            Results.Ok(await dispatcher.InvokeAsync(hierarchy.GetHierarchy)))
            .DisableAntiforgery();
    }

    /// <summary>シーン追加 API の指定からシーンのアセットキーの解決</summary>
    /// <remarks>キー、Assets 基準の表示パス、ソースの絶対パスを受け付ける。末尾の <c>.meta</c> は外して読む</remarks>
    internal static string? ResolveSceneAssetKey(AssetCatalog catalog, string value)
    {
        string candidate = value.Trim();
        if (catalog.IsSceneAsset(candidate)) return candidate;

        const string metaSuffix = ".meta";
        if (candidate.EndsWith(metaSuffix, StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^metaSuffix.Length];

        if (!Path.IsPathRooted(candidate))
        {
            return catalog.EnumerateAssets().FirstOrDefault(entry =>
                entry.IsScene && string.Equals(
                    entry.DisplayPath.Replace('\\', '/'),
                    candidate.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))?.Key.Value;
        }

        return catalog.TryGetKeyBySourcePath(candidate, out string? key)
            && key is not null
            && catalog.IsSceneAsset(key)
                ? key
                : null;
    }

    /// <summary>外側のファイルツリーからのアセット表示要求の受け口</summary>
    /// <remarks>ソースファイルの実パスを受け付ける。</remarks>
    private static void MapEditorSelectionApi(
        IEndpointRouteBuilder routes,
        EditorViewModel viewModel,
        EditorDispatcher dispatcher,
        AssetCatalog catalog)
    {
        routes.MapPost("/api/editor/select-asset", ([FromQuery] string path) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();

            const string metaSuffix = ".meta";
            if (path.EndsWith(metaSuffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^metaSuffix.Length];

            if (!catalog.TryGetKeyBySourcePath(path, out string? key) || key is null)
                return Results.NotFound();

            dispatcher.Post(() => viewModel.SelectAsset(new AssetKey(key)));
            return Results.Ok(key);
        }).DisableAntiforgery();
    }

    private static void VerifyStaticAssets(WebApplication app, ILogger logger)
    {
        string[] required =
        [
            "_framework/blazor.web.js",
            "_content/EmptyEngine.Editor/editor.css",
            "_content/EmptyEngine.Editor/editor.js",
        ];

        List<string> missing = [.. required.Where(path => !app.Environment.WebRootFileProvider.GetFileInfo(path).Exists)];
        if (missing.Count == 0) return;

        logger.LogWarning(
            "Editor UI assets could not be resolved ({Missing}). The page will render but nothing will be interactive.",
            string.Join(", ", missing));
    }

    private static async Task StartSessionAsync(
        EditorViewModel viewModel,
        RuntimeScenePoller polling,
        EditHistoryStore historyStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EditHistory? restored = historyStore.TryLoad();

        try
        {
            await viewModel.InitializeAsync(restored, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError("Initial scene load failed: {Error}", ex.Message);
        }

        StartPolling(polling, cancellationToken, logger);
    }

    internal static SceneClient CreateSceneClient(
        EditorViewModel viewModel,
        EditorDispatcher dispatcher,
        IHierarchyBlobSerializer serializer,
        AssetCatalog catalog,
        AssetImportService assetImportService,
        EditHistoryViewModel history,
        EditHistoryStore historyStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var client = new SceneClient(EditorEnvironment.EditorHubEndpoint);
        Channel<Func<CancellationToken, Task>> sceneSaveQueue = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _ = RunOrderedOperationsAsync(sceneSaveQueue.Reader, cancellationToken, logger);

        viewModel.SetEditSequenceSource(() => client.EditSequence);

        viewModel.ScenePublished += published =>
        {
            byte[] blob = serializer.Serialize(published.Roots);
            bool isPlaying = viewModel.IsPlaying;
            viewModel.BeginAuthoritativeSend();
            _ = CompleteAuthoritativeSendAsync(
                client.SendSceneAsync(blob, isPlaying, published.Resend, cancellationToken: cancellationToken),
                viewModel,
                logger);
        };

        history.HistoryChanged += changed =>
        {
            _ = SwallowAsync(historyStore.SaveAsync(changed, cancellationToken));
        };

        assetImportService.ArtifactsChanged += updatedKeys =>
        {
            dispatcher.Post(() => viewModel.ReloadImportedScenes(updatedKeys));
            _ = SwallowAsync(client.SendUpdateAssetsAsync(updatedKeys, cancellationToken));
        };

        viewModel.SceneSaveRequested += (sceneRoot, key) =>
        {
            if (!catalog.TryGetSourcePath(key, out string? sourcePath) || sourcePath is null) return;
            if (!assetImportService.TryGetImporter(Path.GetExtension(sourcePath), out IAssetImporter? importer)
                || importer is not ISceneImporter sceneImporter) return;

            // sceneRoot は VM から切り離された写しなので、列に積んで後から書いてよい
            sceneSaveQueue.Writer.TryWrite(async operationCancellationToken =>
            {
                await sceneImporter.SaveAsync(sceneRoot, sourcePath, operationCancellationToken);
                await assetImportService.ImportSingleAsync(sourcePath, operationCancellationToken);
                logger.LogInformation("Scene saved: {Key}", key);
            });
        };

        return client;
    }

    /// <summary>ランタイムへ状態を問い合わせ続けるループの組み立て</summary>
    internal static RuntimeScenePoller CreatePolling(
        SceneClient client,
        EditorViewModel viewModel,
        EditorDispatcher dispatcher,
        IHierarchyBlobSerializer serializer,
        ILogger<RuntimeScenePoller> logger) =>
        new(client,
            serializer,
            (roots, askedAtEdit) => dispatcher.Post(() => viewModel.UpdateHierarchy(roots, askedAtEdit)),
            DefaultPollingInterval,
            logger);

    private static async Task SwallowAsync(Task task)
    {
        try { await task; } catch { }
    }

    private static async Task CompleteAuthoritativeSendAsync(
        Task send,
        EditorViewModel viewModel,
        ILogger logger)
    {
        try
        {
            await send;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError("Scene send failed: {Error}", ex.Message);
        }
        finally
        {
            viewModel.EndAuthoritativeSend();
        }
    }

    private static async Task RunOrderedOperationsAsync(
        ChannelReader<Func<CancellationToken, Task>> reader,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        try
        {
            await foreach (Func<CancellationToken, Task> operation in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await operation(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError("Editor operation failed: {Error}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void StartPolling(
        RuntimeScenePoller polling,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            try { await polling.RunAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Editor link polling failed"); }
        }, cancellationToken);
    }
}
