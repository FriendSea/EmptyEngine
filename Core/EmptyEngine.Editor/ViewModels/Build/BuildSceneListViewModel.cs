using EmptyEngine.Core;
using EmptyEngine.Editor.Distribution;

namespace EmptyEngine.Editor.ViewModels.Build;

/// <summary>ビルド対象シーンの保存先、判定、表示名を提供する。</summary>
/// <remarks><see cref="AssetsRootPath"/> が <c>null</c> の間は一覧を読み書きしない。</remarks>
public interface IBuildSceneSource
{
    /// <summary>Assets の根（取り込みがまだなら <c>null</c>）</summary>
    string? AssetsRootPath { get; }

    /// <summary>そのアセットキーがシーンか</summary>
    bool IsSceneAsset(string key);

    /// <summary>一覧に出す名前（引けなければ <c>null</c>）</summary>
    string? ResolveDisplayName(string key);
}

/// <summary>デプロイ対象シーンの順序付きリスト（Build Settings）</summary>
/// <remarks>先頭が起動シーンになる。変更は <c>BuildScenes.json</c> へ自動保存される。</remarks>
public sealed class BuildSceneListViewModel : ViewModelBase
{
    private readonly IBuildSceneSource _source;
    private BuildSceneItemViewModel? _selected;

    public BuildSceneListViewModel(IBuildSceneSource source) => _source = source;

    /// <summary>並び順のままの項目</summary>
    public SnapshotCollection<BuildSceneItemViewModel> Items { get; } = new();

    /// <summary>並び順のままのアセットキー（先頭が起動シーン）</summary>
    public IEnumerable<string> Keys => Items.Select(item => item.Key);

    /// <summary>そのアセットキーが既に載っているか</summary>
    public bool Contains(string key) =>
        Items.Any(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    public BuildSceneItemViewModel? Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(CanMove));
        }
    }

    public bool CanRemove => _selected is not null;

    public bool CanMove => _selected is not null && Items.Count > 1;

    /// <summary>ソース設定 <c>BuildScenes.json</c> からの読み直し</summary>
    public void Reload()
    {
        if (_source.AssetsRootPath is not { } root)
        {
            Items.ReplaceAll([]);
            return;
        }

        Items.ReplaceAll(BuildSceneList.Read(root)
            .Select(key => new BuildSceneItemViewModel(key, _source.ResolveDisplayName(key) ?? key)));

        UpdateStartupMarkers();
    }

    /// <summary>末尾への追加（シーンでないキーと重複は黙って捨てる）</summary>
    public void Add(AssetKey reference)
    {
        string key = reference.Value;
        if (!_source.IsSceneAsset(key)) return;
        if (Contains(key)) return;

        Items.Add(new BuildSceneItemViewModel(key, _source.ResolveDisplayName(key) ?? key));
        Persist();
        OnPropertyChanged(nameof(CanMove));
    }

    /// <summary>選択中の項目をリストから除く</summary>
    public void RemoveSelected()
    {
        if (_selected is null) return;

        Items.Remove(_selected);
        Selected = null;
        Persist();
        OnPropertyChanged(nameof(CanMove));
    }

    /// <summary>選択中の項目の順序内での移動</summary>
    public void Move(int direction)
    {
        if (_selected is null) return;

        int index = Items.IndexOf(_selected);
        int newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= Items.Count) return;

        Items.Move(index, newIndex);
        Persist();
    }

    /// <summary>表示名の引き直し（アセットの取り込み後）</summary>
    public void RefreshDisplayNames()
    {
        foreach (BuildSceneItemViewModel item in Items)
            item.DisplayName = _source.ResolveDisplayName(item.Key) ?? item.Key;
    }

    private void Persist()
    {
        UpdateStartupMarkers();
        if (_source.AssetsRootPath is not { } root) return;

        BuildSceneList.Write(root, Keys.ToList());
    }

    private void UpdateStartupMarkers()
    {
        for (int i = 0; i < Items.Count; i++)
            Items[i].IsStartup = i == 0;
    }
}
