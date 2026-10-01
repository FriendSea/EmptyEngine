using System.Text.RegularExpressions;

namespace EmptyEngine.Host;

/// <summary>子プロセスの行に混じる ANSI 制御列を落とす</summary>
internal static partial class HostLog
{
    /// <summary>ANSI の制御列を落とした素の文字列</summary>
    public static string StripAnsi(string text) =>
        text.Contains(Escape) ? AnsiEscape().Replace(text, string.Empty) : text;

    private const char Escape = '\u001b';

    [GeneratedRegex(@"\e(?:\[[0-9;?]*[ -/]*[@-~]|\][^\a\e]*(?:\a|\e\\)|[@-Z\\-_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();
}
