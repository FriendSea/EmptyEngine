using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>ソースファイルの OS のファイラでの表示</summary>
internal static class SourceFileBrowser
{
    public static void OpenInEditor(string fullPath, int line, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Open source failed for '{Path}:{Line}': {Error}", fullPath, line, ex.Message);
        }
    }

    public static void Reveal(string fullPath, ILogger logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Start("explorer.exe", $"/select,\"{fullPath}\"");
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Start("open", $"-R \"{fullPath}\"");
                return;
            }

            if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
                Start("xdg-open", $"\"{directory}\"");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Reveal failed for '{Path}': {Error}", fullPath, ex.Message);
        }
    }

    private static void Start(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false });
}
