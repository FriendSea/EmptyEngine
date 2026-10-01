using System.Globalization;
using System.Text.RegularExpressions;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>WGSL ソースからのユーザ定義パラメータの解析</summary>
internal static class WgslShaderParams
{
    private static readonly Regex ParamBindingRegex = new(
        @"@group\s*\(\s*0\s*\)\s*@binding\s*\(\s*3\s*\)\s*var\s*<\s*uniform\s*>\s*\w+\s*:\s*(?<struct>\w+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex GlobalBindingRegex = new(
        @"@group\s*\(\s*1\s*\)\s*@binding\s*\(\s*0\s*\)\s*var\s*<\s*uniform\s*>\s*\w+\s*:\s*(?<struct>\w+)\s*;",
        RegexOptions.Compiled);

    private static Regex StructBodyRegex(string structName) => new(
        @"struct\s+" + Regex.Escape(structName) + @"\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex ParamFieldRegex = new(
        @"(?<name>\w+)\s*:\s*(?<type>f32|vec4\s*<\s*f32\s*>)",
        RegexOptions.Compiled);

    private static readonly Regex DefaultRegex = new(
        @"default\s*[:=]?\s*(?<value>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex NumberRegex = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled);

    private static readonly Regex HexColorRegex = new(
        @"default\s*[:=]?\s*#(?<hex>[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>WGSL ソースからのユーザ定義パラメータ列の解析</summary>
    public static ShaderParam[] Parse(string wgsl) => ParseUniformStruct(wgsl, ParamBindingRegex);

    /// <summary>WGSL ソースからの、世界で共有する値の宣言の解析</summary>
    public static ShaderParam[] ParseGlobals(string wgsl) => ParseUniformStruct(wgsl, GlobalBindingRegex);

    /// <summary>指定 binding の uniform 構造体のメンバ列の解析</summary>
    private static ShaderParam[] ParseUniformStruct(string wgsl, Regex bindingRegex)
    {
        string source = StripBlockComments(wgsl);
        Match binding = bindingRegex.Match(source);
        if (!binding.Success) return [];

        string structName = binding.Groups["struct"].Value;
        Match body = StructBodyRegex(structName).Match(source);
        if (!body.Success) return [];

        var result = new List<ShaderParam>();
        foreach (string rawLine in body.Groups["body"].Value.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            int comment = line.IndexOf("//", StringComparison.Ordinal);
            string code = comment >= 0 ? line[..comment] : line;
            string note = comment >= 0 ? line[(comment + 2)..] : string.Empty;

            Match field = ParamFieldRegex.Match(code);
            if (!field.Success) continue;

            string name = field.Groups["name"].Value;
            string type = Regex.Replace(field.Groups["type"].Value, @"\s", string.Empty);
            if (type == "f32")
            {
                float def = 0f;
                Match defaultMatch = DefaultRegex.Match(note);
                if (defaultMatch.Success)
                    float.TryParse(defaultMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out def);

                result.Add(new ShaderParam { Name = name, Default = def });
                continue;
            }

            (float r, float g, float b, float a) = ParseColorDefault(note);
            result.Add(new ShaderParam
            {
                Name = name,
                Kind = ShaderParamKind.Color,
                Default = r,
                DefaultG = g,
                DefaultB = b,
                DefaultA = a,
            });
        }

        return result.ToArray();
    }

    private static (float R, float G, float B, float A) ParseColorDefault(string note)
    {
        Match hex = HexColorRegex.Match(note);
        if (hex.Success)
        {
            string value = hex.Groups["hex"].Value;
            return (
                Convert.ToByte(value[..2], 16) / 255f,
                Convert.ToByte(value.Substring(2, 2), 16) / 255f,
                Convert.ToByte(value.Substring(4, 2), 16) / 255f,
                value.Length == 8 ? Convert.ToByte(value.Substring(6, 2), 16) / 255f : 1f);
        }

        int defaultIndex = note.IndexOf("default", StringComparison.OrdinalIgnoreCase);
        string defaults = defaultIndex >= 0 ? note[(defaultIndex + "default".Length)..] : string.Empty;
        defaults = Regex.Replace(defaults, @"vec4\s*<\s*f32\s*>", string.Empty, RegexOptions.IgnoreCase);
        float[] values = NumberRegex.Matches(defaults)
            .Select(match => float.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture))
            .Take(4)
            .ToArray();

        return (
            values.ElementAtOrDefault(0),
            values.ElementAtOrDefault(1),
            values.ElementAtOrDefault(2),
            values.Length >= 4 ? values[3] : 1f);
    }

    private static readonly Regex TextureBindingRegex = new(
        @"@group\s*\(\s*0\s*\)\s*@binding\s*\(\s*(?<binding>\d+)\s*\)\s*var\s+(?<name>\w+)\s*:\s*texture_2d\s*<\s*f32\s*>\s*;",
        RegexOptions.Compiled);

    /// <summary>WGSL ソースからの追加テクスチャスロットの解析</summary>
    public static ShaderTextureSlot[] ParseTextureSlots(string wgsl)
    {
        string stripped = Regex.Replace(StripBlockComments(wgsl), @"//[^\n]*", " ");

        var slots = new List<ShaderTextureSlot>();
        foreach (Match match in TextureBindingRegex.Matches(stripped))
        {
            int binding = int.Parse(match.Groups["binding"].Value, CultureInfo.InvariantCulture);
            if (binding is 0 or 2 or 3) continue;
            slots.Add(new ShaderTextureSlot { Name = match.Groups["name"].Value, Binding = binding });
        }

        return slots.OrderBy(s => s.Binding).ToArray();
    }

    private static string StripBlockComments(string wgsl)
        => Regex.Replace(wgsl, @"/\*.*?\*/", " ", RegexOptions.Singleline);
}
