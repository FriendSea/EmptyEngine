using System.Numerics;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>プラットフォーム非依存の描画コア</summary>
internal sealed unsafe class WebGpuRenderer : IDisposable
{
    /// <summary>レンダーパスの深度アタッチメントのフォーマット</summary>
    public const TextureFormat DepthFormat = TextureFormat.Depth24Plus;

    private static readonly float[] QuadVertices =
    {
        -0.5f, -0.5f,    0f, 1f,
         0.5f, -0.5f,    1f, 1f,
         0.5f,  0.5f,    1f, 0f,
        -0.5f, -0.5f,    0f, 1f,
         0.5f,  0.5f,    1f, 0f,
        -0.5f,  0.5f,    0f, 0f,
    };

    private const string BlitShaderSource = """
        @group(0) @binding(0) var tex: texture_2d<f32>;
        @group(0) @binding(1) var samp: sampler;

        struct VsOut {
            @builtin(position) position: vec4<f32>,
            @location(0) uv: vec2<f32>,
        };

        @vertex
        fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>) -> VsOut {
            var out: VsOut;
            out.position = vec4<f32>(position * 2.0, 0.0, 1.0);
            out.uv = uv;
            return out;
        }

        @fragment
        fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            return textureSample(tex, samp, in.uv);
        }
        """;

    private Device* _device;
    private Queue* _queue;
    private TextureFormat _format;
    private readonly GpuResourcePool _resources = new();
    private readonly GpuAssetCache _assets = new();

    private WgpuBuffer* _quadVertexBuffer;
    private Texture* _depthTexture;
    private TextureView* _depthTextureView;
    private uint _depthWidth;
    private uint _depthHeight;
    private Texture* _defaultTexture;
    private TextureView* _defaultTextureView;
    private Sampler* _sampler;
    private Sampler* _repeatSampler;
    private Texture* _opaqueTexture;
    private TextureView* _opaqueTextureView;
    private uint _opaqueWidth;
    private uint _opaqueHeight;
    private RenderPipeline* _blitPipeline;
    private PipelineLayout* _blitPipelineLayout;
    private BindGroupLayout* _blitLayout;
    private BindGroup* _blitBindGroup;
    private readonly uint _quadVertexCount = (uint)(QuadVertices.Length / 4);
    private readonly List<RenderEntry> _opaqueEntries = [];
    private readonly List<RenderEntry> _transparentEntries = [];
    private readonly List<RenderEntry> _canvasEntries = [];

    /// <summary>不透明の描画が終わった時点の画面を、以降のシェーダが読める形で残すか</summary>
    public bool CaptureOpaqueTexture { get; set; }

    /// <summary>device/queue と描画先フォーマットからの共有 GPU リソースの生成</summary>
    public void Initialize(Device* device, Queue* queue, TextureFormat format)
    {
        _device = device;
        _queue = queue;
        _format = format;
        CreateQuadVertexBuffer();
        CreateDefaultTextureAndSampler();
    }

    private void CreateQuadVertexBuffer()
    {
        ulong size = (ulong)(QuadVertices.Length * sizeof(float));
        var bufferDescriptor = new BufferDescriptor
        {
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst,
            Size = size,
        };
        _quadVertexBuffer = WGPU.wgpuDeviceCreateBuffer(_device, &bufferDescriptor);
        fixed (float* vertices = QuadVertices)
        {
            WGPU.wgpuQueueWriteBuffer(_queue, _quadVertexBuffer, 0, vertices, (nuint)size);
        }
    }

    private void CreateDefaultTextureAndSampler()
    {
        var textureDescriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _defaultTexture = WGPU.wgpuDeviceCreateTexture(_device, &textureDescriptor);

        uint white = 0xFFFFFFFFu;
        var destination = new ImageCopyTexture
        {
            Texture = _defaultTexture,
            MipLevel = 0,
            Origin = default,
            Aspect = TextureAspect.All,
        };
        var dataLayout = new TextureDataLayout { Offset = 0, BytesPerRow = 4, RowsPerImage = 1 };
        var writeSize = new Extent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 };
        WGPU.wgpuQueueWriteTexture(_queue, &destination, &white, 4, &dataLayout, &writeSize);

        _defaultTextureView = WGPU.wgpuTextureCreateView(_defaultTexture, null);

        var samplerDescriptor = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0f,
            LodMaxClamp = 1f,
            MaxAnisotropy = 1,
        };
        _sampler = WGPU.wgpuDeviceCreateSampler(_device, &samplerDescriptor);

        samplerDescriptor.AddressModeU = AddressMode.Repeat;
        samplerDescriptor.AddressModeV = AddressMode.Repeat;
        samplerDescriptor.AddressModeW = AddressMode.Repeat;
        _repeatSampler = WGPU.wgpuDeviceCreateSampler(_device, &samplerDescriptor);
    }

    /// <summary>1 フレーム分のレンダーパスの記録と submit</summary>
    public void RenderFrame(
        TextureView* targetView,
        int width,
        int height,
        Color clearColor,
        RenderWorld world)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        float aspect = (float)width / height;
        EnsureDepthTexture((uint)width, (uint)height);

        bool capturesOpaque = CaptureOpaqueTexture;
        if (capturesOpaque)
        {
            EnsureOpaqueTarget((uint)width, (uint)height);
        }

        IRenderCamera? camera = world.ViewCamera;
        world.RecomputeCanvases(aspect);

        Matrix4x4 viewMatrix = camera?.BuildViewMatrix() ?? Matrix4x4.Identity;
        Matrix4x4 projectionMatrix = camera?.BuildProjectionMatrix(aspect) ?? BuildFallbackProjection(aspect);
        Matrix4x4 worldProjection = viewMatrix * projectionMatrix;

        var encoderDescriptor = new CommandEncoderDescriptor();
        CommandEncoder* encoder = WGPU.wgpuDeviceCreateCommandEncoder(_device, &encoderDescriptor);

        var colorAttachment = new RenderPassColorAttachment
        {
            View = capturesOpaque ? _opaqueTextureView : targetView,
            DepthSlice = 0xFFFFFFFF,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = clearColor,
        };
        var depthAttachment = new RenderPassDepthStencilAttachment
        {
            View = _depthTextureView,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = 1f,
        };
        var renderPassDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = &depthAttachment,
        };

        RenderPassEncoder* pass = WGPU.wgpuCommandEncoderBeginRenderPass(encoder, &renderPassDescriptor);

        var context = new RenderContext
        {
            Device = _device,
            Queue = _queue,
            Resources = _resources,
            Assets = _assets,
            Pass = pass,
            QuadVertexBuffer = _quadVertexBuffer,
            QuadVertexCount = _quadVertexCount,
            DefaultTextureView = _defaultTextureView,
            DefaultSampler = _sampler,
            RepeatSampler = _repeatSampler,
            OpaqueTextureView = _defaultTextureView,
            Projection = worldProjection,
            ViewMatrix = viewMatrix,
            ProjectionMatrix = projectionMatrix,
            Time = (float)world.Time,
            Globals = world.Globals,
            TargetFormat = _format,
            DepthFormat = DepthFormat,
        };
        Classify(world, worldProjection);

        _opaqueEntries.Sort(RenderQueueOrder.Instance);
        for (int i = 0; i < _opaqueEntries.Count; i++)
        {
            _opaqueEntries[i].Renderer.Render(in context);
        }

        _transparentEntries.Sort(TransparentOrder.Instance);
        _canvasEntries.Sort(CanvasOrder.Instance);

        // 捕らえた画面を画面へ写すのは、最初の半透明を描く直前。ワールドに半透明が無ければ
        // Canvas の中まで持ち越し、そこまでに描いた Canvas の絵も捕らえた画面へ含める。
        bool pendingCapture = capturesOpaque;
        int captureIndex = -1;
        if (pendingCapture && _transparentEntries.Count == 0)
        {
            captureIndex = _canvasEntries.Count;
            for (int i = 0; i < _canvasEntries.Count; i++)
            {
                if (_canvasEntries[i].Queue >= ShaderRenderQueue.Transparent)
                {
                    captureIndex = i;
                    break;
                }
            }
        }

        if (pendingCapture && captureIndex < 0)
        {
            ShowCapture(encoder, &renderPassDescriptor, targetView, ref context, ref pass);
            pendingCapture = false;
        }

        for (int i = 0; i < _transparentEntries.Count; i++)
        {
            _transparentEntries[i].Renderer.Render(in context);
        }

        CanvasScalerComponent? previousCanvas = null;
        int previousLayer = 0;
        for (int i = 0; i < _canvasEntries.Count; i++)
        {
            RenderEntry entry = _canvasEntries[i];
            if (!ReferenceEquals(previousCanvas, entry.Canvas) || previousLayer != entry.Layer)
            {
                // ワールドや前の Canvas の深度でオーバーレイが欠けないよう、色を保ち深度だけ初期化する。
                // 同じ Canvas・レイヤ内では深度を共有し、メッシュ内部とメッシュ同士の隠面消去を行う。
                WGPU.wgpuRenderPassEncoderEnd(pass);
                WGPU.wgpuRenderPassEncoderRelease(pass);
                colorAttachment.LoadOp = LoadOp.Load;
                pass = WGPU.wgpuCommandEncoderBeginRenderPass(encoder, &renderPassDescriptor);
                context.Pass = pass;
                previousCanvas = entry.Canvas;
                previousLayer = entry.Layer;
            }
            if (pendingCapture && i == captureIndex)
            {
                ShowCapture(encoder, &renderPassDescriptor, targetView, ref context, ref pass);
                pendingCapture = false;
            }
            context.ViewMatrix = Matrix4x4.Identity;
            context.ProjectionMatrix = entry.Canvas!.Projection;
            context.Projection = context.ProjectionMatrix;
            entry.Renderer.Render(in context);
        }

        if (pendingCapture)
        {
            ShowCapture(encoder, &renderPassDescriptor, targetView, ref context, ref pass);
        }

        WGPU.wgpuRenderPassEncoderEnd(pass);

        // submit 時に command buffer の参照が残らないよう、先にパスを解放する。
        WGPU.wgpuRenderPassEncoderRelease(pass);

        var commandBufferDescriptor = new CommandBufferDescriptor();
        CommandBuffer* command = WGPU.wgpuCommandEncoderFinish(encoder, &commandBufferDescriptor);
        WGPU.wgpuQueueSubmit(_queue, 1, &command);

        WGPU.wgpuCommandBufferRelease(command);
        WGPU.wgpuCommandEncoderRelease(encoder);
    }

    /// <summary>捕らえた画面の画面への写し込みと、以降の描画先の切り替え</summary>
    /// <remarks>ここまでの絵はこのあとテクスチャとして読むので、画面へは写し直してから続きを重ねる。</remarks>
    private void ShowCapture(
        CommandEncoder* encoder,
        RenderPassDescriptor* descriptor,
        TextureView* targetView,
        ref RenderContext context,
        ref RenderPassEncoder* pass)
    {
        RenderPassColorAttachment* color = descriptor->ColorAttachments;
        var depth = (RenderPassDepthStencilAttachment*)descriptor->DepthStencilAttachment;

        WGPU.wgpuRenderPassEncoderEnd(pass);
        WGPU.wgpuRenderPassEncoderRelease(pass);

        color->View = targetView;
        color->LoadOp = LoadOp.Clear;
        LoadOp resumed = depth->DepthLoadOp;
        depth->DepthLoadOp = LoadOp.Load;
        pass = WGPU.wgpuCommandEncoderBeginRenderPass(encoder, descriptor);
        depth->DepthLoadOp = resumed;
        color->LoadOp = LoadOp.Load;

        context.Pass = pass;
        context.OpaqueTextureView = _opaqueTextureView;
        SpriteRenderSupport.DrawQuad(in context, _blitPipeline, _blitBindGroup);
    }

    /// <summary>描画対象の 3 つのパスへの振り分け</summary>
    /// <remarks>振り分け中の登録・解除は、そのフレームの描画順に影響しない。</remarks>
    private void Classify(RenderWorld world, Matrix4x4 worldProjection)
    {
        _opaqueEntries.Clear();
        _transparentEntries.Clear();
        _canvasEntries.Clear();

        IReadOnlyList<ISpriteRenderer> renderers = world.Renderers;
        for (int i = 0; i < renderers.Count; i++)
        {
            ISpriteRenderer renderer = renderers[i];
            if (SpriteRenderSupport.FindCanvas(renderer.Owner) is { } canvas)
            {
                _canvasEntries.Add(new RenderEntry(
                    renderer, canvas, renderer.RenderQueue, renderer.RenderLayer, 0f, i));
            }
            else if (renderer.RenderQueue < ShaderRenderQueue.Transparent)
            {
                _opaqueEntries.Add(new RenderEntry(renderer, null, renderer.RenderQueue, 0, 0f, i));
            }
            else
            {
                _transparentEntries.Add(new RenderEntry(
                    renderer,
                    null,
                    renderer.RenderQueue,
                    renderer.RenderLayer,
                    GetProjectedDepth(renderer, worldProjection),
                    i));
            }
        }
    }

    private static float GetProjectedDepth(ISpriteRenderer renderer, Matrix4x4 worldProjection)
    {
        Vector4 clipPosition = Vector4.Transform(new Vector4(renderer.SortPosition, 1f), worldProjection);
        if (MathF.Abs(clipPosition.W) <= 1e-6f)
        {
            return 0f;
        }

        float depth = clipPosition.Z / clipPosition.W;
        return float.IsFinite(depth) ? depth : 0f;
    }

    private void EnsureDepthTexture(uint width, uint height)
    {
        if (_depthTexture is not null && _depthWidth == width && _depthHeight == height)
        {
            return;
        }

        if (_depthTextureView is not null) WGPU.wgpuTextureViewRelease(_depthTextureView);
        if (_depthTexture is not null) WGPU.wgpuTextureRelease(_depthTexture);

        var descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = width, Height = height, DepthOrArrayLayers = 1 },
            Format = DepthFormat,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _depthTexture = WGPU.wgpuDeviceCreateTexture(_device, &descriptor);
        _depthTextureView = WGPU.wgpuTextureCreateView(_depthTexture, null);
        _depthWidth = width;
        _depthHeight = height;
    }

    private void EnsureOpaqueTarget(uint width, uint height)
    {
        if (_blitPipeline is null)
        {
            CreateBlitPipeline();
        }

        if (_opaqueTexture is not null && _opaqueWidth == width && _opaqueHeight == height)
        {
            return;
        }

        ReleaseOpaqueTarget();

        var descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = width, Height = height, DepthOrArrayLayers = 1 },
            Format = _format,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _opaqueTexture = WGPU.wgpuDeviceCreateTexture(_device, &descriptor);
        _opaqueTextureView = WGPU.wgpuTextureCreateView(_opaqueTexture, null);
        _opaqueWidth = width;
        _opaqueHeight = height;

        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = _opaqueTextureView };
        entries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        var bindGroupDescriptor = new BindGroupDescriptor { Layout = _blitLayout, EntryCount = 2, Entries = entries };
        _blitBindGroup = WGPU.wgpuDeviceCreateBindGroup(_device, &bindGroupDescriptor);
    }

    private void CreateBlitPipeline()
    {
        var layoutEntries = stackalloc BindGroupLayoutEntry[2];
        layoutEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = 0,
            },
        };
        layoutEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = layoutEntries };
        _blitLayout = WGPU.wgpuDeviceCreateBindGroupLayout(_device, &layoutDescriptor);

        var setup = new RenderContext { Device = _device, TargetFormat = _format, DepthFormat = DepthFormat };
        var replace = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
        _blitPipeline = SpriteRenderSupport.CreateQuadPipeline(
            in setup,
            BlitShaderSource,
            _blitLayout,
            replace,
            replace,
            out _blitPipelineLayout,
            depthWrite: false,
            depthCompare: CompareFunction.Always);
    }

    private void ReleaseOpaqueTarget()
    {
        if (_blitBindGroup is not null) { WGPU.wgpuBindGroupRelease(_blitBindGroup); _blitBindGroup = null; }
        if (_opaqueTextureView is not null) { WGPU.wgpuTextureViewRelease(_opaqueTextureView); _opaqueTextureView = null; }
        if (_opaqueTexture is not null) { WGPU.wgpuTextureRelease(_opaqueTexture); _opaqueTexture = null; }
        _opaqueWidth = 0;
        _opaqueHeight = 0;
    }

    private static Matrix4x4 BuildFallbackProjection(float aspect)
    {
        if (aspect <= 0f)
        {
            return Matrix4x4.Identity;
        }

        return aspect >= 1f
            ? Matrix4x4.CreateScale(1f / aspect, 1f, 1f)
            : Matrix4x4.CreateScale(1f, aspect, 1f);
    }

    public void Dispose()
    {
        ReleaseOpaqueTarget();
        if (_blitPipeline is not null) WGPU.wgpuRenderPipelineRelease(_blitPipeline);
        if (_blitPipelineLayout is not null) WGPU.wgpuPipelineLayoutRelease(_blitPipelineLayout);
        if (_blitLayout is not null) WGPU.wgpuBindGroupLayoutRelease(_blitLayout);
        if (_depthTextureView is not null) WGPU.wgpuTextureViewRelease(_depthTextureView);
        if (_depthTexture is not null) WGPU.wgpuTextureRelease(_depthTexture);
        if (_sampler is not null) WGPU.wgpuSamplerRelease(_sampler);
        if (_repeatSampler is not null) WGPU.wgpuSamplerRelease(_repeatSampler);
        if (_defaultTextureView is not null) WGPU.wgpuTextureViewRelease(_defaultTextureView);
        if (_defaultTexture is not null) WGPU.wgpuTextureRelease(_defaultTexture);
        if (_quadVertexBuffer is not null) WGPU.wgpuBufferRelease(_quadVertexBuffer);

        // 取りこぼされたコンポーネント側リソース（OnDestroy を通らなかった分）の掃除
        _resources.Dispose();
        _assets.Dispose();
    }
}
