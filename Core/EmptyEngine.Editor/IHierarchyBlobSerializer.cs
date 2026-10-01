namespace EmptyEngine.Editor;

/// <summary>ランタイム blob と <see cref="HierarchyNode"/> 列の相互変換</summary>
public interface IHierarchyBlobSerializer
{
    byte[] Serialize(IReadOnlyList<HierarchyNode> roots);
    IReadOnlyList<HierarchyNode> Deserialize(byte[] sceneBlob);
}
