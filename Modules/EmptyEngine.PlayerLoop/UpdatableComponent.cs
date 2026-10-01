using EmptyEngine.ObjectModel;

namespace EmptyEngine.PlayerLoop;

/// <summary>毎フレーム <see cref="Update"/> が呼ばれるコンポーネントの基底</summary>
/// <remarks>駆動するレジストリをコンストラクタに渡すこと。<c>null</c> でも構築できるが、<see cref="OnCreated"/> は例外を投げる。</remarks>
public abstract class UpdatableComponent : ILifecycleAttachable
{
    private readonly PlayerLoopRegistryBase? _registry;
    private IObject? _owner;

    protected UpdatableComponent(PlayerLoopRegistryBase? registry)
    {
        _registry = registry;
    }

    /// <summary>このコンポーネントを駆動するレジストリ</summary>
    /// <exception cref="InvalidOperationException">ctor がレジストリを受け取っていない</exception>
    protected PlayerLoopRegistryBase Registry =>
        _registry ?? throw new InvalidOperationException(
            $"'{GetType().FullName}' was constructed without a player loop registry, so it cannot run. "
            + "Take a concrete player loop registry in the constructor and pass it to the base constructor.");

    public virtual void OnCreated(IObject owner)
    {
        _owner = owner;
        Registry.Register(this);
    }

    public virtual void OnDeserialized(IObject owner)
    {
    }

    public virtual void OnDestroy(IObject owner)
    {
        _owner = null;
        _registry?.Unregister(this);
    }

    internal void Tick(in UpdateContext context)
    {
        if (_owner is null)
        {
            return;
        }

        Update(_owner, context);
    }

    protected abstract void Update(IObject owner, in UpdateContext context);
}
