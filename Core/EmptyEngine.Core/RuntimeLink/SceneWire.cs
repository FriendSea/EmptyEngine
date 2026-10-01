using System.Buffers.Binary;
using System.Net.WebSockets;

namespace EmptyEngine.Core.RuntimeLink;

/// <summary>中継（Host のハブ）が 1 通に対してすることの内訳</summary>
[Flags]
#if EMPTYENGINE_HOST
internal enum SceneHandling : byte
#else
public enum SceneHandling : byte
#endif
{
    /// <summary>何も預からず反対側へ流すだけの線</summary>
    None = 0,

    /// <summary>送信元ごとに最新の 1 通を預かり反対側の接続時に押し込む線</summary>
    Retain = 1 << 0,

    /// <summary>逆方向の同フラグ付き保持フレームを破棄する</summary>
    /// <remarks><see cref="Retain"/> と併用すると、逆方向の同フラグ付きフレームを受け取るか送信元が切り替わるまで保持する。再送時は通常の保持フレームより後に送る。</remarks>
    CancelOpposite = 1 << 1,
}

/// <summary>1 通単位の送受信ユーティリティ</summary>
/// <remarks>1 フレームは <c>[扱い 1B][長さ i32 LE][本体]</c> の形式で、WebSocket のバイナリメッセージ 1 個に対応する。宛先は受信したソケットの反対側で、フレームには含めない。</remarks>
#if EMPTYENGINE_HOST
internal static class SceneWire
#else
public static class SceneWire
#endif
{
    /// <summary>ハブの接続先をランタイムへ渡す名前</summary>
    /// <remarks>接続先 URL を指定する環境変数および MSBuild プロパティの名前。C# ランタイムでの取得には <see cref="EditorWebSocketAppContextKey"/> を使う。</remarks>
    public const string EditorWebSocketProperty = "EmptyEngineEditorWebSocketUrl";

    /// <summary>runtimeconfig から接続先を読む AppContext キー</summary>
    public const string EditorWebSocketAppContextKey = "EmptyEngine.EditorWebSocketUrl";

    /// <summary>接続先が名乗られないときの既定のホスト</summary>
    public const string DefaultHubHost = "127.0.0.1";

    /// <summary>エディタもランタイムも繋ぎに行く WebSocket ハブの既定ポート</summary>
    /// <remarks>どちらが繋いだかはポートではなくパスで分ける。</remarks>
    public const int DefaultHubWebSocketPort = 5003;

    /// <summary>runtime WebSocket endpoint のパス</summary>
    public const string RuntimeWebSocketPath = "/runtime";

    /// <summary>editor WebSocket endpoint のパス</summary>
    public const string EditorWebSocketPath = "/editor";

    /// <summary>頭 <c>[扱い 1B][長さ i32 LE]</c> の長さ</summary>
    public const int HeaderLength = 5;

    /// <summary>1 フレームに含められるペイロードの最大バイト数</summary>
    /// <remarks>メッセージ内容には依存しない transport 上の制約。</remarks>
    public const int MaxPayloadLength = 256 * 1024 * 1024;

    /// <summary><paramref name="flag"/> が立っているか</summary>
    public static bool Has(this SceneHandling handling, SceneHandling flag) =>
        (handling & flag) == flag;

    /// <summary>WebSocket の binary message 1 個から 1 フレームを読む</summary>
    public static async Task<byte[]?> ReadFrameAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        byte[] chunk = new byte[16 * 1024];
        using var frame = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (frame.Length == 0) return null;
                throw new EndOfStreamException("WebSocket closed before the frame message was complete.");
            }

            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidDataException("Scene link accepts binary WebSocket messages only.");

            frame.Write(chunk, 0, result.Count);
            if (frame.Length > HeaderLength + MaxPayloadLength)
                throw new InvalidDataException($"Frame exceeds the {MaxPayloadLength}-byte payload limit.");

            if (!result.EndOfMessage) continue;

            byte[] bytes = frame.ToArray();
            ValidateFrame(bytes);
            return bytes;
        }
    }

    /// <summary>1 フレームを 1 個の binary WebSocket message として書く</summary>
    public static Task WriteFrameAsync(
        WebSocket socket,
        byte[] frame,
        CancellationToken cancellationToken = default)
    {
        ValidateFrame(frame);
        return socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    /// <summary>指定した扱いとペイロードを持つフレームを作成する</summary>
    public static byte[] Compose(SceneHandling type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                $"Frame payloads are limited to {MaxPayloadLength} bytes.");

        byte[] frame = new byte[HeaderLength + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>フレームのヘッダーと実データ長が一致することを検証する</summary>
    public static void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength)
            throw new InvalidDataException($"Frame is shorter than the {HeaderLength}-byte header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(frame[1..]);
        if (length < 0 || length > MaxPayloadLength)
            throw new InvalidDataException($"Frame payload length {length} is outside 0..{MaxPayloadLength} bytes.");

        if (frame.Length != HeaderLength + length)
            throw new InvalidDataException($"Frame declared {length} payload bytes but contained {frame.Length - HeaderLength}.");
    }
}
