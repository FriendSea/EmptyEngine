using EmptyEngine.ObjectModel;
using Xunit;

namespace EmptyEngine.PlayerLoop.Tests;

/// <summary>レジストリ無しで作った <see cref="UpdatableComponent"/> の扱いの検証</summary>
public sealed class UpdatableComponentRegistryTests
{
    private sealed class Owner : IObject
    {
        public IObject? Parent => null;

        public T? GetAttachable<T>() where T : class, IAttachable => null;

        public IEnumerable<IAttachable> GetAllAttachables() => [];
    }

    [Fact]
    public void Putting_it_on_an_object_without_a_registry_throws()
    {
        CounterProbe component = new(null);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => component.OnCreated(new Owner()));

        Assert.Contains(typeof(CounterProbe).FullName!, error.Message);
    }

    [Fact]
    public void Tearing_down_one_that_never_started_does_not_throw()
    {
        CounterProbe component = new(null);

        component.OnDestroy(new Owner());
    }

}

public sealed class CounterProbe : UpdatableComponent
{
    public CounterProbe(DefaultPlayerLoopRegistry? registry) : base(registry) { }

    public int Ticks { get; private set; }

    protected override void Update(IObject owner, in UpdateContext context) => Ticks++;
}
