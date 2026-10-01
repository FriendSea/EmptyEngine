using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace EmptyEngine.Core.RuntimeLink;

/// <summary>エディタ↔ランタイムのメッセージ種別</summary>
public enum SceneMessageType : byte
{
    /// <summary>アセット再読み込み</summary>
    UpdateAssets = (int)SceneHandling.None,

    /// <summary>エディタ状態をランタイムに押し付け</summary>
    EditBlob = (int)SceneHandling.Retain,

    /// <summary>ランタイムの現在のシーン状態を要求する。</summary>
    /// <remarks>返信は任意。返信しない場合も編集を継続できるが、ランタイムの現在の状態は確認できない。</remarks>
    RequestScene = (int)(SceneHandling.Retain | SceneHandling.CancelOpposite),

    /// <summary><see cref="RequestScene"/> への返答</summary>
    SceneBlob = (int)SceneHandling.CancelOpposite,
}

/// <summary>線の 1 通を両端の語彙で読んだもの</summary>
public sealed record SceneMessage(SceneMessageType Type, byte[] Payload);

public static class SceneProtocol
{
    /// <summary>EditBlob の <c>[モード 1B][シーン blob]</c> を組む</summary>
    public static byte[] Frame(bool flag, byte[] body)
    {
        byte[] framed = new byte[body.Length + 1];
        framed[0] = flag ? (byte)1 : (byte)0;
        body.CopyTo(framed, 1);
        return framed;
    }

    /// <summary><see cref="Frame"/> を解く</summary>
    public static (bool Flag, byte[] Body)? Unframe(byte[] payload)
        => payload.Length >= 1 ? (payload[0] != 0, payload[1..]) : null;

    /// <summary>UpdateAssets の本体の組み立て</summary>
    public static byte[] FrameKeys(IReadOnlyCollection<string> keys)
    {
        var framed = new List<byte>(sizeof(int) + keys.Count * 48);
        byte[] number = new byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(number, keys.Count);
        framed.AddRange(number);

        foreach (string key in keys)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(key);
            BinaryPrimitives.WriteInt32LittleEndian(number, utf8.Length);
            framed.AddRange(number);
            framed.AddRange(utf8);
        }

        return framed.ToArray();
    }

    /// <summary><see cref="FrameKeys"/> を解く</summary>
    public static string[]? UnframeKeys(byte[] payload)
    {
        if (payload.Length < sizeof(int))
        {
            return null;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0)
        {
            return null;
        }

        var keys = new string[count];
        int offset = sizeof(int);
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - offset < sizeof(int))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            offset += sizeof(int);
            if (length < 0 || length > payload.Length - offset)
            {
                return null;
            }

            keys[i] = Encoding.UTF8.GetString(payload, offset, length);
            offset += length;
        }

        return keys;
    }

    /// <summary>1 通の書き込み</summary>
    public static Task WriteMessageAsync(
        WebSocket socket,
        SceneMessageType type,
        byte[]? payload,
        CancellationToken cancellationToken = default)
        => SceneWire.WriteFrameAsync(
            socket,
            SceneWire.Compose((SceneHandling)type, payload ?? Array.Empty<byte>()),
            cancellationToken);

    /// <summary>1 通の読み取り</summary>
    public static async Task<SceneMessage?> ReadMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        byte[]? frame = await SceneWire.ReadFrameAsync(socket, cancellationToken);
        return frame is null
            ? null
            : new SceneMessage((SceneMessageType)frame[0], frame[SceneWire.HeaderLength..]);
    }
}
