using System.Globalization;

namespace EmptyEngine.Editor.ViewModels.Inspector;

/// <summary>インスペクタが扱う色（各チャンネル 0..255）</summary>
public readonly record struct EditorColor(byte R, byte G, byte B, byte A)
{
    public static EditorColor FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

    /// <summary>HTML の色入力が読み書きする形（<c>#rrggbb</c>）</summary>
    public string ToHexRgb() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary><c>#rgb</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c> の読み取り</summary>
    public static bool TryParseHex(string? text, byte fallbackAlpha, out EditorColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];

        switch (span.Length)
        {
            case 3:
                if (!TryNibble(span[0], out byte r3) || !TryNibble(span[1], out byte g3) || !TryNibble(span[2], out byte b3))
                    return false;
                color = new EditorColor((byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17), fallbackAlpha);
                return true;

            case 6:
            case 8:
                if (!TryByte(span[..2], out byte r) || !TryByte(span[2..4], out byte g) || !TryByte(span[4..6], out byte b))
                    return false;
                byte a = fallbackAlpha;
                if (span.Length == 8 && !TryByte(span[6..8], out a)) return false;
                color = new EditorColor(r, g, b, a);
                return true;

            default:
                return false;
        }
    }

    private static bool TryByte(ReadOnlySpan<char> span, out byte value) =>
        byte.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool TryNibble(char c, out byte value)
    {
        Span<char> one = stackalloc char[1] { c };
        return byte.TryParse(one, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
