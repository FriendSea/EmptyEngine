using System.Buffers;
using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;
using EmptyEngine.Serialization.Editor;
using MessagePack;

namespace EmptyEngine.World.Editor;

/// <summary>エディタ側の blob ⇄ <see cref="HierarchyNode"/> 変換</summary>
/// <remarks>ワイヤ形式はランタイムの <see cref="SceneBlob"/> と互換性を持つ。</remarks>
internal static class AuthoringBlobCodec
{
    private static readonly HashSet<string> ReportedUnknownTypes = new(StringComparer.Ordinal);

    public static byte[] Encode(IReadOnlyList<HierarchyNode> roots)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteMapHeader(1);
        writer.Write("Roots");
        writer.WriteArrayHeader(roots.Count);
        foreach (HierarchyNode root in roots)
        {
            writer.WriteMapHeader(2);
            writer.Write("InstanceId");
            writer.Write(root.SceneId);
            writer.Write("Object");
            WriteObject(ref writer, root);
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static IReadOnlyList<HierarchyNode> Decode(ReadOnlyMemory<byte> blob, ISchemaSource schemas)
    {
        var reader = new MessagePackReader(blob);
        var roots = new List<HierarchyNode>();

        int rootCount = reader.ReadMapHeader();
        for (int i = 0; i < rootCount; i++)
        {
            if (reader.ReadString() == "Roots")
            {
                int count = reader.ReadArrayHeader();
                for (int s = 0; s < count; s++)
                    roots.Add(ReadRoot(ref reader, schemas));
            }
            else
            {
                reader.Skip();
            }
        }

        return roots;
    }

    private static void WriteObject(ref MessagePackWriter writer, HierarchyNode node)
    {
        writer.WriteMapHeader(5);
        writer.Write("Id");
        writer.Write(node.ObjectId);
        writer.Write("Name");
        writer.Write(node.Name);
        writer.Write("Active");
        writer.Write(node.Active);
        writer.Write("Components");
        writer.WriteArrayHeader(node.Components.Count);
        foreach (var component in node.Components)
            WriteComponent(ref writer, component);
        writer.Write("Children");
        writer.WriteArrayHeader(node.Children.Count);
        foreach (HierarchyNode child in node.Children)
            WriteObject(ref writer, child);
    }

    private static void WriteComponent(ref MessagePackWriter writer, AuthoringObject authoring)
    {
        writer.WriteMapHeader(2);
        writer.Write("TypeName");
        writer.Write(authoring.TypeName);
        writer.Write("Data");
        FieldValueCodec.Encode(ref writer, authoring.Data, authoring.Schema.Root);
    }

    private static HierarchyNode ReadRoot(ref MessagePackReader reader, ISchemaSource schemas)
    {
        string instanceId = string.Empty;
        HierarchyNode root = new(string.Empty, string.Empty);

        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "InstanceId":
                    instanceId = reader.ReadString() ?? string.Empty;
                    break;
                case "Object":
                    root = ReadObject(ref reader, schemas);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        root.SceneId = instanceId;
        return root;
    }

    private static HierarchyNode ReadObject(ref MessagePackReader reader, ISchemaSource schemas)
    {
        string id = string.Empty;
        string name = string.Empty;
        bool active = true;
        var components = new List<AuthoringObject>();
        var children = new List<HierarchyNode>();

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
                    int cc = reader.ReadArrayHeader();
                    for (int c = 0; c < cc; c++)
                        if (ReadComponent(ref reader, schemas) is { } component) components.Add(component);
                    break;
                case "Children":
                    int ch = reader.ReadArrayHeader();
                    for (int c = 0; c < ch; c++)
                        children.Add(ReadObject(ref reader, schemas));
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new HierarchyNode(id, name, children, components, active: active);
    }

    private static AuthoringObject? ReadComponent(ref MessagePackReader reader, ISchemaSource schemas)
    {
        string typeName = string.Empty;
        ReadOnlySequence<byte>? data = null;

        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "TypeName":
                    typeName = reader.ReadString() ?? string.Empty;
                    break;
                case "Data":
                    data = reader.ReadRaw();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (schemas.Find(typeName) is not { } schema)
        {
            ReportUnknownType(typeName);
            return null;
        }

        var dataReader = data is { } raw ? new MessagePackReader(raw) : default;
        return new AuthoringObject(schema, data.HasValue ? FieldValueCodec.Decode(ref dataReader, schema.Root) : new FieldValue());
    }

    /// <summary>カタログに無い型の 1 回だけの報告</summary>
    private static void ReportUnknownType(string typeName)
    {
        lock (ReportedUnknownTypes)
            if (!ReportedUnknownTypes.Add(typeName)) return;

        Console.Error.WriteLine(
            $"[AuthoringBlob] Skipping '{typeName}' returned by the runtime because it is not in the catalog"
            + " (the component is restored from the editor's authoritative tree)");
    }
}
