using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using EmptyEngine.Graphics;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>テクスチャ付きの描画コンポーネントが共有する GPU 機構</summary>
public static unsafe class SpriteRenderSupport
{
    /// <summary>オブジェクトのワールド変換行列の組み立て</summary>
    public static Matrix4x4 ReadTransform(IObject? owner)
        => TransformComponent.GetWorldMatrix(owner);

    /// <summary>Canvas 配下の描画レイヤ番号</summary>
    public const int CanvasRenderLayer = 1000;

    /// <summary>祖先から最初に見つかる <see cref="CanvasScalerComponent"/></summary>
    public static CanvasScalerComponent? FindCanvas(IObject? owner)
    {
        for (IObject? node = owner; node is not null; node = node.Parent)
        {
            if (node.GetAttachable<CanvasScalerComponent>() is { } scaler)
                return scaler;
        }
        return null;
    }

    /// <summary>オブジェクトの描画順レイヤ</summary>
    public static int GetRenderLayer(IObject? owner)
        => FindCanvas(owner) is { } canvas ? CanvasRenderLayer + canvas.SortOrder : 0;

    /// <summary>アセットのピクセル本体の GPU テクスチャへの転送</summary>
    public static Texture* CreateTexture(in RenderContext context, TextureAsset asset)
    {
        BasisTranscodeTarget basisTarget = GpuCapabilities.PreferredTextureCompression switch
        {
            TextureCompressionFamily.Bc => BasisTranscodeTarget.Bc7,
            TextureCompressionFamily.Astc => BasisTranscodeTarget.Astc4x4,
            TextureCompressionFamily.Etc2 => BasisTranscodeTarget.Etc2Rgba8,
            _ => BasisTranscodeTarget.Rgba8,
        };
        TranscodedTexture? transcoded = asset.Format == TexturePixelFormat.BasisUniversalKtx2
            ? asset.Transcode(basisTarget)
            : null;

        TextureFormat format = transcoded is { } basisTexture
            ? basisTexture.Target switch
            {
                BasisTranscodeTarget.Bc7 => TextureFormat.BC7RgbaUnorm,
                BasisTranscodeTarget.Astc4x4 => TextureFormat.ASTC4x4Unorm,
                BasisTranscodeTarget.Etc2Rgba8 => TextureFormat.ETC2Rgba8Unorm,
                _ => TextureFormat.Rgba8Unorm,
            }
            : TextureFormat.Rgba8Unorm;

        var textureDescriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)asset.Width, Height = (uint)asset.Height, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        Texture* texture = WGPU.wgpuDeviceCreateTexture(context.Device, &textureDescriptor);

        byte[]? convertedPixels = transcoded?.Pixels;
        long length = convertedPixels?.Length ?? asset.Pixels.Length;

        byte* buffer = (byte*)NativeMemory.Alloc((nuint)length);
        try
        {
            if (convertedPixels is not null)
            {
                convertedPixels.CopyTo(new Span<byte>(buffer, convertedPixels.Length));
            }
            else
            {
                using Stream stream = asset.Pixels.OpenRead();
                stream.ReadExactly(new Span<byte>(buffer, (int)length));
            }

            (uint bytesPerRow, uint rowsPerImage) = transcoded is { } basis
                ? (basis.BytesPerRow, basis.RowsPerImage)
                : ((uint)(asset.Width * 4), (uint)asset.Height);
            var destination = new ImageCopyTexture
            {
                Texture = texture,
                MipLevel = 0,
                Origin = default,
                Aspect = TextureAspect.All,
            };
            var dataLayout = new TextureDataLayout { Offset = 0, BytesPerRow = bytesPerRow, RowsPerImage = rowsPerImage };
            var writeSize = new Extent3D { Width = (uint)asset.Width, Height = (uint)asset.Height, DepthOrArrayLayers = 1 };
            WGPU.wgpuQueueWriteTexture(context.Queue, &destination, buffer, (nuint)length, &dataLayout, &writeSize);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return texture;
    }

    /// <summary>通常のアルファ合成のブレンド成分</summary>
    public static (BlendComponent Color, BlendComponent Alpha) AlphaBlend => (
        new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
        new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add });

    private static StencilFaceState StencilDisabled => new()
    {
        Compare = CompareFunction.Always,
        FailOp = StencilOperation.Keep,
        DepthFailOp = StencilOperation.Keep,
        PassOp = StencilOperation.Keep,
    };

    /// <summary>2D 用の深度状態</summary>
    public static DepthStencilState DepthTestNoWrite(TextureFormat depthFormat)
        => DepthTest(depthFormat, writeEnabled: false, CompareFunction.LessEqual);

    /// <summary>深度比較と深度書き込みの指定から描画時の深度状態を作成する</summary>
    public static DepthStencilState DepthTest(
        TextureFormat depthFormat,
        bool writeEnabled,
        CompareFunction compare) => new()
    {
        Format = depthFormat,
        DepthWriteEnabled = writeEnabled ? 1u : 0u,
        DepthCompare = compare,
        StencilFront = StencilDisabled,
        StencilBack = StencilDisabled,
        StencilReadMask = 0xFFFFFFFF,
        StencilWriteMask = 0xFFFFFFFF,
    };

    /// <summary>不透明 3D メッシュ用の深度状態</summary>
    public static DepthStencilState DepthTestWrite(TextureFormat depthFormat)
        => DepthTest(depthFormat, writeEnabled: true, CompareFunction.LessEqual);

    /// <summary>同一深度のオーバーレイを許可する深度書き込み状態</summary>
    public static DepthStencilState DepthTestWriteLessEqual(TextureFormat depthFormat)
        => DepthTest(depthFormat, writeEnabled: true, CompareFunction.LessEqual);

    /// <summary>uniform + texture + sampler の 3 点 bind group レイアウトの生成</summary>
    public static BindGroupLayout* CreateTexturedQuadLayout(in RenderContext context, ShaderStage uniformVisibility, ulong uniformMinSize)
    {
        var entries = stackalloc BindGroupLayoutEntry[3];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = uniformVisibility,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = uniformMinSize },
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = 0,
            },
        };
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        var descriptor = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = entries };
        return WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &descriptor);
    }

    /// <summary>group(0) と、指定があれば group(1)（<see cref="RenderContext.GlobalsLayout"/>）を並べたパイプラインレイアウトの生成</summary>
    public static PipelineLayout* CreatePipelineLayout(
        in RenderContext context, BindGroupLayout* bindGroupLayout, BindGroupLayout* globalsLayout)
    {
        BindGroupLayout** layouts = stackalloc BindGroupLayout*[2];
        layouts[0] = bindGroupLayout;
        layouts[1] = globalsLayout;
        var descriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = (nuint)(globalsLayout is null ? 1 : 2),
            BindGroupLayouts = layouts,
        };
        return WGPU.wgpuDeviceCreatePipelineLayout(context.Device, &descriptor);
    }

    /// <summary>共有 quad 頂点を入力に取るレンダーパイプラインの生成</summary>
    public static RenderPipeline* CreateQuadPipeline(
        in RenderContext context,
        string wgsl,
        BindGroupLayout* bindGroupLayout,
        in BlendComponent colorBlend,
        in BlendComponent alphaBlend,
        out PipelineLayout* pipelineLayout,
        bool depthWrite = false,
        CompareFunction depthCompare = CompareFunction.LessEqual,
        BindGroupLayout* globalsLayout = null)
    {
        nint codePtr = Marshal.StringToCoTaskMemUTF8(wgsl);
        nint vsEntry = Marshal.StringToCoTaskMemUTF8("vs_main");
        nint fsEntry = Marshal.StringToCoTaskMemUTF8("fs_main");
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWGSLDescriptor },
                Code = (byte*)codePtr,
            };
            var shaderModuleDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDescriptor };
            ShaderModule* module = WGPU.wgpuDeviceCreateShaderModule(context.Device, &shaderModuleDescriptor);

            pipelineLayout = CreatePipelineLayout(in context, bindGroupLayout, globalsLayout);

            var vertexAttributes = stackalloc VertexAttribute[2];
            vertexAttributes[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            vertexAttributes[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 2, ShaderLocation = 1 };
            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = sizeof(float) * 4,
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 2,
                Attributes = vertexAttributes,
            };
            var vertexState = new VertexState
            {
                Module = module,
                EntryPoint = (byte*)vsEntry,
                BufferCount = 1,
                Buffers = &vertexBufferLayout,
            };

            var blendState = new BlendState { Color = colorBlend, Alpha = alphaBlend };
            var colorTargetState = new ColorTargetState
            {
                Format = context.TargetFormat,
                Blend = &blendState,
                WriteMask = ColorWriteMask.All,
            };
            var fragmentState = new FragmentState
            {
                Module = module,
                EntryPoint = (byte*)fsEntry,
                TargetCount = 1,
                Targets = &colorTargetState,
            };

            DepthStencilState depthState = DepthTest(context.DepthFormat, depthWrite, depthCompare);
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = pipelineLayout,
                Vertex = vertexState,
                Fragment = &fragmentState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None,
                },
                DepthStencil = &depthState,
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = 0 },
            };
            RenderPipeline* pipeline = WGPU.wgpuDeviceCreateRenderPipeline(context.Device, &pipelineDescriptor);

            WGPU.wgpuShaderModuleRelease(module);
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem(codePtr);
            Marshal.FreeCoTaskMem(vsEntry);
            Marshal.FreeCoTaskMem(fsEntry);
        }
    }

    /// <summary>uniform + texture + sampler の 3 点 bind group の生成</summary>
    public static BindGroup* CreateTexturedBindGroup(
        in RenderContext context,
        BindGroupLayout* layout,
        WgpuBuffer* uniform,
        ulong uniformSize,
        TextureView* textureView,
        Sampler* sampler)
    {
        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniform, Offset = 0, Size = uniformSize };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = textureView };
        entries[2] = new BindGroupEntry { Binding = 2, Sampler = sampler };
        var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 3, Entries = entries };
        return WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);
    }

    /// <summary>共有 quad 頂点バッファでの 1 枚の quad の描画</summary>
    public static void DrawQuad(in RenderContext context, RenderPipeline* pipeline, BindGroup* bindGroup)
    {
        WGPU.wgpuRenderPassEncoderSetPipeline(context.Pass, pipeline);
        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 0, bindGroup, 0, null);
        DrawQuadVertices(in context);
    }

    public static void DrawQuadVertices(in RenderContext context)
    {
        WGPU.wgpuRenderPassEncoderSetVertexBuffer(
            context.Pass,
            0,
            context.QuadVertexBuffer,
            0,
            context.QuadVertexCount * sizeof(float) * 4);
        WGPU.wgpuRenderPassEncoderDraw(context.Pass, context.QuadVertexCount, 1, 0, 0);
    }

    /// <summary>アセットに対応する WGSL ソース文字列</summary>
    private static readonly ConditionalWeakTable<ShaderAsset, string> ShaderSourceCache = new();

    /// <summary>WGSL アセット本体の UTF-8 文字列としての読み出し</summary>
    /// <remarks>同じアセットに対しては最初の 1 回だけ実際に読む</remarks>
    public static string ReadShaderSource(ShaderAsset shader)
        => ShaderSourceCache.GetValue(shader, static asset =>
        {
            using Stream stream = asset.Source.OpenRead();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            // WGSL パーサーが BOM を受け付けないため除去する。
            return Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
        });
}
