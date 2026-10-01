extern alias BrowserAbi;
extern alias IosAbi;

using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace EmptyEngine.WebGpu.Tests;

/// <summary>
/// Emscripten 3.1.56 and Dawn ad853f80 share these ABI values. Platform-specific
/// API availability may differ, but the managed declarations must not.
/// </summary>
public sealed class WebGpuEnumAbiTests
{
    private static readonly (Assembly Assembly, string Library)[] Targets =
    [
        (typeof(CompareFunction).Assembly, "webgpu_dawn"),
        (typeof(BrowserAbi::EmptyEngine.WebGpu.CompareFunction).Assembly, "webgpu_dawn"),
        (typeof(IosAbi::EmptyEngine.WebGpu.CompareFunction).Assembly, "__Internal"),
    ];

    private static readonly string[] Pinned =
    [
        "AddressMode|ClampToEdge=1,Repeat=2,MirrorRepeat=3",
        "BlendFactor|Zero=1,One=2,Src=3,OneMinusSrc=4,SrcAlpha=5,OneMinusSrcAlpha=6,Dst=7,OneMinusDst=8,DstAlpha=9,OneMinusDstAlpha=10,SrcAlphaSaturated=11,Constant=12,OneMinusConstant=13",
        "BlendOperation|Add=1,Subtract=2,ReverseSubtract=3,Min=4,Max=5",
        "CompareFunction|Undefined=0,Never=1,Less=2,Equal=3,LessEqual=4,Greater=5,NotEqual=6,GreaterEqual=7,Always=8",
        "CullMode|None=1,Front=2,Back=3",
        "FilterMode|Nearest=1,Linear=2",
        "FrontFace|Ccw=1,CW=2",
        "MipmapFilterMode|Nearest=1,Linear=2",
        "PresentMode|Fifo=1,FifoRelaxed=2,Immediate=3,Mailbox=4",
        "PrimitiveTopology|PointList=1,LineList=2,LineStrip=3,TriangleList=4,TriangleStrip=5",
        "StencilOperation|Keep=1,Zero=2,Replace=3,Invert=4,IncrementClamp=5,DecrementClamp=6,IncrementWrap=7,DecrementWrap=8",
        "TextureAspect|All=1,StencilOnly=2,DepthOnly=3",
        "TextureDimension|Dimension1D=1,Dimension2D=2,Dimension3D=3",
        "VertexStepMode|VertexBufferNotUsed=1,Vertex=2,Instance=3",
    ];

    [Theory]
    [MemberData(nameof(PinnedTable))]
    public void Values_match_emscripten_3_1_56_and_pinned_dawn(string name, string expected)
    {
        foreach ((Assembly assembly, _) in Targets)
        {
            Type type = assembly.GetType("EmptyEngine.WebGpu." + name, throwOnError: true)!;
            string[] actual = Enum.GetNames(type).Select(n =>
                n + "=" + Convert.ToInt64(Enum.Parse(type, n), CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(expected.Split(',').Order(), actual.Order());
        }
    }

    [Fact]
    public void Compiled_targets_share_declarations_and_the_pinned_descriptor_layout()
    {
        HashSet<string> abi = new(Targets[1..]
            .SelectMany(target => AbiTypes(target.Assembly).Select(type => type.FullName!)), StringComparer.Ordinal);
        string[] desktop = Declarations(Targets[0].Assembly, abi);
        foreach ((Assembly assembly, string library) in Targets)
        {
            Assert.Equal(desktop, Declarations(assembly, abi));
            Type api = assembly.GetType("EmptyEngine.WebGpu.WGPU", throwOnError: true)!;
            DllImportAttribute[] imports = api.GetMethods().Select(m => m.GetCustomAttribute<DllImportAttribute>())
                .OfType<DllImportAttribute>().ToArray();
            Assert.NotEmpty(imports);
            Assert.All(imports, import => Assert.Equal(library, import.Value));

            if (IntPtr.Size != 8) continue;
            Type instance = assembly.GetType("EmptyEngine.WebGpu.InstanceDescriptor", true)!;
            Assert.Equal(32, Marshal.SizeOf(instance));
            Assert.Equal(8, Marshal.OffsetOf(instance, "Features").ToInt32());
            Type adapter = assembly.GetType("EmptyEngine.WebGpu.RequestAdapterOptions", true)!;
            Assert.Equal(32, Marshal.SizeOf(adapter));
            Assert.Equal(24, Marshal.OffsetOf(adapter, "ForceFallbackAdapter").ToInt32());
            Assert.Equal(28, Marshal.OffsetOf(adapter, "CompatibilityMode").ToInt32());
            Type device = assembly.GetType("EmptyEngine.WebGpu.DeviceDescriptor", true)!;
            Assert.Equal(72, Marshal.SizeOf(device));
            Assert.Equal(40, Marshal.OffsetOf(device, "DefaultQueue").ToInt32());
            Assert.Equal(56, Marshal.OffsetOf(device, "DeviceLostCallback").ToInt32());
        }
    }

    public static TheoryData<string, string> PinnedTable()
    {
        var data = new TheoryData<string, string>();
        foreach (string row in Pinned)
        {
            int separator = row.IndexOf('|');
            data.Add(row[..separator], row[(separator + 1)..]);
        }

        return data;
    }

    private static IEnumerable<Type> AbiTypes(Assembly assembly) => assembly.GetExportedTypes()
        .Where(type => type.Namespace == "EmptyEngine.WebGpu");

    private static string[] Declarations(Assembly assembly, HashSet<string> abi) => AbiTypes(assembly)
        .Where(type => abi.Contains(type.FullName!))
        .SelectMany(type =>
        {
            if (type.IsEnum)
                return Enum.GetNames(type).Select(name =>
                    type.FullName + "." + name + "=" + Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture));
            IEnumerable<string> fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(field => type.FullName + "." + field.Name + ":" + field.FieldType);
            IEnumerable<string> imports = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
                .Select(method => type.FullName + "." + method.Name + ":" + method.ReturnType
                    + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.ToString())) + ")");
            return new[] { type.FullName! }.Concat(fields).Concat(imports);
        }).Order(StringComparer.Ordinal).ToArray();
}
