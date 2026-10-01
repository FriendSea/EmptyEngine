using EmptyEngine.Core.RuntimeLink;
using EmptyEngine.PlayerLoop;
using EmptyEngine.WebGpu;
using EmptyEngine.Windowing;
using EmptyEngine.World;
using Microsoft.Extensions.DependencyInjection;

// Windowに紐づいてしまうリソースがあるため、Windowを最初に作っておく
IRuntimeWindow window = RuntimeWindow.Create(new WindowSettings(960, 540, "DotnetRuntimeSample"));

RuntimeEditorLink? editorLink = null;
ServiceProvider? worldServices = null;
SceneWorld? sceneWorld = null;
RenderWorld? renderWorld = null;
DefaultPlayerLoopRegistry? loop = null;

// ゲーム内の状態やリソースは全て何かのコンテナに詰めてしまうことをオススメします
// edit/playモードの切り替え時に、コンテナ丸ごとの再構築で状態をリセット可能です
void BuildResources()
{
    // 古いサービス全破壊
    worldServices?.Dispose();

    // 必要なサービス登録
    var services = new ServiceCollection();
    services.AddSingleton<RenderWorld>();
    services.AddSingleton(sp => new WorldAssetResolver(sp, PlatformLog.Write));
    services.AddSingleton(sp => new SceneWorld(sp.GetRequiredService<WorldAssetResolver>(), PlatformLog.Write, sp));
    services.AddSingleton(_ => new DefaultPlayerLoopRegistry(PlatformLog.Write));

    worldServices = services.BuildServiceProvider();
    sceneWorld = worldServices.GetRequiredService<SceneWorld>();
    renderWorld = worldServices.GetRequiredService<RenderWorld>();
    loop = worldServices.GetRequiredService<DefaultPlayerLoopRegistry>();

    // 再構築したタイミングで編集セッションに新しいインスタンスを提供
    editorLink?.Scene = sceneWorld;
    editorLink?.Assets = worldServices.GetRequiredService<WorldAssetResolver>();
}

if (RuntimeMode.IsEditorAttached)
{
    // エディタ実行であれば編集セッションに接続
    editorLink = new RuntimeEditorLink(PlatformLog.Write);
    // editmode/playmodeの切り替え時にサービス/リソースを再構築
    editorLink.ModeChanged += _ => BuildResources();
    editorLink.Start();
}
else
{
    // エディタ実行でなければ、最初のシーンをロード
    BuildResources();
    worldServices!.GetRequiredService<WorldAssetResolver>()
        .LoadStartupScenesAsync(sceneWorld!, PlatformLog.Write).GetAwaiter().GetResult();
}

// ウィンドウのイベントに対する処理を登録
IPresenter? presenter = null;
window.Loaded += () =>
{
    (nint handle, nint hinstance) = window.NativeHandle;
    (int width, int height) = window.FramebufferSize;
    presenter = new DesktopPresenter(handle, hinstance, width, height);
};
window.Resized += (width, height) => presenter?.Resize(width, height);
window.Render += delta =>
{
    if (presenter is not null && renderWorld is not null)
        presenter.RenderFrame(delta, renderWorld);
};
window.Closed += () =>
{
    editorLink?.Dispose();
    worldServices?.Dispose();
    presenter?.Dispose();
    window.Dispose();
};

// windowの描画更新に合わせてUpdate
const double DeltaTime = 1.0 / 60.0;
const int MaxStepsPerFrame = 5;
double accumulator = 0.0;
window.Update += delta =>
{
    // 定期的にエディタ同期を呼び出し
    // この実装でのISceneSerializerはスレッドセーフを保証しないため、ゲームロジックと同一のスレッドで呼び出し
    editorLink?.Pump();
    // この例では固定60FPSとして辻褄が合うようにUpdateする
    accumulator = Math.Min(accumulator + delta, DeltaTime * MaxStepsPerFrame);
    while (accumulator >= DeltaTime)
    {
        accumulator -= DeltaTime;

        loop?.Tick(DeltaTime);
        sceneWorld?.FlushPendingDestructions();
    }
};

// 全部の登録が終わったら開始
// このRunメソッドではプロセス終了まで処理がブロックされる
window.Run();
