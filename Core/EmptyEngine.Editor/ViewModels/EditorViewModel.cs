using EmptyEngine.Core;
using EmptyEngine.Editor.Assets;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Editor.Catalog;
using EmptyEngine.Editor.State;
using EmptyEngine.Editor.Timing;
using EmptyEngine.Editor.ViewModels.Build;
using EmptyEngine.Editor.ViewModels.Hierarchy;
using EmptyEngine.Editor.ViewModels.Inspector;
using EmptyEngine.Editor.ViewModels.Utils;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.ViewModels;

/// <summary>アセット選択ダイアログの 1 候補</summary>
public sealed record AssetCandidate(string DisplayName, AssetKey Key);

/// <summary>現在のヒエラルキーにロードされているシーンインスタンス</summary>
public sealed record LoadedSceneInfo(string SceneId, string Name, string AssetKey, string DisplayPath);

/// <summary>ランタイムへ渡す権威ツリー 1 本ぶん</summary>
/// <param name="Roots">送るツリー</param>
/// <param name="Resend">直前に送ったものと同じ内容でも送り直すか（ロードは中身が同じでも積み直させる）</param>
internal readonly record struct ScenePublication(IReadOnlyList<HierarchyNode> Roots, bool Resend);

public sealed class EditorViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly SnapshotCollection<AuthoringObjectViewModel> EmptyComponents = new();

    /// <summary>フィールド編集をランタイムへ流す最小間隔</summary>
    private static readonly TimeSpan EditStreamInterval = TimeSpan.FromMilliseconds(33);

    private HierarchyNodeViewModel? _selectedNode;
    private readonly Dictionary<string, string> _loadedSceneKeys = new(StringComparer.Ordinal);
    private readonly AssetCatalog _assets;
    private readonly AssetImportService _imports;
    private readonly IHierarchyBlobSerializer _cloneSerializer;
    private readonly TypeCatalog _typeCatalog;
    private readonly EditorStateStore _stateStore;
    private readonly ILogger _logger;
    private readonly Cooldown _editStreamWindow = new(EditStreamInterval);
    private readonly Debouncer _editStream = new();
    private int _pendingAuthoritativeSends;
    private bool _applyingRuntimeSnapshot;
    private Func<int>? _editSequence;
    private int _pickerSuspendDepth;
    private bool _isPlaying;
    private bool _initialLoadPending;
    private List<HierarchyNode>? _editSnapshot;
    private readonly HashSet<string> _dirtySceneIds = new(StringComparer.Ordinal);
    private HashSet<string> _snapshotDirtyIds = new(StringComparer.Ordinal);
    private readonly HashSet<AuthoringObject> _reattachedComponents = new(ReferenceEqualityComparer.Instance);
    private string? _objectDrift;

    private HierarchyNode? _clipboardNode;
    private AuthoringObject? _clipboardComponent;

    private readonly EditHistoryViewModel _history;

    /// <summary>権威ツリーをランタイムへ渡すことの通知</summary>
    /// <remarks>非同期に送信する購読者は、イベント処理を返す前に <see cref="BeginAuthoritativeSend"/> を呼び、送信終了時に <see cref="EndAuthoritativeSend"/> を呼ぶこと。</remarks>
    internal event Action<ScenePublication>? ScenePublished;

    /// <summary>シーンをソースファイルへ書き戻すことの要求</summary>
    /// <remarks>渡すツリーは VM から切り離した独立した写しなので、購読者は保持して後から書いてよい。</remarks>
    internal event Action<HierarchyNode, string>? SceneSaveRequested;

    /// <param name="assets">取り込み済みアセットのカタログ</param>
    /// <param name="imports">ソースアセットの取り込み</param>
    /// <param name="serializer">undo と権威ツリー送信で使う blob 変換</param>
    /// <param name="state">セッションを跨いで保つ UI 状態（ペイン幅・展開）</param>
    /// <param name="history">undo/redo の段とカーソル（段を積むのも戻すのもこの VM から）</param>
    /// <param name="catalog">オーサリング対象の型の供給元</param>
    public EditorViewModel(
        AssetCatalog assets,
        AssetImportService imports,
        IHierarchyBlobSerializer serializer,
        EditorStateStore state,
        EditHistoryViewModel history,
        TypeCatalog catalog,
        ILogger<EditorViewModel> logger)
    {
        _assets = assets;
        _imports = imports;
        _cloneSerializer = serializer;
        _stateStore = state;
        _typeCatalog = catalog;
        _logger = logger;

        RootNodes = new SnapshotCollection<HierarchyNodeViewModel>();
        BuildScenes = new BuildSceneListViewModel(new BuildSceneSource(this));
        _history = history;
        _history.Restoring += RestoreWorld;

        Asset = new AssetInspectorViewModel(assets, imports, logger);
        Asset.CatalogChanged += BuildScenes.RefreshDisplayNames;
        Asset.SceneCreated += LoadSceneByKey;
        Asset.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AssetInspectorViewModel.IsInspecting)
                or nameof(AssetInspectorViewModel.Inspected))
            {
                OnPropertyChanged(nameof(CanPasteComponent));
            }
        };

        RefreshAssetViews();
        BuildScenes.Reload();
    }

    /// <summary>インスペクタが映しているアセット（ヒエラルキーの選択とは別系統）</summary>
    public AssetInspectorViewModel Asset { get; }

    /// <summary>待っているアセットの書き戻しを終えてから閉じる</summary>
    public ValueTask DisposeAsync() => Asset.DisposeAsync();

    /// <summary>Build Settings リストが取り込み結果を引く受け口</summary>
    private sealed class BuildSceneSource(EditorViewModel owner) : IBuildSceneSource
    {
        public string? AssetsRootPath => owner._imports.AssetsRootPath;

        public bool IsSceneAsset(string key) => owner._assets.IsSceneAsset(key);

        public string? ResolveDisplayName(string key) => owner.ResolveAssetDisplay(key);
    }

    /// <summary>編集中のツリーの子</summary>
    private static readonly Func<HierarchyNodeViewModel, IReadOnlyList<HierarchyNodeViewModel>> VmChildren =
        vm => vm.Children;

    private static bool HasId(string id, string other) => string.Equals(id, other, StringComparison.Ordinal);

    /// <summary>プレイモード（<c>true</c>）かエディットモード（<c>false</c>）か</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>プレイモードとエディットモードの切り替え</summary>
    /// <remarks>プレイへ入るときは戻ってくる先を控え、戻るときはそこへ巻き戻す</remarks>
    /// <returns>モードが変わったか</returns>
    public bool SetPlayMode(bool playing)
    {
        if (_isPlaying == playing) return false;

        _history.CloseCoalescing();

        if (playing)
        {
            _editSnapshot = CaptureHierarchy();
            _snapshotDirtyIds = new HashSet<string>(_dirtySceneIds, StringComparer.Ordinal);
        }

        SetProperty(ref _isPlaying, playing, nameof(IsPlaying));
        _history.Enabled = !playing;
        Asset.IsPlaying = playing;
        OnPropertyChanged(nameof(CanSaveScene));
        OnPropertyChanged(nameof(CanExtractPrefab));

        if (playing)
        {
            UpdateDirtyMarkers();
            PublishStructuralEdit();
        }
        else
        {
            RestoreSnapshot();
        }

        return true;
    }

    public SnapshotCollection<HierarchyNodeViewModel> RootNodes { get; }

    /// <summary>デプロイ対象シーンの順序付きリスト（Build Settings）</summary>
    public BuildSceneListViewModel BuildScenes { get; }

    public HierarchyNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _history.CloseCoalescing();
            SetProperty(ref _selectedNode, value);
            OnPropertyChanged(nameof(SelectedComponents));
            OnPropertyChanged(nameof(SelectedNodeName));
            OnPropertyChanged(nameof(CanModifySelectedObject));
            OnPropertyChanged(nameof(CanSaveScene));
            OnPropertyChanged(nameof(CanUnloadScene));
            OnPropertyChanged(nameof(CanAddPrefabChild));
            OnPropertyChanged(nameof(CanExtractPrefab));
            if (value is not null) Asset.StopInspecting();
        }
    }

    /// <summary>選択中ノードの名前</summary>
    public string? SelectedNodeName
    {
        get => _selectedNode?.Name;
        set
        {
            if (_selectedNode is null || value is null || _selectedNode.Name == value) return;
            _selectedNode.Name = value;
            MarkSceneDirty(_selectedNode);
            PublishEdits("~name");
        }
    }

    /// <summary>シーンルート以外が選択されているか（削除・移動・リネームの可否）</summary>
    public bool CanModifySelectedObject =>
        _selectedNode is not null && !IsSceneRoot(_selectedNode);

    /// <summary>ヒエラルキー上のオブジェクトとその子孫の有効状態を変更する</summary>
    /// <remarks>プレイ中も変更できる。プレイ中の変更は未保存の編集として扱わず、停止時にプレイ開始前の状態へ戻す。</remarks>
    public void SetActive(HierarchyNodeViewModel viewModel, bool active)
    {
        if (OwningRoot(viewModel) is null || viewModel.Active == active) return;
        viewModel.Active = active;
        MarkSceneDirty(viewModel);
        PublishStructuralEdit();
    }

    public SnapshotCollection<AuthoringObjectViewModel> SelectedComponents
        => SelectedNode?.Components ?? EmptyComponents;

    /// <summary>選択中オブジェクトと同じシーンにある参照先をヒエラルキーで選択する</summary>
    public bool SelectReferencedObject(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId) || OwningRoot(_selectedNode) is not { } owning)
            return false;

        if (Tree.FindPath(owning, VmChildren, vm => HasId(vm.ObjectId, targetId!)) is not { } path)
            return false;

        foreach (HierarchyNodeViewModel ancestor in path.Take(path.Count - 1))
            SetHierarchyExpanded(ancestor, true);

        SelectedNode = path[^1];
        return true;
    }

    /// <summary>インスペクタの表示対象アセットの差し替え</summary>
    public void SelectAsset(AssetKey? reference) => Asset.Select(reference);

    /// <summary>アセット参照(GUID キー)を人間可読名へ解決する公開窓口（インスペクタが使う）</summary>
    public string? ResolveAssetDisplayName(string assetKey) => ResolveAssetDisplay(assetKey);

    /// <summary>選択ノードが属するシーンが保存先キーを持つか</summary>
    public bool CanSaveScene => !_isPlaying && OwningSceneKey(_selectedNode) is not null;

    /// <summary>ポーリング取り込みの鮮度判定に使う編集本数のソースの差し込み</summary>
    internal void SetEditSequenceSource(Func<int>? source) => _editSequence = source;

    /// <summary>Hierarchy ノードの展開状態を変更し、シーン単位で保存する</summary>
    public void SetHierarchyExpanded(HierarchyNodeViewModel node, bool expanded)
    {
        node.IsExpanded = expanded;
        if (OwningRoot(node) is not { } owning) return;
        if (!_loadedSceneKeys.TryGetValue(owning.SceneId, out string? sceneKey)) return;

        _stateStore.SetExpanded(sceneKey, node.ObjectId, expanded);
    }

    /// <summary>起動時のシーン状態を復元する</summary>
    /// <param name="history">復元する undo 履歴。指定された場合は通常の起動シーンより優先する。</param>
    public async Task InitializeAsync(
        EditHistory? history = null, CancellationToken cancellationToken = default)
    {
        _initialLoadPending = true;
        try
        {
            await ImportAssetsIfChangedAsync(cancellationToken);
            RefreshAssetViews();
            if (!TryRestoreHistory(history)) LoadStartupScene();
        }
        finally
        {
            _initialLoadPending = false;
        }
    }

    /// <summary>前回セッションの undo 履歴の復元</summary>
    private bool TryRestoreHistory(EditHistory? history)
    {
        if (history is null) return false;
        if (history.Cursor < 0 || history.Cursor >= history.Steps.Count) return false;

        SceneSnapshot current = history.Steps[history.Cursor].Snapshot;

        List<HierarchyNode> roots;
        try
        {
            roots = _cloneSerializer.Deserialize(current.Blob).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Session restore failed (falling back to normal startup): {Error}", ex.Message);
            return false;
        }

        AdoptSnapshotState(current);
        SyncRootNodeViews(roots);

        // 履歴は丸ごと引き継ぐので、ここで基準点を作り直さない。
        FinishLoad(LoadHistory.Keep);
        _history.Adopt(history);

        _logger.LogInformation(
            "Restored {Scenes} scene(s) and {Steps} history step(s) from the previous session (unsaved edits included).",
            roots.Count, history.Steps.Count);
        return true;
    }

    /// <summary>起動シーンの新しいインスタンスとしての読み込み</summary>
    private void LoadStartupScene()
    {
        string? startupKey = BuildScenes.Keys.FirstOrDefault(_assets.IsSceneAsset)
            ?? _assets.EnumerateAssets()
                .Where(e => e.IsScene)
                .Select(e => e.Key.Value)
                .FirstOrDefault();
        if (startupKey is null) return;
        if (_assets.GetSceneRoot(startupKey) is not { } template) return;

        var instance = InstantiateTemplate(template);
        _loadedSceneKeys[instance.SceneId] = startupKey;

        SyncRootNodeViews([instance]);
        FinishLoad(LoadHistory.Reset);
    }

    /// <summary>ロードのあと履歴をどう動かすか</summary>
    private enum LoadHistory
    {
        /// <summary>これを基準点にして履歴を捨てる（起動時の読み込み）</summary>
        Reset,

        /// <summary>1 段積む（そのロード操作自体を取り消せるようにする）</summary>
        Commit,

        /// <summary>触らない（履歴の側が別に整える）</summary>
        Keep,
    }

    /// <summary>ロードしたシーンを表示へ反映し、ランタイムへのロードを要求する</summary>
    /// <remarks>以前と同じ内容でもロード要求を通知する。履歴の扱いは <paramref name="history"/> に従う。</remarks>
    private void FinishLoad(LoadHistory history)
    {
        CancelPendingEdits();
        UpdateDirtyMarkers();

        ScenePublished?.Invoke(new ScenePublication(CaptureHierarchy(), Resend: true));
        OnPropertyChanged(nameof(CanSaveScene));

        switch (history)
        {
            case LoadHistory.Reset:
                _history.Reset(CaptureWorld());
                break;
            case LoadHistory.Commit:
                CommitEdit(null);
                break;
        }
    }

    /// <summary>スナップショットが持つツリー以外の状態（シーンキー・dirty 集合）の取り込み</summary>
    private void AdoptSnapshotState(SceneSnapshot snapshot)
    {
        _loadedSceneKeys.Clear();
        foreach (KeyValuePair<string, string> pair in snapshot.LoadedSceneKeys)
            _loadedSceneKeys[pair.Key] = pair.Value;

        _dirtySceneIds.Clear();
        foreach (string id in snapshot.DirtySceneIds) _dirtySceneIds.Add(id);
    }

    public void UpdateHierarchy(IReadOnlyList<HierarchyNode> roots)
        => UpdateHierarchy(roots, int.MaxValue);

    /// <param name="askedAtEdit">この poll を要求した時点の編集本数。今の値より小さければ古い応答として捨てる。</param>
    public void UpdateHierarchy(IReadOnlyList<HierarchyNode> roots, int askedAtEdit)
    {
        if (_initialLoadPending) return;

        if (Volatile.Read(ref _pickerSuspendDepth) > 0) return;

        if (Volatile.Read(ref _pendingAuthoritativeSends) > 0) return;

        if (_editSequence is { } sequence && askedAtEdit < sequence()) return;

        List<HierarchyNode>? held = !_isPlaying ? CaptureHierarchy() : null;
        if (held is { Count: > 0 }
            && HierarchyReconciliation.DescribeObjectMismatch(held, roots) is { } drift)
        {
            ReportObjectDrift(drift);
            return;
        }

        _objectDrift = null;
        _reattachedComponents.Clear();
        if (held is not null) HierarchyReconciliation.RestoreMissingComponents(held, roots, _reattachedComponents);

        var presentIds = new HashSet<string>(roots.Select(r => r.SceneId), StringComparer.Ordinal);
        _dirtySceneIds.RemoveWhere(id => !presentIds.Contains(id));

        _applyingRuntimeSnapshot = true;
        try { SyncRootNodeViews(roots); }
        finally { _applyingRuntimeSnapshot = false; }
        UpdateDirtyMarkers();

        if (!_isPlaying && !_history.HasBaseline) _history.Reset(CaptureWorld());
    }

    /// <summary>同じ食い違いが続く間は 1 回だけ知らせる（poll ごとに出さない）</summary>
    private void ReportObjectDrift(string drift)
    {
        if (string.Equals(_objectDrift, drift, StringComparison.Ordinal)) return;

        _objectDrift = drift;
        _logger.LogError(
            "The runtime answered with a different set of objects than the editor holds, "
            + "so the answer was dropped (edit mode is editor-authoritative): {Drift}",
            drift);
    }

    /// <summary>与えたルート列での表示ツリーの差分更新と選択の復元</summary>
    private void SyncRootNodeViews(IReadOnlyList<HierarchyNode> roots)
    {
        string? selectedId = SelectedNode?.ObjectId;
        string? selectedRootId = OwningRoot(SelectedNode)?.SceneId;

        Reconcile.Into(
            RootNodes,
            roots,
            vm => vm.SceneId,
            root => root.SceneId,
            (vm, root) =>
            {
                vm.IsSceneRoot = true;
                UpdateNode(vm, root);
                RestoreHierarchyExpansion(vm);
            },
            root =>
            {
                HierarchyNodeViewModel added = ToViewModel(root);
                added.IsSceneRoot = true;
                RestoreHierarchyExpansion(added);
                return added;
            });

        RestoreSelection(selectedId, selectedRootId);
    }

    private void RestoreSelection(string? selectedId, string? selectedRootId)
    {
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            HierarchyNodeViewModel? rootVm = selectedRootId is not null
                ? RootNodes.FirstOrDefault(r => string.Equals(r.SceneId, selectedRootId, StringComparison.Ordinal))
                : null;
            SelectedNode = (rootVm is not null ? FindById(rootVm, selectedId) : null) ?? RootNodes.FirstOrDefault();
        }
        else
        {
            SelectedNode ??= RootNodes.FirstOrDefault();
        }
    }

    private void RestoreHierarchyExpansion(HierarchyNodeViewModel root)
    {
        if (!_loadedSceneKeys.TryGetValue(root.SceneId, out string? sceneKey)) return;

        ApplyHierarchyExpansion(root, _stateStore.ExpandedObjects(sceneKey));
    }

    private static void ApplyHierarchyExpansion(
        HierarchyNodeViewModel node,
        IReadOnlySet<string> expanded)
    {
        node.IsExpanded = expanded.Contains(node.ObjectId);
        foreach (HierarchyNodeViewModel child in node.Children)
            ApplyHierarchyExpansion(child, expanded);
    }

    public async Task ImportAssetsIfChangedAsync(CancellationToken cancellationToken = default)
    {
        if (_isPlaying) return;

        IReadOnlyList<AssetImportResult> results = await _imports.ImportIfChangedAsync(cancellationToken);
        if (results.Count == 0) return;

        int success = results.Count(r => r.Success);
        int failed = results.Count - success;
        _logger.LogInformation("Asset auto import completed. success={Success}, failed={Failed}", success, failed);
        RefreshAssetViews();
    }

    /// <summary>カタログ差し替え後のアセット由来の表示の貼り直し</summary>
    public void RefreshAssetViews()
    {
        Asset.Refresh();
        BuildScenes.RefreshDisplayNames();
    }

    /// <summary>ピッカーで選ばれたシーンを新しいインスタンスとして読み込む（同じシーンを複数同時に可）</summary>
    public void LoadScene(AssetKey reference)
    {
        if (!_assets.IsSceneAsset(reference.Value)) return;
        LoadSceneByKey(reference.Value);
    }

    /// <summary>アセットキーでシーンをロードし、作成されたシーンインスタンスを返す</summary>
    public LoadedSceneInfo? TryLoadScene(string assetKey)
    {
        if (!_assets.IsSceneAsset(assetKey)) return null;

        HashSet<string> before = RootNodes.Select(root => root.SceneId).ToHashSet(StringComparer.Ordinal);
        LoadSceneByKey(assetKey);

        HierarchyNodeViewModel? loaded = RootNodes.LastOrDefault(root => !before.Contains(root.SceneId));
        return loaded is null ? null : DescribeLoadedScene(loaded);
    }

    /// <summary>外のファイルツリーからヒエラルキーへ落とされたファイルの受け取り</summary>
    /// <param name="dropped">落とされた指し先。<c>file://</c> の URI・実パス・ファイル名のいずれか。</param>
    /// <param name="target">落とし先のオブジェクト。<c>null</c>（ツリーの空き）ならルートへ加算ロード、オブジェクトの上ならその子としてネストプレハブ配置。</param>
    /// <remarks>シーン以外のアセットと未取り込みのファイルは何もしない（理由はログへ出る）</remarks>
    public async Task DropFilesAsync(
        IReadOnlyList<string> dropped,
        HierarchyNodeViewModel? target = null,
        CancellationToken cancellationToken = default)
    {
        if (dropped.Count == 0) return;

        foreach (string raw in dropped)
        {
            if (DroppedItem.Parse(raw) is not { } item) continue;

            if (!TryResolveDropped(item, out string? key) || key is null)
            {
                _logger.LogWarning("Drop: '{File}' is not an imported asset.", item.FileName);
                continue;
            }

            if (!_assets.IsSceneAsset(key))
            {
                _logger.LogWarning("Drop: '{File}' is not a scene (only scenes go into the hierarchy).", item.FileName);
                continue;
            }

            if (target is null)
            {
                LoadSceneByKey(key);
                continue;
            }

            SelectedNode = target;
            await AddPrefabChildAsync(new AssetKey(key), cancellationToken);
        }
    }

    /// <summary>アセット参照フィールドへ落とされたファイルの参照としての解決</summary>
    /// <param name="dropped">落とされた指し先（<c>file://</c> の URI・実パス・ファイル名のいずれか）。</param>
    /// <param name="constraintTypeName">フィールドが要求する型の完全修飾名。<c>null</c> なら型を問わない。</param>
    /// <returns>入れられるものが見つかれば <c>true</c>。</returns>
    /// <remarks>受け入れの規則はピッカーの候補と同じ。複数落とされても入るのは先頭から見て最初の 1 つ</remarks>
    public bool TryResolveDroppedAsset(
        IReadOnlyList<string> dropped,
        string? constraintTypeName,
        out AssetKey? reference)
    {
        reference = null;
        if (dropped.Count == 0) return false;

        foreach (string raw in dropped)
        {
            if (DroppedItem.Parse(raw) is not { } item) continue;

            if (!TryResolveDropped(item, out string? key) || key is null)
            {
                _logger.LogWarning("Drop: '{File}' is not an imported asset.", item.FileName);
                continue;
            }

            var candidate = new AssetKey(key);
            if (!EditorCandidates.Matches(
                    _assets,
                    candidate,
                    _assets.IsSceneAsset(key),
                    constraintTypeName))
            {
                _logger.LogWarning(
                    "Drop: '{File}' is not a {Type}.", item.FileName, ObjectSchema.ShortNameOf(constraintTypeName!));
                continue;
            }

            reference = candidate;
            return true;
        }
        return false;
    }

    private bool TryResolveDropped(DroppedItem item, out string? key)
    {
        key = null;
        return item.FullPath is { Length: > 0 } path
            ? _assets.TryGetKeyBySourcePath(path, out key)
            : _assets.TryGetKeyByFileName(item.FileName, out key);
    }

    /// <summary>新規シーンを作れるか（アセットサービスと .scene インポーターが揃っているか）</summary>
    public bool CanCreateScene => HasSceneImporter<ISceneImporter>();

    /// <summary>空のシーンの新規作成</summary>
    public async Task CreateNewSceneAsync(
        string? relativeFolder = null, string? requestedName = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetImporter(".scene", out ISceneImporter sceneImporter))
        {
            _logger.LogWarning("Cannot create scene: no .scene importer registered.");
            return;
        }

        string targetDir = _imports.ResolveFolder(relativeFolder);
        Directory.CreateDirectory(targetDir);

        string baseName = SanitizeSceneName(requestedName);
        string path = UniqueScenePath(targetDir, baseName);
        string sceneName = Path.GetFileNameWithoutExtension(path);

        var emptyScene = new HierarchyNode(Guid.NewGuid().ToString(), sceneName);
        try
        {
            await sceneImporter.SaveAsync(emptyScene, path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create scene '{Scene}': {Error}", Path.GetFileName(path), ex.Message);
            return;
        }

        AssetImportResult result = await _imports.ImportSingleAsync(path, cancellationToken);
        RefreshAssetViews();

        if (result.Success && result.Asset is { Key: { Length: > 0 } key })
        {
            _logger.LogInformation("Created scene: {Scene}", Path.GetFileName(path));
            LoadSceneByKey(key);
        }
    }


    /// <summary>選択ノードの下へ <c>.scene</c> をネスト配置できるか</summary>
    public bool CanAddPrefabChild =>
        _selectedNode is not null && HasSceneImporter<INestedPrefabImporter>();

    /// <summary>選ばれた <c>.scene</c> アセットの選択ノードの子としてのネスト配置</summary>
    public async Task AddPrefabChildAsync(AssetKey reference, CancellationToken cancellationToken = default)
    {
        if (!TryGetImporter(".scene", out INestedPrefabImporter nestedImporter))
        {
            _logger.LogWarning("Cannot add prefab child: no nesting-capable .scene importer registered.");
            return;
        }

        string assetKey = reference.Value;
        if (!IsSceneSourceAsset(assetKey)
            || !_assets.TryGetSourcePath(assetKey, out string? sourcePath)
            || sourcePath is null)
        {
            _logger.LogWarning("Cannot add prefab child: pick a .scene asset.");
            return;
        }

        HierarchyNodeViewModel? parentVm = _selectedNode;
        if (parentVm is null || OwningRoot(parentVm) is not { } owning) return;

        if (_loadedSceneKeys.TryGetValue(owning.SceneId, out string? hostKey)
            && string.Equals(hostKey, assetKey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Cannot add prefab child: a scene cannot nest itself.");
            return;
        }

        HierarchyNode instance;
        try
        {
            instance = await nestedImporter.InstantiateNestedAsync(assetKey, sourcePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to add prefab child: {Error}", ex.Message);
            return;
        }

        if (!ReferenceEquals(OwningRoot(parentVm), owning)) return;
        HierarchyNodeViewModel viewModel = ToViewModel(instance, parentVm.ObjectId);
        parentVm.Children.Add(viewModel);
        SelectedNode = viewModel;

        MarkSceneDirty(parentVm);
        PublishStructuralEdit();
    }

    /// <summary>選択中ノードを別プレハブへ切り出せるか</summary>
    public bool CanExtractPrefab =>
        !_isPlaying
        && _selectedNode is not null
        && !IsSceneRoot(_selectedNode)
        && HasSceneImporter<INestedPrefabImporter>()
        && !IsAtOrInsideNestedInstance(_selectedNode);

    private bool IsAtOrInsideNestedInstance(HierarchyNodeViewModel vm)
    {
        if (!TryGetImporter(".scene", out INestedPrefabImporter nested)) return false;
        if (OwningRoot(vm) is not { } owning) return false;

        if (Tree.FindPath(owning, VmChildren, node => ReferenceEquals(node, vm)) is not { } path)
            return false;
        for (int i = 1; i < path.Count; i++)
            if (nested.IsNestedInstanceRoot(path[i].ObjectId, path[i - 1].ObjectId)) return true;
        return false;
    }

    /// <summary>選択中サブツリーの新しい <c>.scene</c> への切り出しとネスト配置への差し替え</summary>
    public async Task ExtractPrefabAsync(string? requestedName = null, CancellationToken cancellationToken = default)
    {
        if (_selectedNode is null) return;
        if (!CanExtractPrefab)
        {
            _logger.LogWarning("Cannot extract prefab: select a non-root object outside nested prefab instances (and stop play mode).");
            return;
        }
        if (!TryGetImporter(".scene", out INestedPrefabImporter nestedImporter)) return;

        if (OwningRoot(_selectedNode) is not { } owning) return;
        HierarchyNodeViewModel selected = _selectedNode;
        HierarchyNodeViewModel? parentVm = FindParentVm(owning, selected.ObjectId);
        if (parentVm is null) return;
        HierarchyNode subtree = selected.Capture();

        string targetDir = OwningSceneKey(_selectedNode) is { } hostKey
            && _assets.TryGetSourcePath(hostKey, out string? hostPath) && hostPath is not null
            ? Path.GetDirectoryName(hostPath) ?? _imports.AssetsRootPath
            : _imports.AssetsRootPath;
        string baseName = SanitizeSceneName(requestedName ?? subtree.Name);
        string path = UniqueScenePath(targetDir, baseName);

        try
        {
            await nestedImporter.SaveAsync(subtree, path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to extract prefab '{Prefab}': {Error}", Path.GetFileName(path), ex.Message);
            return;
        }

        AssetImportResult result = await _imports.ImportSingleAsync(path, cancellationToken);
        RefreshAssetViews();
        if (!result.Success || result.Asset is not { Key: { Length: > 0 } key })
        {
            _logger.LogError(
                "Failed to extract prefab: import failed for {Prefab}: {Error}", Path.GetFileName(path), result.Message);
            return;
        }

        HierarchyNode instance;
        try
        {
            instance = await nestedImporter.InstantiateNestedAsync(key, path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to extract prefab: {Error}", ex.Message);
            return;
        }

        if (!ReferenceEquals(OwningRoot(parentVm), owning)) return;
        int vmIndex = parentVm.Children.IndexOf(selected);
        if (vmIndex < 0) return;

        HierarchyNodeViewModel newVm = ToViewModel(instance, parentVm.ObjectId);
        parentVm.Children[vmIndex] = newVm;
        var components = Tree.Flatten(owning, VmChildren).SelectMany(node => node.Components).ToArray();
        var snapshots = components.Select(component => component.Capture()).ToArray();
        PrefabAuthoring.RewriteObjectReferences(snapshots, PrefabAuthoring.MapIds(subtree, instance));
        for (int i = 0; i < components.Length; i++)
            UpdateComponentViewModel(components[i], snapshots[i]);
        SelectedNode = newVm;

        MarkSceneDirty(parentVm);
        PublishStructuralEdit();
        _logger.LogInformation("Extracted prefab: {Prefab}", Path.GetFileName(path));
    }


    /// <summary>保存先フォルダの候補（アセットルート基準の相対パス）</summary>
    public IReadOnlyList<string> GetAssetFolders() => _imports.EnumerateFolders();

    /// <summary>拡張子に結び付いたインポータの、求める役ができるものとしての取り出し</summary>
    /// <remarks>取り込みがまだ（サービスが居ない）ときも「その役は無い」と答える</remarks>
    private bool TryGetImporter<T>(string extension, out T importer) where T : class, IAssetImporter =>
        _imports.TryGetImporter(extension, out importer);

    /// <summary>その役ができる <c>.scene</c> インポータが居るか</summary>
    private bool HasSceneImporter<T>() where T : class, IAssetImporter =>
        _imports.HasImporter<T>(".scene");

    /// <summary>ソースが <c>.scene</c> のシーンアセットか（<c>.variant</c> を除く）</summary>
    private bool IsSceneSourceAsset(string assetKey) =>
        _assets.IsSceneAsset(assetKey)
        && _assets.IsSourceExtension(assetKey, ".scene");

    private static string SanitizeSceneName(string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return "NewScene";

        string name = requestedName.Trim();
        if (name.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            name = name[..^".scene".Length];

        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid.ToString(), string.Empty);

        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "NewScene" : name;
    }

    private static string UniqueScenePath(string dir, string baseName) =>
        AssetInspectorViewModel.UniquePath(dir, baseName, ".scene");

    private void LoadSceneByKey(string assetKey)
    {
        if (_assets.GetSceneRoot(assetKey) is not { } template) return;

        var instance = InstantiateTemplate(template);
        _loadedSceneKeys[instance.SceneId] = assetKey;

        HierarchyNodeViewModel viewModel = ToViewModel(instance);
        viewModel.IsSceneRoot = true;
        RestoreHierarchyExpansion(viewModel);
        RootNodes.Add(viewModel);
        SelectedNode ??= viewModel;
        FinishLoad(LoadHistory.Commit);
    }

    /// <summary>書き換わったアーティファクトのロード済みシーンへの反映</summary>
    public void ReloadImportedScenes(IReadOnlyCollection<string> updatedKeys)
    {
        if (_isPlaying || updatedKeys.Count == 0 || RootNodes.Count == 0) return;
        string? selectedId = SelectedNode?.ObjectId;
        string? selectedRootId = OwningRoot(SelectedNode)?.SceneId;
        bool replaced = false;
        foreach (HierarchyNodeViewModel root in RootNodes)
        {
            if (!_loadedSceneKeys.TryGetValue(root.SceneId, out string? key)) continue;
            if (!updatedKeys.Contains(key)) continue;

            if (_dirtySceneIds.Contains(root.SceneId))
            {
                _logger.LogWarning("Scene '{Scene}' was reimported but has unsaved changes; not reloaded.", root.Name);
                continue;
            }

            if (_assets.GetSceneRoot(key) is not { } template) continue;

            UpdateNode(root, InstantiateTemplate(template, root.SceneId));
            RestoreHierarchyExpansion(root);
            replaced = true;
        }

        if (!replaced) return;

        RestoreSelection(selectedId, selectedRootId);
        UpdateDirtyMarkers();

        PublishStructuralEdit();
    }

    public void SaveScene()
    {
        if (_isPlaying) return;
        HierarchyNodeViewModel? root = OwningRoot(_selectedNode);
        if (root is null || !_loadedSceneKeys.TryGetValue(root.SceneId, out string? key)) return;
        SceneSaveRequested?.Invoke(root.Capture(), key);

        if (_dirtySceneIds.Remove(root.SceneId))
            UpdateDirtyMarkers();
    }

    /// <summary>選択中ノードが属するシーンインスタンスをアンロード（世界から取り除き）できるか</summary>
    public bool CanUnloadScene => OwningRoot(_selectedNode) is not null;

    /// <summary>選択中ノードが属するシーンインスタンスを世界から取り除く（子ごと破棄）</summary>
    public void UnloadSelectedScene() => UnloadScene(_selectedNode);

    /// <summary>指定ノードが属するシーンインスタンスの世界からの取り除き</summary>
    public void UnloadScene(HierarchyNodeViewModel? node)
    {
        if (OwningRoot(node) is not { } owning) return;

        string rootId = owning.SceneId;
        RootNodes.Remove(owning);
        _loadedSceneKeys.Remove(rootId);
        _dirtySceneIds.Remove(rootId);
        SelectedNode = RootNodes.FirstOrDefault();

        PublishStructuralEdit();
    }

    /// <summary>シーンインスタンス ID を指定してヒエラルキーからアンロードする</summary>
    public bool TryUnloadScene(string sceneId)
    {
        HierarchyNodeViewModel? root = RootNodes.FirstOrDefault(
            candidate => string.Equals(candidate.SceneId, sceneId, StringComparison.Ordinal));
        if (root is null) return false;

        UnloadScene(root);
        return true;
    }

    /// <summary>現在のヒエラルキーにロードされているシーンを上から順に返す</summary>
    public IReadOnlyList<LoadedSceneInfo> GetLoadedScenes() =>
        RootNodes.Select(DescribeLoadedScene).ToList();

    private LoadedSceneInfo DescribeLoadedScene(HierarchyNodeViewModel root)
    {
        string assetKey = _loadedSceneKeys.GetValueOrDefault(root.SceneId) ?? string.Empty;
        return new LoadedSceneInfo(
            root.SceneId,
            root.Name,
            assetKey,
            ResolveAssetDisplay(assetKey) ?? assetKey);
    }

    /// <summary>選択中ノードの子への空オブジェクトの追加</summary>
    public void AddObject()
    {
        if (RootNodes.Count == 0) return;

        HierarchyNodeViewModel parentVm = _selectedNode ?? RootNodes[0];
        if (OwningRoot(parentVm) is null) return;

        var viewModel = new HierarchyNodeViewModel(Guid.NewGuid().ToString(), "GameObject");
        parentVm.Children.Add(viewModel);
        SelectedNode = viewModel;

        MarkSceneDirty(parentVm);
        PublishStructuralEdit();
    }

    /// <summary>選択中オブジェクトの削除</summary>
    public void DeleteSelectedObject()
    {
        if (!CanModifySelectedObject || _selectedNode is null || RootNodes.Count == 0) return;
        if (OwningRoot(_selectedNode) is not { } owning) return;

        HierarchyNodeViewModel? parentVm = FindParentVm(owning, _selectedNode.ObjectId);
        if (parentVm is null) return;
        parentVm.Children.Remove(_selectedNode);
        MarkSceneDirty(owning);
        SelectedNode = RootNodes.FirstOrDefault();

        PublishStructuralEdit();
    }

    /// <summary>選択中オブジェクトを同階層で並べ替える（direction: -1 上 / +1 下）</summary>
    public void MoveSelectedObject(int direction)
    {
        if (!CanModifySelectedObject || _selectedNode is null || RootNodes.Count == 0) return;
        if (OwningRoot(_selectedNode) is not { } owning) return;

        HierarchyNodeViewModel? parentVm = FindParentVm(owning, _selectedNode.ObjectId);
        if (parentVm is null) return;

        int index = parentVm.Children.IndexOf(_selectedNode);
        int newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= parentVm.Children.Count) return;

        parentVm.Children.Move(index, newIndex);

        MarkSceneDirty(owning);
        PublishStructuralEdit();
    }

    /// <summary>クリップボードにオブジェクトがあり貼り付け先があるか</summary>
    public bool CanPasteObject => _clipboardNode is not null && RootNodes.Count > 0;

    /// <summary>選択中オブジェクトのディープコピーをクリップボードへ取る</summary>
    public void CopySelectedObject()
    {
        if (_selectedNode is null) return;
        if (OwningRoot(_selectedNode) is null) return;

        _clipboardNode = _selectedNode.Capture();
        OnPropertyChanged(nameof(CanPasteObject));
    }

    /// <summary>クリップボードのオブジェクトの選択中ノードの子としての貼り付け</summary>
    public void PasteObject()
    {
        if (_clipboardNode is null || RootNodes.Count == 0) return;

        HierarchyNodeViewModel parentVm = _selectedNode ?? RootNodes[0];
        if (OwningRoot(parentVm) is null) return;

        HierarchyNode pasted = WithFreshIds(_clipboardNode);

        HierarchyNodeViewModel viewModel = ToViewModel(pasted, parentVm.ObjectId);
        parentVm.Children.Add(viewModel);
        SelectedNode = viewModel;

        MarkSceneDirty(parentVm);
        PublishStructuralEdit();
    }

    /// <summary>ヒエラルキーで今掴んでいるノード（掴んでいなければ <c>null</c>）</summary>
    /// <remarks>全ての画面で共有する。変更通知は出さない</remarks>
    public HierarchyNodeViewModel? DraggingNode { get; set; }

    /// <summary>ドラッグ&amp;ドロップでの source の target に対する移動</summary>
    public void MoveObject(HierarchyNodeViewModel source, HierarchyNodeViewModel target, DropPosition position)
    {
        if (RootNodes.Count == 0) return;
        if (ReferenceEquals(source, target) || IsSceneRoot(source)) return;
        if (IsDescendant(source, target)) return;

        if (OwningRoot(source) is not { } sourceOwner) return;
        HierarchyNodeViewModel? sourceParentVm = FindParentVm(sourceOwner, source.ObjectId);
        if (sourceParentVm is null) return;

        if (OwningRoot(target) is not { } targetOwner) return;

        if (position == DropPosition.Into)
        {
            sourceParentVm.Children.Remove(source);
            target.Children.Add(source);
            source.IsPrefabRoot = IsNestedPrefabRoot(source.ObjectId, target.ObjectId);
        }
        else
        {
            if (IsSceneRoot(target)) return;
            HierarchyNodeViewModel? targetParentVm = FindParentVm(targetOwner, target.ObjectId);
            if (targetParentVm is null) return;

            sourceParentVm.Children.Remove(source);

            int vmIndex = targetParentVm.Children.IndexOf(target);
            if (position == DropPosition.After) vmIndex++;

            targetParentVm.Children.Insert(vmIndex, source);
            source.IsPrefabRoot = IsNestedPrefabRoot(source.ObjectId, targetParentVm.ObjectId);
        }

        MarkSceneDirty(sourceOwner);
        MarkSceneDirty(targetOwner);
        PublishStructuralEdit();
    }

    /// <summary><paramref name="node"/> が <paramref name="ancestor"/> の下にあるか（自分自身は含まない）</summary>
    private static bool IsDescendant(HierarchyNodeViewModel ancestor, HierarchyNodeViewModel node) =>
        !ReferenceEquals(ancestor, node) && ContainsByRef(ancestor, node);

    private static HierarchyNodeViewModel? FindParentVm(HierarchyNodeViewModel root, string childId) =>
        Tree.FindParent(root, VmChildren, vm => HasId(vm.ObjectId, childId));

    /// <summary>追加可能なコンポーネント型</summary>
    public IReadOnlyList<ComponentTypeOption> AvailableComponentTypes
        => _typeCatalog.GetComponentTypes();

    /// <summary>コンポーネント型 id に対応するソース位置</summary>
    /// <remarks>カタログに記録された位置が実在するファイルを指す場合だけ、絶対パスにして返す</remarks>
    public TypeSourceLocation? GetComponentSourceLocation(string typeId)
    {
        if (_typeCatalog.GetSourceLocation(typeId) is not { } source) return null;

        try
        {
            string fullPath = Path.GetFullPath(source.Path);
            return File.Exists(fullPath) ? new TypeSourceLocation(fullPath, source.Line) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>選択中ノードへの指定型のコンポーネントの追加</summary>
    public void AddComponent(ComponentTypeOption option)
    {
        if (_selectedNode is null) return;
        if (OwningRoot(_selectedNode) is null) return;

        AuthoringObject instance = option.Factory();

        _selectedNode.Components.Add(CreateComponentViewModel(instance));
        OnPropertyChanged(nameof(SelectedComponents));

        MarkSceneDirty(_selectedNode);
        PublishStructuralEdit();
    }

    /// <summary>選択中ノードから指定コンポーネントを取り外す</summary>
    public void RemoveComponent(AuthoringObjectViewModel component)
    {
        if (_selectedNode is null) return;

        int index = _selectedNode.Components.IndexOf(component);
        if (index < 0) return;
        if (OwningRoot(_selectedNode) is null) return;
        _selectedNode.Components.RemoveAt(index);
        OnPropertyChanged(nameof(SelectedComponents));

        MarkSceneDirty(_selectedNode);
        PublishStructuralEdit();
    }

    /// <summary>クリップボードの値を貼り付けられる対象があるか</summary>
    /// <remarks>アセットの表示中は同じ型のアセットが対象になり、それ以外では選択中のノードが対象になる。</remarks>
    public bool CanPasteComponent =>
        _clipboardComponent is not null
        && (Asset.IsInspecting ? Asset.CanPaste(_clipboardComponent) : _selectedNode is not null);

    /// <summary>指定コンポーネントのディープコピーをクリップボードへ取る</summary>
    public void CopyComponent(AuthoringObjectViewModel component)
    {
        _clipboardComponent = component.Capture();
        OnPropertyChanged(nameof(CanPasteComponent));
    }

    /// <summary>クリップボードの値の貼り付け</summary>
    /// <remarks>アセットを映している間はそのアセットへ、そうでなければ選択中ノードへ足す</remarks>
    public void PasteComponent()
    {
        if (_clipboardComponent is null) return;

        if (Asset.IsInspecting)
        {
            Asset.Paste(_clipboardComponent);
            return;
        }

        if (_selectedNode is null) return;
        if (OwningRoot(_selectedNode) is null) return;

        AuthoringObject instance = _clipboardComponent.Clone();

        _selectedNode.Components.Add(CreateComponentViewModel(instance));
        OnPropertyChanged(nameof(SelectedComponents));

        MarkSceneDirty(_selectedNode);
        PublishStructuralEdit();
    }

    private static HierarchyNode InstantiateTemplate(HierarchyNode template, string? sceneId = null)
        => new(template.ObjectId, template.Name, template.Children, template.Components,
            sceneId: sceneId ?? Guid.NewGuid().ToString(), active: template.Active);

    private static HierarchyNode WithFreshIds(HierarchyNode node)
    {
        var children = node.Children.Select(WithFreshIds).ToList();
        return new HierarchyNode(Guid.NewGuid().ToString(), node.Name, children, node.Components, active: node.Active);
    }

    /// <summary>権威状態の送信を 1 本始めることの通知</summary>
    internal void BeginAuthoritativeSend() => Interlocked.Increment(ref _pendingAuthoritativeSends);

    /// <summary>権威状態の送信が 1 本終わったことの通知</summary>
    internal void EndAuthoritativeSend() => Interlocked.Decrement(ref _pendingAuthoritativeSends);

    /// <summary>捨てるまで送信中として数えさせる控え</summary>
    private IDisposable HoldAuthoritativeSend()
    {
        BeginAuthoritativeSend();
        return new AuthoritativeSendHold(this);
    }

    private sealed class AuthoritativeSendHold(EditorViewModel owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.EndAuthoritativeSend();
        }
    }

    /// <summary>参照ピッカーを開くことの通知</summary>
    public void BeginPickerSuspension()
    {
        if (Interlocked.Increment(ref _pickerSuspendDepth) == 1) OnPropertyChanged(nameof(IsPickerOpen));
    }

    /// <summary>参照ピッカーを閉じたことの通知</summary>
    /// <remarks>開いている間に来たソースアセットの取り込み要求は捨てられるので、閉じたことを知らせる</remarks>
    public void ResumePickerSuspension()
    {
        if (Interlocked.Decrement(ref _pickerSuspendDepth) == 0) OnPropertyChanged(nameof(IsPickerOpen));
    }

    /// <summary>参照ピッカーを開いている間 <c>true</c></summary>
    public bool IsPickerOpen => Volatile.Read(ref _pickerSuspendDepth) > 0;

    private void PublishStructuralEdit()
    {
        CancelPendingEdits();
        List<HierarchyNode> roots = CommitEdit(null);
        ScenePublished?.Invoke(new ScenePublication(roots, Resend: false));
    }

    /// <summary>編集後の状態を一度確定し、同じ値を履歴と送信へ渡す。</summary>
    private List<HierarchyNode> CommitEdit(string? editKey)
    {
        List<HierarchyNode> roots = CaptureHierarchy();
        if (_history.Enabled && !_history.IsSuspended)
            _history.Commit(editKey, CaptureWorld(roots));
        return roots;
    }

    private List<HierarchyNode> CaptureHierarchy() => RootNodes.Select(node => node.Capture()).ToList();

    /// <summary>現在の世界の 1 段分の状態としての写し取り</summary>
    private SceneSnapshot CaptureWorld() => CaptureWorld(CaptureHierarchy());

    private SceneSnapshot CaptureWorld(IReadOnlyList<HierarchyNode> roots)
    {
        return new SceneSnapshot(
            _cloneSerializer.Serialize(roots),
            new Dictionary<string, string>(_loadedSceneKeys, StringComparer.Ordinal),
            new HashSet<string>(_dirtySceneIds, StringComparer.Ordinal));
    }

    /// <summary>履歴の 1 段への巻き戻しとランタイムへの再送</summary>
    /// <remarks>履歴の記録を停止する責務は呼び出し側にある（<see cref="EditHistoryViewModel.Restoring"/> は止めた中で呼ぶ）。</remarks>
    private void RestoreWorld(SceneSnapshot snapshot)
    {
        CancelPendingEdits();

        List<HierarchyNode> roots = _cloneSerializer.Deserialize(snapshot.Blob).ToList();

        AdoptSnapshotState(snapshot);

        SyncRootNodeViews(roots);
        UpdateDirtyMarkers();

        PublishStructuralEdit();
        OnPropertyChanged(nameof(CanSaveScene));
        OnPropertyChanged(nameof(CanUnloadScene));
        OnPropertyChanged(nameof(SelectedNodeName));
    }

    /// <summary>プレイ移行時のスナップショットへの巻き戻しとエディットモードでの再送</summary>
    private void RestoreSnapshot()
    {
        List<HierarchyNode>? restored = _editSnapshot;
        _editSnapshot = null;

        if (restored is not null)
        {
            _dirtySceneIds.Clear();
            foreach (string id in _snapshotDirtyIds) _dirtySceneIds.Add(id);
            _snapshotDirtyIds.Clear();

            SyncRootNodeViews(restored);
        }

        UpdateDirtyMarkers();

        using (_history.Suspend())
        {
            PublishStructuralEdit();
        }

        _history.PublishHistory();
    }

    /// <summary>dirty 集合の各シーンルートの表示への反映</summary>
    private void UpdateDirtyMarkers()
    {
        foreach (HierarchyNodeViewModel root in RootNodes)
            root.IsDirty = !_isPlaying && _dirtySceneIds.Contains(root.SceneId);
    }

    /// <summary>指定 VM が属するシーンの dirty 化</summary>
    private void MarkSceneDirty(HierarchyNodeViewModel? vm)
    {
        if (_isPlaying || vm is null) return;
        HierarchyNodeViewModel? root = RootNodes.FirstOrDefault(r => ContainsByRef(r, vm));
        if (root is null) return;
        if (_dirtySceneIds.Add(root.SceneId))
            root.IsDirty = true;
    }

    /// <summary>VM がシーンルートか</summary>
    private bool IsSceneRoot(HierarchyNodeViewModel? vm) =>
        vm is not null && RootNodes.Any(r => ReferenceEquals(r, vm));

    /// <summary>指定 VM が属するシーンインスタンスのルート</summary>
    private HierarchyNodeViewModel? OwningRoot(HierarchyNodeViewModel? vm) =>
        vm is null ? null : RootNodes.FirstOrDefault(root => ContainsByRef(root, vm));

    private static bool ContainsByRef(HierarchyNodeViewModel root, HierarchyNodeViewModel target) =>
        Tree.Find(root, VmChildren, vm => ReferenceEquals(vm, target)) is not null;

    /// <summary>指定 VM が属するシーンの保存先キー</summary>
    private string? OwningSceneKey(HierarchyNodeViewModel? vm)
    {
        HierarchyNodeViewModel? root = OwningRoot(vm);
        return root is not null && _loadedSceneKeys.TryGetValue(root.SceneId, out string? key) ? key : null;
    }

    /// <summary>アセット選択ダイアログ用の候補一覧</summary>
    public IReadOnlyList<AssetCandidate> GetAssetCandidates(string? constraintTypeName) =>
        EditorCandidates.ForAsset(_assets, constraintTypeName);

    /// <summary>シーンアセットだけの選択候補</summary>
    /// <param name="sceneSourceOnly">ソースが <c>.scene</c> のものだけに絞る（<c>.variant</c> を除く）。</param>
    /// <param name="excludeBuildScenes">既に Build Settings に載っているシーンを除く。</param>
    public IReadOnlyList<AssetCandidate> GetSceneCandidates(
        bool sceneSourceOnly = false, bool excludeBuildScenes = false) =>
        EditorCandidates.ForScene(
            _assets,
            key => (!sceneSourceOnly || IsSceneSourceAsset(key))
                   && (!excludeBuildScenes || !BuildScenes.Contains(key)));

    /// <param name="componentTypeName">型付き参照が要求するコンポーネント型の完全修飾名。<c>null</c> なら型を問わない。</param>
    public IReadOnlyList<ObjectCandidate> GetObjectCandidates(string? componentTypeName = null) =>
        OwningRoot(_selectedNode) is { } root
            ? EditorCandidates.ForObject(root, componentTypeName)
            : Array.Empty<ObjectCandidate>();

    private string? ResolveObjectDisplay(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        HierarchyNodeViewModel? root = OwningRoot(_selectedNode);
        return root is not null ? FindById(root, id)?.Name : null;
    }

    private bool IsNestedPrefabRoot(string objectId, string? parentObjectId) =>
        TryGetImporter(".scene", out INestedPrefabImporter nested)
        && nested.IsNestedInstanceRoot(objectId, parentObjectId);

    private HierarchyNodeViewModel ToViewModel(HierarchyNode node, string? parentObjectId = null)
    {
        var viewModel = new HierarchyNodeViewModel(node.ObjectId, node.Name)
        {
            SceneId = node.SceneId,
            IsPrefabRoot = IsNestedPrefabRoot(node.ObjectId, parentObjectId),
            Active = node.Active,
        };
        viewModel.Components.ReplaceAll(node.Components.Select(CreateComponentViewModel));
        viewModel.Children.ReplaceAll(node.Children.Select(child => ToViewModel(child, node.ObjectId)));
        return viewModel;
    }

    private void UpdateNode(HierarchyNodeViewModel viewModel, HierarchyNode node)
    {
        viewModel.SceneId = node.SceneId;
        viewModel.Name = node.Name;
        viewModel.Active = node.Active;

        UpdateComponents(viewModel.Components, node.Components);

        Reconcile.Into(
            viewModel.Children,
            node.Children,
            child => child.ObjectId,
            child => child.ObjectId,
            (childVm, child) =>
            {
                childVm.IsPrefabRoot = IsNestedPrefabRoot(child.ObjectId, node.ObjectId);
                UpdateNode(childVm, child);
            },
            child => ToViewModel(child, node.ObjectId));
    }

    private void UpdateComponents(
        SnapshotCollection<AuthoringObjectViewModel> viewModels,
        IReadOnlyList<AuthoringObject> components) =>
        Reconcile.Into(
            viewModels,
            components,
            viewModel => viewModel.Key,
            component => component.TypeName,
            UpdateComponentViewModel,
            CreateComponentViewModel);

    private string? ResolveAssetDisplay(string key) => _assets.ResolveDisplayPath(key);

    /// <summary>カタログが既定値を焼けなかった型か</summary>
    /// <remarks>カタログに無い型は false＝知らないものを咎めない</remarks>
    private bool HasNoCatalogDefault(AuthoringObject component)
        => !_typeCatalog.HasDefault(component.TypeName);

    private AuthoringObjectViewModel CreateComponentViewModel(AuthoringObject component)
    {
        string typeName = component.TypeName;
        var viewModel = new AuthoringObjectViewModel(
            typeName, component.Schema.DisplayName, component,
            editKey => PublishEdits(editKey is null ? null : $"{typeName}/{editKey}"),
            ResolveAssetDisplay, ResolveObjectDisplay);
        viewModel.SetMissingInRuntime(_reattachedComponents.Contains(component));
        viewModel.SetDefaultsMissing(HasNoCatalogDefault(component));
        return viewModel;
    }

    private void UpdateComponentViewModel(AuthoringObjectViewModel viewModel, AuthoringObject component)
    {
        using IDisposable refresh = viewModel.DeferRefresh();
        viewModel.Apply(component, force: !_applyingRuntimeSnapshot);
        viewModel.SetMissingInRuntime(_reattachedComponents.Contains(component));
        viewModel.SetDefaultsMissing(HasNoCatalogDefault(component));
    }

    /// <param name="editKey">編集したフィールドの識別キー。直前と同じなら undo 1 段にまとめる。</param>
    private void PublishEdits(string? editKey = null)
    {
        if (RootNodes.Count == 0) return;

        MarkSceneDirty(_selectedNode);

        List<HierarchyNode> roots = CommitEdit(editKey is null ? null : $"{_selectedNode?.ObjectId}/{editKey}");

        CancelPendingEdits();

        TimeSpan wait = _editStreamWindow.Remaining;
        if (wait <= TimeSpan.Zero)
        {
            PushEdits(roots);
            return;
        }

        // 待っている間も送信中として数える（この間に届いた応答は編集より古い）
        _editStream.Schedule(wait, () => PushEdits(roots), HoldAuthoritativeSend());
    }

    private void CancelPendingEdits() => _editStream.Cancel();

    /// <summary>権威ツリーの購読者への引き渡し</summary>
    /// <remarks>非同期に送信する購読者は、イベント処理を返す前に <see cref="BeginAuthoritativeSend"/> を呼び、送信終了時に <see cref="EndAuthoritativeSend"/> を呼ぶこと。</remarks>
    private void PushEdits(IReadOnlyList<HierarchyNode> roots)
    {
        BeginAuthoritativeSend();
        try
        {
            ScenePublished?.Invoke(new ScenePublication(roots, Resend: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Edit send skipped: {Error}", ex.Message);
        }
        finally
        {
            _editStreamWindow.Mark();
            EndAuthoritativeSend();
        }
    }

    private static HierarchyNodeViewModel? FindById(HierarchyNodeViewModel root, string id) =>
        Tree.Find(root, VmChildren, vm => HasId(vm.ObjectId, id));
}
