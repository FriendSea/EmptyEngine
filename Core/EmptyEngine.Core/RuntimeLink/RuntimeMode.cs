namespace EmptyEngine.Core.RuntimeLink;

/// <summary>エディタアタッチ状況を持つエンジン規約</summary>
public static class RuntimeMode
{
    /// <summary>エディタの接続先が設定されているか</summary>
    public static bool IsEditorAttached => EditorWebSocketEndpoint is not null;

    /// <summary>エディタ接続用のランタイム側 WebSocket URL（未設定なら <c>null</c>）</summary>
    public static Uri? EditorWebSocketEndpoint =>
        ParseEndpoint(AppContext.GetData(SceneWire.EditorWebSocketAppContextKey));

    internal static Uri? ParseEndpoint(object? value)
    {
        string? text = value as string;
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not ("ws" or "wss"))
            return null;

        return endpoint;
    }
}
