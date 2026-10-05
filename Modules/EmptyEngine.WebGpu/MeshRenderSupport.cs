using System.Runtime.InteropServices;
using EmptyEngine.Core;
using EmptyEngine.ObjectModel;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>3D メッシュの描画に必要な GPU 資源を作成する</summary>
internal static unsafe class MeshRenderSupport
{
    /// <summary>メッシュ用レンダーパイプラインの WGSL からの生成</summary>
    public static RenderPipeline* CreateMeshPipeline(
        in RenderContext context,
        string wgsl,
        BindGroupLayout* bindGroupLayout,
        (BlendComponent Color, BlendComponent Alpha)? blend,
        bool depthWrite,
        CompareFunction depthCompare,
        out PipelineLayout* pipelineLayout,
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

            pipelineLayout = SpriteRenderSupport.CreatePipelineLayout(in context, bindGroupLayout, globalsLayout);

            var vertexAttributes = stackalloc VertexAttribute[3];
            vertexAttributes[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
            vertexAttributes[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = sizeof(float) * 3, ShaderLocation = 1 };
            vertexAttributes[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = sizeof(float) * 6, ShaderLocation = 2 };
            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = sizeof(float) * 8,
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
                Attributes = vertexAttributes,
            };
            var vertexState = new VertexState
            {
                Module = module,
                EntryPoint = (byte*)vsEntry,
                BufferCount = 1,
                Buffers = &vertexBufferLayout,
            };

            var blendState = default(BlendState);
            var colorTargetState = new ColorTargetState
            {
                Format = context.TargetFormat,
                Blend = null,
                WriteMask = ColorWriteMask.All,
            };
            if (blend is { } b)
            {
                blendState = new BlendState { Color = b.Color, Alpha = b.Alpha };
                colorTargetState.Blend = &blendState;
            }
            var fragmentState = new FragmentState
            {
                Module = module,
                EntryPoint = (byte*)fsEntry,
                TargetCount = 1,
                Targets = &colorTargetState,
            };

            DepthStencilState depthState = SpriteRenderSupport.DepthTest(
                context.DepthFormat, depthWrite, depthCompare);
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = pipelineLayout,
                Vertex = vertexState,
                Fragment = &fragmentState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = CoordinateConvention.IsLeftHanded ? FrontFace.CW : FrontFace.Ccw,
                    CullMode = CullMode.Back,
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

    /// <summary>カスタムシェーダ用の bind group layout</summary>
    public static BindGroupLayout* CreateShaderParamLayout(
        in RenderContext context, ulong uniformSize, ulong paramSize, ShaderTextureSlot[]? textureSlots = null)
    {
        ShaderTextureSlot[] slots = textureSlots ?? [];
        var layoutEntries = stackalloc BindGroupLayoutEntry[3 + slots.Length];
        layoutEntries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = uniformSize },
        };
        layoutEntries[1] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        layoutEntries[2] = new BindGroupLayoutEntry
        {
            Binding = 3,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = paramSize },
        };
        for (int i = 0; i < slots.Length; i++)
        {
            layoutEntries[3 + i] = new BindGroupLayoutEntry
            {
                Binding = (uint)slots[i].Binding,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = 0,
                },
            };
        }
        var descriptor = new BindGroupLayoutDescriptor { EntryCount = (nuint)(3 + slots.Length), Entries = layoutEntries };
        return WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &descriptor);
    }

    /// <summary><see cref="CreateShaderParamLayout"/> に対応する bind group</summary>
    public static BindGroup* CreateShaderParamBindGroup(
        in RenderContext context,
        BindGroupLayout* layout,
        WgpuBuffer* uniformBuffer,
        ulong uniformSize,
        Sampler* sampler,
        WgpuBuffer* paramBuffer,
        ulong paramSize,
        ReadOnlySpan<int> slotBindings = default,
        ReadOnlySpan<nint> slotViews = default)
    {
        int slotCount = slotBindings.Length;
        var entries = stackalloc BindGroupEntry[3 + slotCount];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = uniformBuffer, Offset = 0, Size = uniformSize };
        entries[1] = new BindGroupEntry { Binding = 2, Sampler = sampler };
        entries[2] = new BindGroupEntry { Binding = 3, Buffer = paramBuffer, Offset = 0, Size = paramSize };
        for (int i = 0; i < slotCount; i++)
        {
            entries[3 + i] = new BindGroupEntry { Binding = (uint)slotBindings[i], TextureView = (TextureView*)slotViews[i] };
        }
        var descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = (nuint)(3 + slotCount), Entries = entries };
        return WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);
    }

    /// <summary>アセット本体の GPU バッファへの転送</summary>
    public static WgpuBuffer* CreateBufferFromBinary(in RenderContext context, IAssetBinary binary, BufferUsage usage)
    {
        long length = binary.Length;
        var descriptor = new BufferDescriptor
        {
            Usage = usage | BufferUsage.CopyDst,
            Size = (ulong)length,
        };
        WgpuBuffer* buffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &descriptor);

        byte* scratch = (byte*)NativeMemory.Alloc((nuint)length);
        try
        {
            using Stream stream = binary.OpenRead();
            stream.ReadExactly(new Span<byte>(scratch, (int)length));
            WGPU.wgpuQueueWriteBuffer(context.Queue, buffer, 0, scratch, (nuint)length);
        }
        finally
        {
            NativeMemory.Free(scratch);
        }

        return buffer;
    }
}
