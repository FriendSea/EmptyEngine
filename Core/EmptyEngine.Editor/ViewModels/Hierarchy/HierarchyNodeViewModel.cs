using EmptyEngine.Editor.ViewModels.Inspector;

namespace EmptyEngine.Editor.ViewModels.Hierarchy;

public sealed class HierarchyNodeViewModel : ViewModelBase
{
    private string _name;
    private bool _isDirty;
    private bool _isSceneRoot;
    private bool _isPrefabRoot;
    private bool _isExpanded;
    private bool _active = true;

    public HierarchyNodeViewModel(string objectId, string name)
    {
        ObjectId = objectId;
        _name = name;
        Children = new SnapshotCollection<HierarchyNodeViewModel>();
        Components = new SnapshotCollection<AuthoringObjectViewModel>();
    }

    /// <summary>オブジェクトの安定 Id（参照先）</summary>
    public string ObjectId { get; }

    /// <summary>このノードが属するロード単位の識別子</summary>
    public string SceneId { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>未保存の編集がある（dirty な）シーンか</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>ツリー表示名</summary>
    public string DisplayName => _isDirty ? "* " + _name : _name;

    public bool Active
    {
        get => _active;
        set => SetProperty(ref _active, value);
    }

    /// <summary>このノードがシーンインスタンスのルートか</summary>
    public bool IsSceneRoot
    {
        get => _isSceneRoot;
        set => SetProperty(ref _isSceneRoot, value);
    }

    /// <summary>このノードがネスト配置されたプレハブインスタンスのルートか</summary>
    public bool IsPrefabRoot
    {
        get => _isPrefabRoot;
        set => SetProperty(ref _isPrefabRoot, value);
    }

    /// <summary>Hierarchy で子ノードを展開しているか</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public SnapshotCollection<HierarchyNodeViewModel> Children { get; }

    public SnapshotCollection<AuthoringObjectViewModel> Components { get; }

    /// <summary>絞り込みに一致するか（選択中ノードとその祖先も表示する）</summary>
    public bool MatchesFilter(string? filter, HierarchyNodeViewModel? selected) =>
        string.IsNullOrWhiteSpace(filter)
        || ReferenceEquals(this, selected)
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Children.Snapshot.Any(child => child.MatchesFilter(filter, selected));

    /// <summary>保存・通信に使う独立したツリーを取得する。</summary>
    /// <remarks>取得後の編集は、コンポーネントの値も含めて返したツリーに反映されない。</remarks>
    internal HierarchyNode Capture() => new(
        ObjectId,
        Name,
        Children.Select(child => child.Capture()).ToArray(),
        Components.Select(component => component.Capture()).ToArray(),
        SceneId,
        Active);
}
