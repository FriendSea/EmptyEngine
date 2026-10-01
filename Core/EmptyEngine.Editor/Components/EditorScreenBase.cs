using EmptyEngine.Editor.Hosting;
using EmptyEngine.Editor.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace EmptyEngine.Editor.Components;

/// <summary>エディタの画面に共通の土台</summary>
public abstract class EditorScreenBase : ObservingComponentBase, IAsyncDisposable
{
    [Inject] protected EditorViewModel Editor { get; set; } = null!;

    [Inject] private EditHistoryViewModel History { get; set; } = null!;

    [Inject] private SourceAssetRefresher AssetRefresher { get; set; } = null!;

    [Inject] protected IJSRuntime JS { get; set; } = null!;

    /// <summary>開いたときにフォーカスを当てる画面のルート要素</summary>
    protected ElementReference Root;

    /// <summary>読み込んだ <c>editor.js</c></summary>
    protected IJSObjectReference? Module;

    /// <summary>画面を閉じるときに解除するドラッグ操作</summary>
    protected IJSObjectReference? ScreenDrag;

    private IDisposable? _self;

    /// <summary>画面の JS 側への接続</summary>
    protected async Task AttachScreenAsync<TScreen>(TScreen screen) where TScreen : EditorScreenBase
    {
        DotNetObjectReference<TScreen> self = DotNetObjectReference.Create(screen);
        _self = self;

        Module = await JS.InvokeAsync<IJSObjectReference>("import", "/_content/EmptyEngine.Editor/editor.js");

        await Module.InvokeVoidAsync("useExternalDropTypes", (object)FileDrop.ExternalTypes);

        await OnScreenAttachedAsync(Module, self);

        await Module.InvokeVoidAsync("watchVisibility", self);
        await Module.InvokeVoidAsync("watchShortcuts", self);
        await Root.FocusAsync();
    }

    /// <summary>画面固有の JS 初期化（既定は無し）</summary>
    protected virtual Task OnScreenAttachedAsync(IJSObjectReference module, object self) => Task.CompletedTask;

    /// <summary>可視復帰時のソースアセット取り込み</summary>
    protected Task RefreshOnBecameVisibleAsync() =>
        Dispatcher.InvokeAsync(() => AssetRefresher.RefreshIfChangedAsync());

    /// <summary>ショートカットキーの実行</summary>
    /// <param name="name"><c>editor.js</c> の <c>watchShortcuts</c> が送る名前</param>
    protected Task RunShortcutAsync(string name) =>
        Dispatcher.InvokeAsync(() =>
        {
            switch (name)
            {
                case "undo":
                    History.Undo();
                    break;
                case "redo":
                    History.Redo();
                    break;
                case "save":
                    Editor.SaveScene();
                    break;
                case "play":
                    Editor.SetPlayMode(!Editor.IsPlaying);
                    break;
                case "delete":
                    Editor.DeleteSelectedObject();
                    break;
            }
        });

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            if (ScreenDrag is not null)
            {
                await ScreenDrag.InvokeVoidAsync("dispose");
                await ScreenDrag.DisposeAsync();
            }
            if (Module is not null) await Module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        finally
        {
            _self?.Dispose();
        }
    }
}
