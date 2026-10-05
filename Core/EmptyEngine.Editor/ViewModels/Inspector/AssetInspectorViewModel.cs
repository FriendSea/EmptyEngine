using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>インスペクタが映しているアセット 1 つ分</summary>
/// <remarks>アセットを表示している間は、ヒエラルキーで選択したオブジェクトのインスペクタを表示しない。
/// <see cref="IAssetImporter.IsSaveSupported"/> が <c>false</c> のアセットと、パッケージ同梱のアセットは読み取り専用になる。
/// 編集はソースファイルへ保存され、シーンの undo 履歴には含まれない。</remarks>
public sealed class AssetInspectorViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AssetCatalog _assets;
    private readonly AssetImportService _imports;
    private readonly ProjectAssetLayout _layout;
    private readonly ILogger _logger;

    private string? _key;
    private bool _inspecting;
    private bool _isEditable;
    private bool _rebinding;
    private bool _isPlaying;
    private AuthoringObjectViewModel? _inspected;

    /// <summary>最後の書き換えからソースへ書き戻すまでの待ち</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private IAssetImporter? _editableImporter;
    private string? _editableSourcePath;
    private readonly AssetSaveQueue _save;

    /// <summary>新しく作られてロードすべきシーンアセット（バリアント作成）</summary>
    public event Action<string>? SceneCreated;

    /// <summary>取り込みが走ってカタログが変わったことの通知</summary>
    public event Action? CatalogChanged;

    /// <param name="assets">取り込み済みアセットのカタログ</param>
    /// <param name="imports">ソースアセットの取り込み</param>
    /// <param name="layout">アセットルートとパッケージの配置</param>
    public AssetInspectorViewModel(
        AssetCatalog assets, AssetImportService imports, ProjectAssetLayout layout, ILogger logger)
    {
        _assets = assets;
        _imports = imports;
        _layout = layout;
        _logger = logger;
        _save = new AssetSaveQueue(logger);
    }

    /// <summary>待っている書き戻しを終えてから閉じる</summary>
    public ValueTask DisposeAsync() => _save.DisposeAsync();

    /// <summary>プレイ中か。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged(nameof(CanReimport));
        }
    }

    /// <summary>映す対象の差し替え（<c>null</c> で解除）</summary>
    public void Select(AssetKey? reference)
    {
        // 対象変更で前のアセットの保存予約が失われないよう、先に書き込む。
        _save.Flush();
        _key = reference?.Value;
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(CanCreateVariant));
        OnPropertyChanged(nameof(CanReimport));
        Rebind();
    }

    /// <summary>カタログが差し替わったあとの映し直し</summary>
    /// <remarks>映しているかどうか（<see cref="IsInspecting"/>）は動かさない</remarks>
    public void Refresh()
    {
        _rebinding = true;
        try
        {
            Rebind();
        }
        finally
        {
            _rebinding = false;
        }
    }

    /// <summary>ヒエラルキー側が選ばれたときのアセットビューの引っ込め</summary>
    public void StopInspecting()
    {
        _save.Flush();
        SetInspecting(false);
    }

    /// <summary>映しているアセットのカタログキー</summary>
    public string? Key => _key;

    /// <summary>映しているアセット 1 つ分の箱（映していなければ <c>null</c>）</summary>
    public AuthoringObjectViewModel? Inspected
    {
        get => _inspected;
        private set => SetProperty(ref _inspected, value);
    }

    /// <summary>映しているアセットが編集可能か</summary>
    public bool IsEditable
    {
        get => _isEditable;
        private set => SetProperty(ref _isEditable, value);
    }

    /// <summary>インスペクタがアセットビューを映しているか</summary>
    public bool IsInspecting => _inspecting && Inspected is not null;

    /// <summary>表示中の VM から取得する独立したスナップショット</summary>
    private AuthoringObject? Value => Inspected?.Capture();

    /// <summary>アセットの表示パスを示す見出し</summary>
    public string Title => _key is null ? string.Empty : ResolveDisplay(_key) ?? _key;

    /// <summary>見出し下の注記（編集可否でメッセージを切り替える）</summary>
    public string Hint =>
        _isEditable ? "Editable — changes save to source" : "Read-only asset values";

    /// <summary>Save ボタン用の手動フラッシュ</summary>
    public void SaveNow()
    {
        if (!TryDescribeEditable(out IAssetImporter importer, out string path, out AuthoringObject asset, "manual save"))
            return;

        _save.Schedule(importer, path, asset, TimeSpan.Zero);
    }

    /// <summary>映しているアセットを再取り込みできるか</summary>
    public bool CanReimport => !_isPlaying && _key is { Length: > 0 };

    /// <summary>映しているアセットの単体再インポート</summary>
    public async Task ReimportAsync(CancellationToken cancellationToken = default)
    {
        if (_key is not { Length: > 0 } key) return;
        if (_isPlaying)
        {
            _logger.LogWarning("Reimport is not available while playing. Stop play mode first.");
            return;
        }

        if (!_assets.TryGetSourcePath(key, out string? sourcePath) || sourcePath is null) return;

        // 未保存の値が再取り込みで失われないよう、書き戻しを完了させる。
        await _save.FlushAsync();
        await _imports.ImportSingleAsync(sourcePath, cancellationToken);
        Refresh();
        CatalogChanged?.Invoke();
    }

    /// <summary>映しているアセットからバリアントを作れるか</summary>
    public bool CanCreateVariant =>
        _imports.HasImporter<IVariantImporter>(".variant")
        && _key is { Length: > 0 } key
        && IsSceneSource(key);

    /// <summary>オリジナル guid だけを参照する空バリアントの作成とロード</summary>
    public async Task CreateVariantAsync(CancellationToken cancellationToken = default)
    {
        if (!_imports.TryGetImporter(".variant", out IVariantImporter variantImporter))
        {
            _logger.LogWarning("Cannot create variant: no .variant importer registered.");
            return;
        }

        if (_key is not { Length: > 0 } assetKey
            || !IsSceneSource(assetKey)
            || !_assets.TryGetSourcePath(assetKey, out string? originalPath)
            || originalPath is null)
        {
            _logger.LogWarning("Cannot create variant: inspect a .scene asset first.");
            return;
        }

        string targetDir = _layout.IsReadOnlySource(originalPath)
            ? _layout.AssetsRootPath
            : Path.GetDirectoryName(originalPath) ?? _layout.AssetsRootPath;
        string path = UniquePath(targetDir, Path.GetFileNameWithoutExtension(originalPath) + "Variant", ".variant");

        try
        {
            await variantImporter.CreateAsync(assetKey, path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create variant '{Variant}': {Error}", Path.GetFileName(path), ex.Message);
            return;
        }

        AssetImportResult result = await _imports.ImportSingleAsync(path, cancellationToken);
        Refresh();
        CatalogChanged?.Invoke();

        if (result.Success && result.Asset is { Key: { Length: > 0 } key })
        {
            _logger.LogInformation("Created variant: {Variant}", Path.GetFileName(path));
            SceneCreated?.Invoke(key);
        }
    }

    /// <summary>重ならないファイル名の作成</summary>
    internal static string UniquePath(string dir, string baseName, string extension)
    {
        string path = Path.Combine(dir, baseName + extension);
        int n = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{baseName}{n++}{extension}");
        return path;
    }

    private bool IsSceneSource(string assetKey) =>
        _assets.IsSceneAsset(assetKey)
        && _assets.IsSourceExtension(assetKey, ".scene");

    private string? ResolveDisplay(string key) => _assets.ResolveDisplayPath(key);

    private void Rebind()
    {
        AuthoringObject? value = _key is { Length: > 0 } key ? _assets.GetAsset(key) : null;

        BuildView(value);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));

        if (_rebinding) return;

        SetInspecting(value is not null);
    }

    private void SetInspecting(bool value)
    {
        _inspecting = value;
        OnPropertyChanged(nameof(IsInspecting));
    }

    private void BuildView(AuthoringObject? value)
    {
        _editableImporter = null;
        _editableSourcePath = null;

        if (value is null)
        {
            IsEditable = false;
            Inspected = null;
            return;
        }

        if (_key is { Length: > 0 } assetKey
            && _assets.TryGetSourcePath(assetKey, out string? sourcePath)
            && sourcePath is not null
            && _imports.TryGetImporter(
                AssetImportService.NormalizeExtension(Path.GetExtension(sourcePath)),
                out IAssetImporter? importer)
            && importer is { IsSaveSupported: true }
            && !_layout.IsReadOnlySource(sourcePath))
        {
            _editableImporter = importer;
            _editableSourcePath = sourcePath;
        }

        IsEditable = _editableImporter is not null;

        var box = new AuthoringObjectViewModel(
            value.TypeName, value.Schema.DisplayName, value, _ => SaveDebounced(), ResolveDisplay);

        if (!IsEditable)
        {
            box.Root.MarkReadOnly();
        }

        Inspected = box;
    }

    /// <summary>クリップボードの値をこのアセットへ貼り付けられるか</summary>
    /// <remarks>アセットとクリップボードの値が同じ型の場合に貼り付けられる。</remarks>
    public bool CanPaste(AuthoringObject? clipboard) =>
        IsEditable
        && clipboard is not null
        && Inspected is { } target
        && string.Equals(clipboard.TypeName, target.TypeName, StringComparison.Ordinal);

    /// <summary>クリップボードの値でこのアセットの中身を置き換え、ソースへ書き戻す</summary>
    public void Paste(AuthoringObject clipboard)
    {
        if (!CanPaste(clipboard)) return;

        Inspected!.Apply(clipboard);
        SaveNow();
    }

    private void SaveDebounced()
    {
        if (!TryDescribeEditable(out IAssetImporter importer, out string path, out AuthoringObject asset, "auto-save"))
            return;

        _save.Schedule(importer, path, asset, SaveDelay);
    }

    private bool TryDescribeEditable(
        out IAssetImporter importer, out string path, out AuthoringObject asset, string context)
    {
        if (_editableImporter is { } i && _editableSourcePath is { } p && Value is { } a)
        {
            (importer, path, asset) = (i, p, a);
            return true;
        }

        _logger.LogWarning(
            "Asset {Context} skipped: no editable target bound (importer={Importer}, path={Path}, value={Value}, selected={Selected})",
            context,
            _editableImporter is null ? "null" : "ok",
            _editableSourcePath ?? "null",
            Value is null ? "null" : "ok",
            _key ?? "(none)");
        (importer, path, asset) = (null!, null!, null!);
        return false;
    }
}
