using System.Collections.Specialized;
using System.ComponentModel;
using EmptyEngine.Editor.Hosting;
using Microsoft.AspNetCore.Components;

namespace EmptyEngine.Editor.Components;

/// <summary>ViewModel の変更通知を購読して描き直すコンポーネントの土台</summary>
public abstract class ObservingComponentBase : ComponentBase, IDisposable, IHandleEvent
{
    private readonly List<INotifyPropertyChanged> _properties = new();
    private readonly List<INotifyCollectionChanged> _collections = new();
    private bool _disposed;

    [Inject] protected EditorDispatcher Dispatcher { get; set; } = null!;

    /// <summary>UI の操作の <see cref="EditorDispatcher"/> 上での実行</summary>
    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem item, object? arg)
    {
        try { await Dispatcher.InvokeAsync(() => item.InvokeAsync(arg)); }
        finally { Rerender(); }
    }

    /// <summary>コンポーネントが生きている間の変更購読</summary>
    protected void Observe(INotifyPropertyChanged source)
    {
        source.PropertyChanged += OnSourceChanged;
        _properties.Add(source);
    }

    /// <summary>コレクションの増減の購読</summary>
    protected void ObserveCollection(INotifyCollectionChanged source)
    {
        source.CollectionChanged += OnCollectionChanged;
        _collections.Add(source);
    }

    /// <summary>これまでの購読の解除</summary>
    /// <remarks>観測対象を差し替える前に呼ぶこと。<see cref="Observe"/> は既存の購読を解除しない。</remarks>
    protected void Unobserve()
    {
        foreach (INotifyPropertyChanged source in _properties) source.PropertyChanged -= OnSourceChanged;
        foreach (INotifyCollectionChanged source in _collections) source.CollectionChanged -= OnCollectionChanged;
        _properties.Clear();
        _collections.Clear();
    }

    /// <summary>購読元が変わったときの再描画</summary>
    protected virtual void OnObservedChange(string? propertyName) => Rerender();

    /// <summary>描き直しの circuit のスケジューラへの受け渡し</summary>
    protected void Rerender()
    {
        if (_disposed) return;

        _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            StateHasChanged();
        });
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => OnObservedChange(e.PropertyName);

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnObservedChange(null);

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unobserve();

        GC.SuppressFinalize(this);
    }
}
