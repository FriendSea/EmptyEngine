using System.Runtime.CompilerServices;
using EmptyEngine.ObjectModel;
using EmptyEngine.World;
using MessagePack;
using Xunit;

namespace EmptyEngine.Serialization.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TypeSerializerCollection
{
    public const string Name = "TypeSerializers";
}

/// <summary>生成された手順書がシリアライザの各経路で使われることの検証</summary>
[Collection(TypeSerializerCollection.Name)]
public sealed class TypeSerializerRegistryTests
{
    private static readonly ProbeSerializer Serializer = Install();

    private static ProbeSerializer Install()
    {
        var serializer = new ProbeSerializer();
        TypeSerializers.Install(new Dictionary<string, ITypeSerializer>
        {
            [serializer.TypeName] = serializer,
            [RenamedProbeSerializer.WireName] = new RenamedProbeSerializer(),
        });
        return serializer;
    }

    [Fact]
    public void Blob_round_trip_goes_through_the_registered_serializer()
    {
        int writesBefore = Serializer.Writes;
        int readsBefore = Serializer.Reads;

        var component = new ProbeComponent { Value = 42 };
        byte[] blob = SceneBlob.Encode([Root(component)]);
        IReadOnlyList<RootInstanceData> roots = SceneBlob.Decode(blob);

        var restored = Assert.IsType<ProbeComponent>(roots[0].Root.Components[0].Component);
        Assert.Equal(42, restored.Value);
        Assert.Equal(writesBefore + 1, Serializer.Writes);
        Assert.Equal(readsBefore + 1, Serializer.Reads);

        Assert.Equal("parameterless", restored.ConstructedBy);
    }

    [Fact]
    public void Type_name_resolves_from_the_registry()
    {
        _ = Serializer;
        Assert.Equal(typeof(RenamedProbe), TypeSerializers.For(RenamedProbeSerializer.WireName).Type);
    }

    [Fact]
    public void A_type_id_cannot_be_registered_for_two_different_types()
    {
        _ = Serializer;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            TypeSerializers.Install(new Dictionary<string, ITypeSerializer>
            {
                [Serializer.TypeName] = new RenamedProbeSerializer(),
            }));

        Assert.Contains(Serializer.TypeName, error.Message);
    }

    [Fact]
    public void A_type_missing_from_the_installed_table_is_rejected()
    {
        const string missing = "EmptyEngine.Serialization.Tests.NeverGenerated";

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => TypeSerializers.For(missing));
        Assert.Contains(missing, error.Message);

        Assert.NotNull(TypeSerializers.For(typeof(ProbeComponent)));
    }

    private static RootInstanceData Root(IAttachable component)
        => new(
            "instance",
            new ObjectData("id", "probe", [new ComponentData(typeof(ProbeComponent).FullName!, component)], []));

    public sealed class ProbeDependency(string name)
    {
        public string Name { get; } = name;
    }

    /// <summary>手順書つきのコンポーネント</summary>
    public sealed class ProbeComponent : IAttachable
    {
        public ProbeComponent() => ConstructedBy = "parameterless";

        public ProbeComponent(ProbeDependency dependency) => ConstructedBy = dependency.Name;

        public int Value { get; set; }

        public string? ConstructedBy { get; }
    }

    /// <summary>ワイヤ上の名前が CLR 型名と一致しない型（型名解決が表を引くことの検証用）</summary>
    public sealed class RenamedProbe : IAttachable
    {
    }

    private sealed class ProbeSerializer : ITypeSerializer
    {
        public int Reads;
        public int Writes;

        public Type Type => typeof(ProbeComponent);

        public string TypeName => typeof(ProbeComponent).FullName!;

        public object CreateUninitialized() => RuntimeHelpers.GetUninitializedObject(typeof(ProbeComponent));

        public void Read(ref MessagePackReader reader, object instance, MessagePackSerializerOptions options)
        {
            Reads++;
            var probe = (ProbeComponent)instance;
            int count = reader.ReadMapHeader();
            for (int i = 0; i < count; i++)
            {
                if (reader.ReadString() == nameof(ProbeComponent.Value))
                    probe.Value = reader.ReadInt32();
                else
                    reader.Skip();
            }
        }

        public void Write(ref MessagePackWriter writer, object instance, MessagePackSerializerOptions options)
        {
            Writes++;
            writer.WriteMapHeader(1);
            writer.Write(nameof(ProbeComponent.Value));
            writer.Write(((ProbeComponent)instance).Value);
        }

        public object Construct(IServiceProvider services)
            => services.GetService(typeof(ProbeDependency)) is ProbeDependency dependency
                ? new ProbeComponent(dependency)
                : new ProbeComponent();

        public void CopyShallow(object destination, object source)
            => ((ProbeComponent)destination).Value = ((ProbeComponent)source).Value;

        public void CopyDeep(object destination, object source) => CopyShallow(destination, source);
    }

    private sealed class RenamedProbeSerializer : ITypeSerializer
    {
        public const string WireName = "EmptyEngine.Serialization.Tests.Renamed.Probe";

        public Type Type => typeof(RenamedProbe);

        public string TypeName => WireName;

        public object CreateUninitialized() => new RenamedProbe();

        public void Read(ref MessagePackReader reader, object instance, MessagePackSerializerOptions options)
            => reader.Skip();

        public void Write(ref MessagePackWriter writer, object instance, MessagePackSerializerOptions options)
            => writer.WriteMapHeader(0);

        public object Construct(IServiceProvider services) => new RenamedProbe();

        public void CopyShallow(object destination, object source)
        {
        }

        public void CopyDeep(object destination, object source)
        {
        }
    }
}
