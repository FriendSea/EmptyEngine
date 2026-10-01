using EmptyEngine.Editor.State;

namespace EmptyEngine.Editor.ViewModels;

/// <summary>undo/redo の履歴を管理し、復元するシーン状態を通知する。</summary>
/// <remarks><see cref="Enabled"/> が <c>false</c> の間は履歴の記録・復元・通知を行わない。</remarks>
public sealed class EditHistoryViewModel : ViewModelBase
{
    /// <summary>保持する undo 段数の上限（現在状態を除く）</summary>
    private const int DefaultCapacity = 50;

    private readonly int _capacity;
    private readonly List<EditHistoryStep> _steps = new();
    private ulong _nextKey = 1;
    private int _cursor = -1;

    private string? _openEditKey;
    private bool _suspended;
    private bool _enabled = true;

    public EditHistoryViewModel()
        : this(DefaultCapacity)
    {
    }

    /// <param name="capacity">保持する undo 段数の上限（現在状態を除く）。超えたら古い方から落とす</param>
    internal EditHistoryViewModel(int capacity) => _capacity = capacity;

    /// <summary>戻る先の段が決まったことの通知</summary>
    /// <remarks>購読側がその段を世界へ当てる。通知の間は <see cref="IsSuspended"/> なので、世界が動いても積まない</remarks>
    internal event Action<SceneSnapshot>? Restoring;

    /// <summary>履歴の内容が変わったときに通知する。</summary>
    internal event Action<EditHistory>? HistoryChanged;

    /// <summary>履歴を動かしてよいか</summary>
    /// <remarks>プレイ中は <c>false</c>。積むことも、戻ることも、送出もしない</remarks>
    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            RaiseCanChange();
        }
    }

    public bool CanUndo => _enabled && _cursor > 0;

    public bool CanRedo => _enabled && _cursor >= 0 && _cursor < _steps.Count - 1;

    /// <summary>基準点（現在状態）を持っているか</summary>
    internal bool HasBaseline => _cursor >= 0;

    /// <summary>戻している最中か</summary>
    /// <remarks>この間に世界が動いても積まない</remarks>
    internal bool IsSuspended => _suspended;

    /// <summary>まとめ中の編集の打ち切り</summary>
    /// <remarks>次の <see cref="Commit"/> は同じキーでも必ず新しい段になる（選択が変わった、モードが変わった）</remarks>
    internal void CloseCoalescing() => _openEditKey = null;

    /// <summary>確定済みの状態の 1 段の積み込み</summary>
    /// <param name="editKey">
    /// 連続した編集を 1 段に畳むためのキー。直前と同じキーなら現在の段を置き換える。
    /// <c>null</c> なら必ず新しい段になる
    /// </param>
    /// <param name="snapshot">送信と共通の、確定済みのシーンの写し</param>
    internal void Commit(string? editKey, SceneSnapshot snapshot)
    {
        if (!_enabled || _suspended) return;

        bool coalesce = editKey is not null && string.Equals(editKey, _openEditKey, StringComparison.Ordinal);
        Push(snapshot, coalesce);
        _openEditKey = editKey;
        RaiseCanChange();
        PublishHistory();
    }

    /// <summary>履歴を捨てて <paramref name="baseline"/> だけを基準点にする</summary>
    internal void Reset(SceneSnapshot baseline)
    {
        _openEditKey = null;
        ResetSteps(baseline);
        RaiseCanChange();
        PublishHistory();
    }

    /// <summary>直前の編集の取り消し</summary>
    /// <remarks>戻る先が無くてもまとめは打ち切る（次の編集は必ず新しい段になる）</remarks>
    public void Undo()
    {
        if (!_enabled) return;
        _openEditKey = null;
        if (CanUndo) ApplyToWorld(_steps[--_cursor].Snapshot);
    }

    /// <summary>取り消した編集のやり直し</summary>
    public void Redo()
    {
        if (!_enabled) return;
        _openEditKey = null;
        if (CanRedo) ApplyToWorld(_steps[++_cursor].Snapshot);
    }

    /// <summary>別プロセスが積んだ履歴の引き継ぎ</summary>
    /// <remarks>シーン状態の復元は呼び出し側が行う。</remarks>
    internal void Adopt(EditHistory history)
    {
        if (history.Steps.Count == 0 || history.Cursor < 0 || history.Cursor >= history.Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(history), "The cursor must point at one of the steps.");

        _openEditKey = null;
        _steps.Clear();
        _steps.AddRange(history.Steps);
        _cursor = history.Cursor;
        _nextKey = history.Steps.Max(step => step.Key) + 1;
        TrimToCapacity();
        RaiseCanChange();
        PublishHistory();
    }

    /// <summary>世界を触る間だけ積むのをやめる</summary>
    /// <remarks>返されたスコープを破棄するまで、変更を履歴へ記録しない。</remarks>
    internal IDisposable Suspend() => new Suspension(this);

    /// <summary>今の履歴まるごとの送出</summary>
    internal void PublishHistory()
    {
        if (!_enabled || HistoryChanged is null) return;
        if (_cursor < 0) return;

        HistoryChanged.Invoke(new EditHistory(_steps.ToArray(), _cursor));
    }

    private void Push(SceneSnapshot snapshot, bool coalesce)
    {
        if (_cursor < 0)
        {
            ResetSteps(snapshot);
            return;
        }

        if (coalesce)
        {
            _steps[_cursor] = NewStep(snapshot);
            return;
        }

        _steps.RemoveRange(_cursor + 1, _steps.Count - _cursor - 1);
        _steps.Add(NewStep(snapshot));
        _cursor++;
        TrimToCapacity();
    }

    private void ResetSteps(SceneSnapshot baseline)
    {
        _steps.Clear();
        _steps.Add(NewStep(baseline));
        _cursor = 0;
    }

    /// <summary>キーは中身ごとに振る（上書きした段も新しいキーになる）</summary>
    private EditHistoryStep NewStep(SceneSnapshot snapshot) => new(_nextKey++, snapshot);

    private void TrimToCapacity()
    {
        while (_steps.Count > _capacity + 1 && _cursor > 0)
        {
            _steps.RemoveAt(0);
            _cursor--;
        }
    }

    private void ApplyToWorld(SceneSnapshot snapshot)
    {
        using (Suspend())
        {
            Restoring?.Invoke(snapshot);
        }

        PublishHistory();
        RaiseCanChange();
    }

    private void RaiseCanChange()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private sealed class Suspension : IDisposable
    {
        private readonly EditHistoryViewModel _owner;
        private readonly bool _previous;

        public Suspension(EditHistoryViewModel owner)
        {
            _owner = owner;
            _previous = owner._suspended;
            owner._suspended = true;
        }

        public void Dispose() => _owner._suspended = _previous;
    }
}
