using Xunit;

namespace EmptyEngine.World.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TypeSerializerCollection
{
    public const string Name = "TypeSerializers";
}
