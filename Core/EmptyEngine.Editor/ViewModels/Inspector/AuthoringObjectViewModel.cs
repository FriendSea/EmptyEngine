using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>コンポーネントまたはアセットのフィールドを表示・編集する。</summary>
public sealed class AuthoringObjectViewModel
{
    private readonly Action<string?> _onEdited;

    private readonly EditGate _edits = new();

    private int _deferDepth;
    private bool _refreshPending;
    private int _editDepth;
    private bool _editPending;

    public AuthoringObjectViewModel(
        string key,
        string displayName,
        AuthoringObject component,
        Action<string?> onEdited,
        Func<string, string?>? assetDisplayResolver = null,
        Func<string, string?>? objectDisplayResolver = null)
    {
        Key = key;
        DisplayName = displayName;
        _onEdited = onEdited;
        Schema = component.Schema;
        AssetDisplayResolver = assetDisplayResolver;
        ObjectDisplayResolver = objectDisplayResolver;
        Root = new FieldViewModel(this, string.Empty, Schema.Root, component.Data);
    }

    public string Key { get; }

    public string DisplayName { get; }

    public ObjectSchema Schema { get; }
    public string TypeName => Schema.TypeName;
    public FieldViewModel Root { get; }
    public SnapshotCollection<FieldViewModel> Fields => Root.Children;
    internal Func<string, string?>? AssetDisplayResolver { get; }
    internal Func<string, string?>? ObjectDisplayResolver { get; }

    /// <summary>保存・通信・Undo 用の独立した値。VM は DTO を保持しない。</summary>
    public AuthoringObject Capture() => new(Schema, Root.Capture());

    /// <summary>複数フィールドの変更を一つの編集として通知する。</summary>
    public void Edit(string? editKey, Action edit)
    {
        FieldValue? before = _editDepth == 0 ? Root.Capture() : null;
        _editDepth++;
        try { edit(); }
        catch
        {
            if (before is not null)
            {
                Root.Apply(before, force: true);
                _editPending = false;
            }
            throw;
        }
        finally
        {
            if (--_editDepth == 0 && _editPending)
            {
                _editPending = false;
                OnEdited(editKey);
            }
        }
    }

    /// <summary>ランタイムが返さず、エディタが付け直したものか</summary>
    /// <remarks>インスペクタはこれを欠損の印として出す</remarks>
    public bool MissingInRuntime { get; private set; }

    /// <summary>カタログが既定値を持っていない型か</summary>
    /// <remarks>新しく追加したコンポーネントは型固有の初期値を持たず、インスペクタに警告を表示する。</remarks>
    public bool DefaultsMissing { get; private set; }

    /// <summary>値の書き換え後のランタイムへの再送の要求</summary>
    /// <remarks>フィールド VM からの変更通知。複数欄をまとめる場合は Edit を使う。</remarks>
    public void OnEdited(string? editKey = null)
    {
        if (_editDepth > 0) { _editPending = true; return; }
        _onEdited(editKey);
    }

    /// <summary>このコンポーネントの編集の門</summary>
    /// <remarks>いずれかの欄を編集中は、コンポーネント全体の表示値の更新を抑制する。</remarks>
    internal EditGate Edits => _edits;

    /// <summary>いま触っていて、配り直された値を反映すべきでないか</summary>
    internal bool IsUserEditing => _edits.IsEditing;

    /// <summary>スナップショットや表示状態が適用された後の通知</summary>
    public event Action? Refreshed;

    /// <summary>スナップショットを既存の VM の木へ適用する。</summary>
    /// <remarks>欄の形の権威はカタログなので、宣言が入れ替わっていればここで追いつく。</remarks>
    public void Apply(AuthoringObject component, bool force = true)
    {
        if (component.TypeName != TypeName) throw new ArgumentException("Component type changed.", nameof(component));
        Root.Rebind(Schema.Root, component.Data, force);
        Raise();
    }

    /// <summary>欠損の印の付け外し</summary>
    internal void SetMissingInRuntime(bool missing)
    {
        if (MissingInRuntime == missing) return;

        MissingInRuntime = missing;
        Raise();
    }

    /// <summary>既定値なしの印の付け外し</summary>
    internal void SetDefaultsMissing(bool missing)
    {
        if (DefaultsMissing == missing) return;

        DefaultsMissing = missing;
        Raise();
    }

    /// <summary>更新一式のあいだ <see cref="Refreshed"/> を伏せ、抜けるときに 1 回だけ発火する</summary>
    internal IDisposable DeferRefresh() => new RefreshScope(this);

    private void Raise()
    {
        if (_deferDepth > 0)
        {
            _refreshPending = true;
            return;
        }

        Refreshed?.Invoke();
    }

    private sealed class RefreshScope : IDisposable
    {
        private readonly AuthoringObjectViewModel _owner;
        private bool _disposed;

        public RefreshScope(AuthoringObjectViewModel owner)
        {
            _owner = owner;
            _owner._deferDepth++;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _owner._deferDepth--;
            if (_owner._deferDepth > 0 || !_owner._refreshPending) return;

            _owner._refreshPending = false;
            _owner.Raise();
        }
    }
}
