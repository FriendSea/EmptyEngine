using System.Globalization;
using System.Text.RegularExpressions;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>WGSL ソースからの、シェーダが宣言するインターフェースの解析</summary>
internal static class WgslShaderParams
{
    private static readonly Regex VertexParamBindingRegex = new(
        @"@group\s*\(\s*0\s*\)\s*@binding\s*\(\s*3\s*\)\s*var\s*<\s*uniform\s*>\s*\w+\s*:\s*(?<struct>\w+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex FragmentParamBindingRegex = new(
        @"@group\s*\(\s*2\s*\)\s*@binding\s*\(\s*0\s*\)\s*var\s*<\s*uniform\s*>\s*\w+\s*:\s*(?<struct>\w+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex VertexEntryRegex = new(@"@vertex\s+fn\s+vs_main\s*\(", RegexOptions.Compiled);

    private static readonly Regex FragmentEntryRegex = new(@"@fragment\s+fn\s+fs_main\s*\(", RegexOptions.Compiled);

    private static readonly Regex LocationRegex = new(@"@location\s*\(\s*(?<location>\d+)\s*\)", RegexOptions.Compiled);

    private static readonly Regex BuiltinRegex = new(@"@builtin\s*\(", RegexOptions.Compiled);

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

    /// <summary><c>group(0) binding(3)</c>（値はコンポーネントが持つ）のパラメータ列の解析</summary>
    public static ShaderParam[] ParseVertexParams(string wgsl) => ParseUniformStruct(wgsl, VertexParamBindingRegex);

    /// <summary><c>group(2) binding(0)</c>（値はマテリアルが持つ）のパラメータ列の解析</summary>
    public static ShaderParam[] ParseFragmentParams(string wgsl) => ParseUniformStruct(wgsl, FragmentParamBindingRegex);

    /// <summary><c>vs_main</c> を持つか</summary>
    public static bool HasVertexEntry(string wgsl) => VertexEntryRegex.IsMatch(StripComments(wgsl));

    /// <summary><c>fs_main</c> を持つか</summary>
    public static bool HasFragmentEntry(string wgsl) => FragmentEntryRegex.IsMatch(StripComments(wgsl));

    /// <summary><c>vs_main</c> が受け取る頂点属性の解析（引数に直接書いたものと、構造体の引数のメンバの両方）</summary>
    public static ShaderVertexInput[] ParseVertexInputs(string wgsl)
    {
        string source = StripComments(wgsl);
        Match entry = VertexEntryRegex.Match(source);
        if (!entry.Success) return [];

        int start = entry.Index + entry.Length;
        int end = start;
        for (int depth = 1; end < source.Length; end++)
        {
            if (source[end] == '(') depth++;
            else if (source[end] == ')' && --depth == 0) break;
        }

        var inputs = new List<ShaderVertexInput>();
        foreach (string argument in SplitTopLevel(source[start..end]))
        {
            if (BuiltinRegex.IsMatch(argument)) continue;
            if (TryParseAttribute(argument, out ShaderVertexInput? direct))
            {
                inputs.Add(direct);
                continue;
            }

            // @location の無い引数は構造体。メンバのうち @location の付いたものが頂点属性。
            Match body = StructBodyRegex(TypeOf(argument)).Match(source);
            if (!body.Success) continue;
            foreach (string member in SplitTopLevel(body.Groups["body"].Value))
            {
                if (TryParseAttribute(member, out ShaderVertexInput? attribute))
                    inputs.Add(attribute);
            }
        }

        return inputs.OrderBy(input => input.Location).ToArray();
    }

    private static bool TryParseAttribute(string declaration, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ShaderVertexInput? input)
    {
        Match location = LocationRegex.Match(declaration);
        if (!location.Success)
        {
            input = null;
            return false;
        }

        input = new ShaderVertexInput
        {
            Location = int.Parse(location.Groups["location"].Value, CultureInfo.InvariantCulture),
            Type = TypeOf(declaration),
        };
        return true;
    }

    /// <summary><c>名前 : 型</c> の型（空白を除き、<c>vec3f</c> の類は <c>vec3&lt;f32&gt;</c> へ揃える）</summary>
    private static string TypeOf(string declaration)
    {
        int colon = declaration.LastIndexOf(':');
        string type = Regex.Replace(colon >= 0 ? declaration[(colon + 1)..] : declaration, @"\s", string.Empty);
        return Regex.Replace(type, @"^vec([234])f$", "vec$1<f32>");
    }

    /// <summary>かっこと山かっこの外にあるカンマでの分割</summary>
    private static IEnumerable<string> SplitTopLevel(string list)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c is '(' or '<') depth++;
            else if (c is ')' or '>') depth--;
            else if (c == ',' && depth == 0)
            {
                if (!string.IsNullOrWhiteSpace(list[start..i])) yield return list[start..i];
                start = i + 1;
            }
        }

        if (!string.IsNullOrWhiteSpace(list[start..])) yield return list[start..];
    }

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
        @"@group\s*\(\s*2\s*\)\s*@binding\s*\(\s*(?<binding>\d+)\s*\)\s*var\s+(?<name>\w+)\s*:\s*texture_2d\s*<\s*f32\s*>\s*;",
        RegexOptions.Compiled);

    /// <summary><c>group(2)</c> の追加テクスチャスロットの解析</summary>
    public static ShaderTextureSlot[] ParseTextureSlots(string wgsl)
    {
        var slots = new List<ShaderTextureSlot>();
        foreach (Match match in TextureBindingRegex.Matches(StripComments(wgsl)))
        {
            int binding = int.Parse(match.Groups["binding"].Value, CultureInfo.InvariantCulture);
            slots.Add(new ShaderTextureSlot { Name = match.Groups["name"].Value, Binding = binding });
        }

        return slots.OrderBy(s => s.Binding).ToArray();
    }

    private static string StripComments(string wgsl)
        => Regex.Replace(StripBlockComments(wgsl), @"//[^\n]*", " ");

    private static string StripBlockComments(string wgsl)
        => Regex.Replace(wgsl, @"/\*.*?\*/", " ", RegexOptions.Singleline);
}
