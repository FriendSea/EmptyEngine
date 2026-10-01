using System.Text;

namespace EmptyEngine.Editor.State;

/// <summary>ロード中の全シーンの 1 時点</summary>
/// <remarks>undo の 1 段の中身。アセットインスペクタでの編集は含まれず、undo の対象にならない。</remarks>
public sealed record SceneSnapshot(
    byte[] Blob,
    IReadOnlyDictionary<string, string> LoadedSceneKeys,
    IReadOnlyCollection<string> DirtySceneIds)
{
    private const byte Version = 1;

    /// <summary>1 段のバイト表現</summary>
    internal byte[] Encode()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);

            writer.Write(Blob.Length);
            writer.Write(Blob);

            writer.Write(LoadedSceneKeys.Count);
            foreach (KeyValuePair<string, string> pair in LoadedSceneKeys)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }

            writer.Write(DirtySceneIds.Count);
            foreach (string id in DirtySceneIds)
                writer.Write(id);
        }

        return buffer.ToArray();
    }

    /// <summary>バイト表現からの 1 段の復元</summary>
    internal static SceneSnapshot? TryDecode(byte[] payload)
    {
        try
        {
            using var buffer = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8);

            if (reader.ReadByte() != Version) return null;

            int blobLength = reader.ReadInt32();
            if (blobLength < 0) return null;
            byte[] blob = reader.ReadBytes(blobLength);
            if (blob.Length != blobLength) return null;

            int keyCount = reader.ReadInt32();
            var loadedSceneKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < keyCount; i++)
            {
                string sceneId = reader.ReadString();
                loadedSceneKeys[sceneId] = reader.ReadString();
            }

            int dirtyCount = reader.ReadInt32();
            var dirtySceneIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < dirtyCount; i++)
                dirtySceneIds.Add(reader.ReadString());

            return new SceneSnapshot(blob, loadedSceneKeys, dirtySceneIds);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OutOfMemoryException)
        {
            return null;
        }
    }
}
