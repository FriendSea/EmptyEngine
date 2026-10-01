using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace EmptyEngine.Editor.Components;

/// <summary>エディタの外から来たドロップの受け取り口</summary>
internal static class FileDrop
{
    /// <summary>外部からのドラッグを示す DataTransfer の種別。</summary>
    public static readonly string[] ExternalTypes = ["files", "text/uri-list", "resourceurls", "codefiles"];

    /// <summary>このドラッグがエディタの外から来たものか（＝ヒエラルキー内の並べ替えではないか）</summary>
    public static bool IsExternal(DragEventArgs e)
    {
        string[]? types = e.DataTransfer?.Types;
        if (types is null) return false;

        foreach (string type in types)
        {
            if (Array.Exists(ExternalTypes, known => string.Equals(known, type, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>直前の drop で控えられた指し先の取り出し</summary>
    public static async Task<string[]> TakeAsync(IJSRuntime js)
    {
        try
        {
            return await js.InvokeAsync<string[]>("emptyEngineTakeDrop");
        }
        catch (JSException)
        {
            return [];
        }
        catch (JSDisconnectedException)
        {
            return [];
        }
    }
}
