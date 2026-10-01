using System.Globalization;
using System.Text.RegularExpressions;

namespace EmptyEngine.WebGpu.Editor;

/// <summary>WGSL ソースの <c>//!</c> ディレクティブの解析</summary>
internal static class WgslShaderDirectives
{
    private static readonly Regex InvertDirective = new(
        @"//!\s*blend\s*:\s*invert", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DstAlphaMaskDirective = new(
        @"//!\s*blend\s*:\s*dst-alpha-mask", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlphaDirective = new(
        @"//!\s*blend\s*:\s*alpha", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpaqueDirective = new(
        @"//!\s*render\s*:\s*opaque", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TransparentDirective = new(
        @"//!\s*render\s*:\s*transparent", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QueueDirective = new(
        @"//!\s*queue\s*:\s*(background|geometry|alpha-test|transparent|overlay|\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZWriteDirective = new(
        @"//!\s*zwrite\s*:\s*(on|off)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZTestDirective = new(
        @"//!\s*ztest\s*:\s*(less-equal|lequal|less|always|off)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>//! blend:</c> の読み取り</summary>
    public static ShaderBlendMode ParseBlend(string wgsl)
    {
        if (InvertDirective.IsMatch(wgsl)) return ShaderBlendMode.Invert;
        if (DstAlphaMaskDirective.IsMatch(wgsl)) return ShaderBlendMode.DstAlphaMask;
        if (AlphaDirective.IsMatch(wgsl)) return ShaderBlendMode.Alpha;
        return ShaderBlendMode.Unspecified;
    }

    /// <summary><c>//! render:</c> の読み取り</summary>
    public static ShaderRenderMode ParseRender(string wgsl)
    {
        if (OpaqueDirective.IsMatch(wgsl)) return ShaderRenderMode.Opaque;
        if (TransparentDirective.IsMatch(wgsl)) return ShaderRenderMode.Transparent;
        return ShaderRenderMode.Unspecified;
    }

    /// <summary><c>//! queue:</c> の描画キュー番号への解決</summary>
    /// <returns>指定が無ければ 0</returns>
    public static int ParseQueue(string wgsl)
    {
        Match queue = QueueDirective.Match(wgsl);
        if (!queue.Success) return 0;

        string value = queue.Groups[1].Value;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return numeric;

        return value.ToLowerInvariant() switch
        {
            "background" => ShaderRenderQueue.Background,
            "geometry" => ShaderRenderQueue.Geometry,
            "alpha-test" => ShaderRenderQueue.AlphaTest,
            "overlay" => ShaderRenderQueue.Overlay,
            _ => ShaderRenderQueue.Transparent,
        };
    }

    /// <summary><c>//! zwrite:</c> の読み取り</summary>
    public static ShaderDepthWrite ParseDepthWrite(string wgsl)
    {
        Match zWrite = ZWriteDirective.Match(wgsl);
        if (!zWrite.Success) return ShaderDepthWrite.Unspecified;

        return zWrite.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase)
            ? ShaderDepthWrite.On
            : ShaderDepthWrite.Off;
    }

    /// <summary><c>//! ztest:</c> の読み取り</summary>
    public static ShaderDepthCompare ParseDepthCompare(string wgsl)
    {
        Match zTest = ZTestDirective.Match(wgsl);
        if (!zTest.Success) return ShaderDepthCompare.Unspecified;

        return zTest.Groups[1].Value.ToLowerInvariant() switch
        {
            "less" => ShaderDepthCompare.Less,
            "always" or "off" => ShaderDepthCompare.Always,
            _ => ShaderDepthCompare.LessEqual,
        };
    }
}
