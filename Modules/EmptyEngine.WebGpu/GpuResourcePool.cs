namespace EmptyEngine.WebGpu;

/// <summary>コンポーネントが確保した wgpu ハンドルの持ち主別追跡</summary>
/// <remarks><see cref="ReleaseAll"/> は指定した所有者の資源を解放し、<see cref="Dispose"/> は残っているすべての資源を解放する。</remarks>
public sealed class GpuResourcePool : IDisposable
{
    private readonly Dictionary<object, List<Action>> _byOwner = new(ReferenceEqualityComparer.Instance);

    /// <summary>確保した 1 ハンドル分の解放手続きの登録</summary>
    public void Track(object owner, Action release)
    {
        if (!_byOwner.TryGetValue(owner, out List<Action>? list))
            _byOwner[owner] = list = new List<Action>();
        list.Add(release);
    }

    /// <summary>指定した持ち主が確保した分だけの解放（再確保前の作り直しにも使う）</summary>
    public void ReleaseAll(object owner)
    {
        if (!_byOwner.Remove(owner, out List<Action>? list))
            return;

        foreach (Action release in list)
            release();
    }

    /// <summary>取りこぼされた分すべての解放</summary>
    public void Dispose()
    {
        foreach (List<Action> list in _byOwner.Values)
            foreach (Action release in list)
                release();

        _byOwner.Clear();
    }
}
