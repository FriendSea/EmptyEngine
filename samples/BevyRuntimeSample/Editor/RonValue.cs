using System.Globalization;
using System.Text;

namespace BevyRuntimeSample.Editor;

/// <summary>型情報を保持したまま RON の値を扱う可変ツリー</summary>
public abstract class RonValue
{
}

public enum RonScalarKind
{
    Number,
    Bool,
    String,
    /// <summary>裸の識別子</summary>
    Ident,
}

/// <summary>数値・真偽値・文字列・裸識別子の葉</summary>
public sealed class RonScalar : RonValue
{
    public RonScalarKind Kind;
    public double Number;
    public bool Bool;
    public string Text = "";
    public string RawText = "";

    public void SetNumber(double value)
    {
        Kind = RonScalarKind.Number;
        Number = value;
        RawText = FormatFloat(value);
    }

    public void SetBool(bool value)
    {
        Kind = RonScalarKind.Bool;
        Bool = value;
        RawText = value ? "true" : "false";
    }

    public void SetString(string value)
    {
        Kind = RonScalarKind.String;
        Text = value;
        RawText = RonText.QuoteAndEscape(value);
    }

    public void SetIdent(string value)
    {
        Kind = RonScalarKind.Ident;
        Text = value;
        RawText = value;
    }

    private static string FormatFloat(double value)
    {
        string s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.IndexOfAny(['.', 'e', 'E']) < 0 ? s + ".0" : s;
    }
}

/// <summary>struct フィールド 1 個または位置引数 1 個</summary>
public sealed class RonField
{
    public string? Key;
    public RonValue Value = null!;
}

/// <summary>struct と tuple/enum バリアント呼び出しの両方を表す値</summary>
public sealed class RonCompound : RonValue
{
    public string? Name;
    public List<RonField> Fields = new();
}

public sealed class RonMapEntry
{
    public RonValue Key = null!;
    public RonValue Value = null!;
}

/// <summary><c>{ k: v, ... }</c> のマップ</summary>
public sealed class RonMap : RonValue
{
    public List<RonMapEntry> Entries = new();
}

/// <summary><c>[a, b, c]</c> の配列</summary>
public sealed class RonSeq : RonValue
{
    public List<RonValue> Items = new();
}

/// <summary>RON テキスト ⇔ <see cref="RonValue"/> ツリーの変換</summary>
public static class RonText
{
    public static RonValue Parse(string text) => new Parser(text).ParseValue();

    public static string Write(RonValue value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value);
        return sb.ToString();
    }

    public static string QuoteAndEscape(string text)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, RonValue value)
    {
        switch (value)
        {
            case RonScalar scalar:
                sb.Append(scalar.Kind == RonScalarKind.String ? QuoteAndEscape(scalar.Text) : scalar.RawText);
                break;

            case RonCompound compound:
                if (compound.Name is { } name) sb.Append(name);
                sb.Append('(');
                for (int i = 0; i < compound.Fields.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RonField field = compound.Fields[i];
                    if (field.Key is { } key) { sb.Append(key); sb.Append(':'); }
                    WriteValue(sb, field.Value);
                }
                sb.Append(')');
                break;

            case RonMap map:
                sb.Append('{');
                for (int i = 0; i < map.Entries.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, map.Entries[i].Key);
                    sb.Append(':');
                    WriteValue(sb, map.Entries[i].Value);
                }
                sb.Append('}');
                break;

            case RonSeq seq:
                sb.Append('[');
                for (int i = 0; i < seq.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, seq.Items[i]);
                }
                sb.Append(']');
                break;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public RonValue ParseValue()
        {
            SkipWs();
            char c = Peek();
            if (c == '(') return ParseCompoundBody(null);
            if (c == '{') return ParseMap();
            if (c == '[') return ParseSeq();
            if (c == '"') return new RonScalar { Kind = RonScalarKind.String, Text = ParseQuotedString() };
            if (c == '-' || c == '+' || char.IsDigit(c)) return ParseNumber();

            string ident = ParseIdent();
            if (ident.Length == 0)
                throw new FormatException($"Unexpected character '{c}' at position {_pos} in RON text.");

            SkipWs();
            if (Peek() == '(') return ParseCompoundBody(ident);
            if (ident == "true") return new RonScalar { Kind = RonScalarKind.Bool, Bool = true, RawText = "true" };
            if (ident == "false") return new RonScalar { Kind = RonScalarKind.Bool, Bool = false, RawText = "false" };
            return new RonScalar { Kind = RonScalarKind.Ident, Text = ident, RawText = ident };
        }

        private RonCompound ParseCompoundBody(string? name)
        {
            Expect('(');
            var compound = new RonCompound { Name = name };
            SkipWs();
            if (Peek() == ')') { _pos++; return compound; }

            while (true)
            {
                SkipWs();
                string? key = TryParseFieldKey();
                RonValue value = ParseValue();
                compound.Fields.Add(new RonField { Key = key, Value = value });
                SkipWs();
                if (Peek() == ',')
                {
                    _pos++;
                    SkipWs();
                    if (Peek() == ')') { _pos++; break; }
                    continue;
                }
                if (Peek() == ')') { _pos++; break; }
                throw new FormatException($"Expected ',' or ')' at position {_pos} in RON text.");
            }

            return compound;
        }

        private string? TryParseFieldKey()
        {
            int save = _pos;
            char c = Peek();
            if (!(char.IsLetter(c) || c == '_')) return null;

            string ident = ParseIdent();
            SkipWs();
            if (Peek() == ':') { _pos++; return ident; }

            _pos = save;
            return null;
        }

        private RonMap ParseMap()
        {
            Expect('{');
            var map = new RonMap();
            SkipWs();
            if (Peek() == '}') { _pos++; return map; }

            while (true)
            {
                SkipWs();
                RonValue key = ParseValue();
                SkipWs();
                Expect(':');
                RonValue value = ParseValue();
                map.Entries.Add(new RonMapEntry { Key = key, Value = value });
                SkipWs();
                if (Peek() == ',')
                {
                    _pos++;
                    SkipWs();
                    if (Peek() == '}') { _pos++; break; }
                    continue;
                }
                if (Peek() == '}') { _pos++; break; }
                throw new FormatException($"Expected ',' or '}}' at position {_pos} in RON text.");
            }

            return map;
        }

        private RonSeq ParseSeq()
        {
            Expect('[');
            var seq = new RonSeq();
            SkipWs();
            if (Peek() == ']') { _pos++; return seq; }

            while (true)
            {
                SkipWs();
                seq.Items.Add(ParseValue());
                SkipWs();
                if (Peek() == ',')
                {
                    _pos++;
                    SkipWs();
                    if (Peek() == ']') { _pos++; break; }
                    continue;
                }
                if (Peek() == ']') { _pos++; break; }
                throw new FormatException($"Expected ',' or ']' at position {_pos} in RON text.");
            }

            return seq;
        }

        private string ParseQuotedString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= text.Length) throw new FormatException("Unterminated string in RON text.");
                char c = text[_pos++];
                if (c == '"') break;
                if (c == '\\' && _pos < text.Length)
                {
                    char esc = text[_pos++];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => esc,
                    });
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private RonScalar ParseNumber()
        {
            int start = _pos;
            if (Peek() is '-' or '+') _pos++;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
            if (_pos < text.Length && (text[_pos] is 'e' or 'E'))
            {
                _pos++;
                if (_pos < text.Length && (text[_pos] is '-' or '+')) _pos++;
                while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
            }

            string raw = text[start.._pos];
            double value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new RonScalar { Kind = RonScalarKind.Number, Number = value, RawText = raw };
        }

        private string ParseIdent()
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            return text[start.._pos];
        }

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private void Expect(char c)
        {
            if (Peek() != c) throw new FormatException($"Expected '{c}' at position {_pos} in RON text.");
            _pos++;
        }

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }
    }
}
