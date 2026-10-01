using EmptyEngine.Editor;
using EmptyEngine.Editor.Authoring;

namespace EmptyEngine.World.Editor;

/// <summary>ランタイム通信・アーティファクト blob と <see cref="HierarchyNode"/> の相互変換</summary>
public sealed class HierarchyBlobSerializer(ISchemaSource schemas) : IHierarchyBlobSerializer
{
    public byte[] Serialize(IReadOnlyList<HierarchyNode> roots) => AuthoringBlobCodec.Encode(roots);

    public IReadOnlyList<HierarchyNode> Deserialize(byte[] sceneBlob) =>
        AuthoringBlobCodec.Decode(sceneBlob, schemas);
}
