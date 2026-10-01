using System.Buffers;
using EmptyEngine.Serialization;
using EmptyEngine.ObjectModel;
using MessagePack;

namespace EmptyEngine.World;

/// <summary>オブジェクトツリー blob の MessagePack エンコード／デコード</summary>
/// <remarks><c>InstanceId</c> はロード単位のインスタンス識別子、<c>Object</c> のルート <c>Id</c> は安定ローカル Id で、この 2 つは別物</remarks>
public static class SceneBlob
{
    private static readonly MessagePackSerializerOptions Options = TypeSerializers.Options;

    public static byte[] Encode(IReadOnlyList<RootInstanceData> roots)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteMapHeader(1);
        writer.Write("Roots");
        writer.WriteArrayHeader(roots.Count);
        foreach (RootInstanceData root in roots)
            WriteRoot(ref writer, root);

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>封筒のデコードとルート列の取得</summary>
    /// <param name="log">解決できない型名を報告する先。復号は止めず、その 1 つを飛ばして続ける</param>
    /// <param name="services">コンポーネントの ctor 引数を解決するコンテナ。blob にキーの無いメンバは ctor の値のまま残る</param>
    /// <remarks>手順書も型も見つからないコンポーネントは <see cref="ComponentData.Component"/> が <c>null</c> になる（型名は残る）</remarks>
    public static IReadOnlyList<RootInstanceData> Decode(
        ReadOnlyMemory<byte> blob, Action<string>? log = null, IServiceProvider? services = null)
    {
        var reader = new MessagePackReader(blob);
        var roots = new List<RootInstanceData>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);

        int rootCount = reader.ReadMapHeader();
        for (int i = 0; i < rootCount; i++)
        {
            switch (reader.ReadString())
            {
                case "Roots":
                    int count = reader.ReadArrayHeader();
                    for (int s = 0; s < count; s++)
                        roots.Add(ReadRoot(ref reader, unresolved, services));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (unresolved.Count > 0)
            log?.Invoke(
                $"[scene] skipped {unresolved.Count} component(s) of unknown type: {string.Join(", ", unresolved)}. " +
                "No generated serializer is registered for these type ids.");

        return roots;
    }

    /// <summary>複数の blob のルートの 1 本への束ねと、各ルートの <c>InstanceId</c> の振り直し</summary>
    /// <remarks>オブジェクトはバイト列のまま写す＝復元を通さないので、blob に無いキーは無いまま残る</remarks>
    internal static byte[] MergeRoots(IReadOnlyList<ReadOnlyMemory<byte>> blobs, Func<string> newInstanceId)
    {
        var objects = new List<ReadOnlySequence<byte>?>();
        foreach (ReadOnlyMemory<byte> blob in blobs)
        {
            var reader = new MessagePackReader(blob);
            int entries = reader.ReadMapHeader();
            for (int i = 0; i < entries; i++)
            {
                if (reader.ReadString() != "Roots")
                {
                    reader.Skip();
                    continue;
                }

                int count = reader.ReadArrayHeader();
                for (int r = 0; r < count; r++)
                    objects.Add(ReadRootObject(ref reader));
            }
        }

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(1);
        writer.Write("Roots");
        writer.WriteArrayHeader(objects.Count);
        foreach (ReadOnlySequence<byte>? obj in objects)
        {
            writer.WriteMapHeader(2);
            writer.Write("InstanceId");
            writer.Write(newInstanceId());
            writer.Write("Object");
            if (obj is { } raw)
                writer.WriteRaw(raw);
            else
                WriteObject(ref writer, new ObjectData(string.Empty, string.Empty, [], []));
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static ReadOnlySequence<byte>? ReadRootObject(ref MessagePackReader reader)
    {
        ReadOnlySequence<byte>? obj = null;
        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            if (reader.ReadString() == "Object")
                obj = reader.ReadRaw();
            else
                reader.Skip();
        }

        return obj;
    }

    private static void WriteRoot(ref MessagePackWriter writer, RootInstanceData root)
    {
        writer.WriteMapHeader(2);
        writer.Write("InstanceId");
        writer.Write(root.InstanceId);
        writer.Write("Object");
        WriteObject(ref writer, root.Root);
    }

    private static RootInstanceData ReadRoot(ref MessagePackReader reader, ISet<string> unresolved, IServiceProvider? services)
    {
        string instanceId = string.Empty;
        ObjectData root = new(string.Empty, string.Empty, Array.Empty<ComponentData>(), Array.Empty<ObjectData>());

        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "InstanceId":
                    instanceId = reader.ReadString() ?? string.Empty;
                    break;
                case "Object":
                    root = ReadObject(ref reader, unresolved, services);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new RootInstanceData(instanceId, root);
    }

    private static void WriteObjects(ref MessagePackWriter writer, IReadOnlyList<ObjectData> objects)
    {
        writer.WriteArrayHeader(objects.Count);
        foreach (ObjectData obj in objects)
            WriteObject(ref writer, obj);
    }

    private static void WriteObject(ref MessagePackWriter writer, ObjectData obj)
    {
        writer.WriteMapHeader(5);
        writer.Write("Id");
        writer.Write(obj.Id);
        writer.Write("Name");
        writer.Write(obj.Name);
        writer.Write("Active");
        writer.Write(obj.Active);
        writer.Write("Components");
        writer.WriteArrayHeader(obj.Components.Count);
        foreach (ComponentData component in obj.Components)
            WriteComponent(ref writer, component);
        writer.Write("Children");
        WriteObjects(ref writer, obj.Children);
    }

    private static void WriteComponent(ref MessagePackWriter writer, ComponentData component)
    {
        writer.WriteMapHeader(2);
        writer.Write("TypeName");
        writer.Write(component.TypeName);
        writer.Write("Data");
        if (component.Component is null)
        {
            writer.WriteNil();
        }
        else
        {
            TypeSerializers.For(component.Component.GetType()).Write(ref writer, component.Component, Options);
        }
    }

    private static List<ObjectData> ReadObjects(ref MessagePackReader reader, ISet<string> unresolved, IServiceProvider? services)
    {
        int count = reader.ReadArrayHeader();
        var list = new List<ObjectData>(count);
        for (int i = 0; i < count; i++)
            list.Add(ReadObject(ref reader, unresolved, services));
        return list;
    }

    private static ObjectData ReadObject(ref MessagePackReader reader, ISet<string> unresolved, IServiceProvider? services)
    {
        string id = string.Empty;
        string name = string.Empty;
        bool active = true;
        var components = new List<ComponentData>();
        var children = new List<ObjectData>();

        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "Id":
                    id = reader.ReadString() ?? string.Empty;
                    break;
                case "Name":
                    name = reader.ReadString() ?? string.Empty;
                    break;
                case "Active":
                    active = reader.ReadBoolean();
                    break;
                case "Components":
                    components = ReadComponents(ref reader, unresolved, services);
                    break;
                case "Children":
                    children = ReadObjects(ref reader, unresolved, services);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new ObjectData(id, name, components, children, active);
    }

    private static List<ComponentData> ReadComponents(ref MessagePackReader reader, ISet<string> unresolved, IServiceProvider? services)
    {
        int count = reader.ReadArrayHeader();
        var list = new List<ComponentData>(count);
        for (int i = 0; i < count; i++)
            list.Add(ReadComponent(ref reader, unresolved, services));
        return list;
    }

    private static ComponentData ReadComponent(ref MessagePackReader reader, ISet<string> unresolved, IServiceProvider? services)
    {
        string typeName = string.Empty;
        object? component = null;

        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "TypeName":
                    typeName = reader.ReadString() ?? string.Empty;
                    break;
                case "Data":
                    if (reader.TryReadNil())
                    {
                        component = null;
                    }
                    else if (TypeSerializers.Lookup(typeName) is { } serializer)
                    {
                        object instance = InstanceActivator.Construct(serializer, services);
                        serializer.Read(ref reader, instance, Options);
                        component = instance;
                    }
                    else
                    {
                        unresolved.Add(typeName);
                        reader.Skip();
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new ComponentData(typeName, component as IAttachable);
    }
}

/// <summary>世界のルートに直置きされた 1 プレハブ分のデータ</summary>
public sealed record RootInstanceData(string InstanceId, ObjectData Root);

public sealed record ObjectData(
    string Id,
    string Name,
    IReadOnlyList<ComponentData> Components,
    IReadOnlyList<ObjectData> Children,
    bool Active = true);

/// <summary>コンポーネント 1 つ分のワイヤ表現</summary>
public sealed record ComponentData(string TypeName, IAttachable? Component);
