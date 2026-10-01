using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Timing;
using EmptyEngine.Editor.ViewModels;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>ソースアセットの変更の取り込みとツリーの組み直し</summary>
/// <remarks>ソースの監視・プレイの停止・ピッカーを閉じたときに自分から見に行く</remarks>
internal sealed class SourceAssetRefresher : IDisposable
{
    private readonly EditorViewModel _editor;
    private readonly EditorDispatcher _dispatcher;
    private readonly AssetImportService _imports;
    private readonly ILogger _logger;

    private readonly Cooldown _check = new(TimeSpan.FromSeconds(1));

    private SourceAssetWatcher? _watcher;
    private bool _checkInProgress;
    private bool _recheckRequested;

    public SourceAssetRefresher(
        EditorViewModel editor,
        EditorDispatcher dispatcher,
        AssetImportService imports,
        ILogger<SourceAssetRefresher> logger)
    {
        _editor = editor;
        _dispatcher = dispatcher;
        _imports = imports;
        _logger = logger;

        // ピッカーを開いている間の要求は捨てられる。閉じたところで見に行き直す
        _editor.PropertyChanged += (_, e) =>
        {
            bool resumed = e.PropertyName switch
            {
                nameof(EditorViewModel.IsPlaying) => !_editor.IsPlaying,
                nameof(EditorViewModel.IsPickerOpen) => !_editor.IsPickerOpen,
                _ => false,
            };

            if (resumed) _dispatcher.Post(() => _ = RefreshIfChangedAsync(immediate: true));
        };
    }

    /// <summary>ソースアセットの監視の開始</summary>
    public void StartWatching()
    {
        _watcher ??= SourceAssetWatcher.TryStart(
            _imports.AssetsRootPath,
            () => _dispatcher.Post(() => _ = RefreshIfChangedAsync(immediate: true)),
            _logger);
    }

    /// <summary>ソースアセットの変更の取り込みとツリーの組み直し</summary>
    /// <remarks><paramref name="immediate"/> は時間による間引きを飛ばす</remarks>
    public async Task RefreshIfChangedAsync(bool immediate = false)
    {
        if (_checkInProgress)
        {
            _recheckRequested = true;
            return;
        }

        if (!_check.TryEnter() && !immediate) return;
        _check.Mark();

        if (_editor.IsPickerOpen) return;

        _checkInProgress = true;
        try
        {
            await _editor.ImportAssetsIfChangedAsync();
            _editor.RefreshAssetViews();
        }
        catch (Exception ex)
        {
            _logger.LogError("Asset refresh failed: {Error}", ex.Message);
        }
        finally
        {
            _checkInProgress = false;
        }

        if (!_recheckRequested) return;

        _recheckRequested = false;
        await RefreshIfChangedAsync(immediate: true);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
