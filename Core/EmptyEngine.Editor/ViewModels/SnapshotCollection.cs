using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EmptyEngine.Editor.ViewModels;

/// <summary>変更のたびに固定された並びを作り直して公開する <see cref="ObservableCollection{T}"/></summary>
public sealed class SnapshotCollection<T> : ObservableCollection<T>
{
    private T[] _snapshot = [];

    public SnapshotCollection()
    {
    }

    public SnapshotCollection(IEnumerable<T> items) : base(items) => Publish();

    /// <summary>列挙用の固定された並び</summary>
    public IReadOnlyList<T> Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>中身の一括差し替え</summary>
    /// <remarks>変更通知は 1 回だけ発行する。</remarks>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (T item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        Publish();
        base.OnCollectionChanged(e);
    }

    private void Publish() => Volatile.Write(ref _snapshot, [.. Items]);
}
