namespace EmptyEngine.WebGpu;

#region 不透明ハンドル（ポインタとしてのみ使う）
public struct Instance { }
public struct Surface { }
public struct Adapter { }
public struct Device { }
public struct Queue { }
public struct ShaderModule { }
public struct BindGroupLayout { }
public struct PipelineLayout { }
public struct RenderPipeline { }
public struct Buffer { }
public struct Texture { }
public struct TextureView { }
public struct Sampler { }
public struct BindGroup { }
public struct CommandEncoder { }
public struct CommandBuffer { }
public struct RenderPassEncoder { }
public struct SwapChain { }
#endregion

#region enum
public enum SType
{
    SurfaceDescriptorFromMetalLayer = 1,
    SurfaceDescriptorFromWindowsHWND = 2,
    SurfaceDescriptorFromXlibWindow = 3,
    SurfaceDescriptorFromCanvasHTMLSelector = 4,
    ShaderModuleWGSLDescriptor = 6,
    SurfaceDescriptorFromWaylandSurface = 8,
}

[Flags]
public enum TextureUsage : uint
{
    None = 0,
    CopySrc = 1,
    CopyDst = 2,
    TextureBinding = 4,
    StorageBinding = 8,
    RenderAttachment = 0x10,
}

[Flags]
public enum BufferUsage : uint
{
    None = 0,
    MapRead = 1,
    MapWrite = 2,
    CopySrc = 4,
    CopyDst = 8,
    Index = 0x10,
    Vertex = 0x20,
    Uniform = 0x40,
    Storage = 0x80,
    Indirect = 0x100,
    QueryResolve = 0x200,
}

[Flags]
public enum ShaderStage : uint { None = 0, Vertex = 1, Fragment = 2, Compute = 4 }

[Flags]
public enum ColorWriteMask : uint { None = 0, Red = 1, Green = 2, Blue = 4, Alpha = 8, All = 0xF }

public enum PresentMode { Fifo = 1, FifoRelaxed = 2, Immediate = 3, Mailbox = 4 }
public enum LoadOp { Undefined = 0, Clear = 1, Load = 2 }
public enum StoreOp { Undefined = 0, Store = 1, Discard = 2 }
public enum ErrorType { NoError = 0, Validation = 1, OutOfMemory = 2, Internal = 3, Unknown = 4, DeviceLost = 5 }
public enum PowerPreference { Undefined = 0, LowPower = 1, HighPerformance = 2 }
public enum BackendType { Undefined = 0, Null = 1, WebGPU = 2, D3D11 = 3, D3D12 = 4, Metal = 5, Vulkan = 6, OpenGL = 7, OpenGLES = 8 }
public enum BufferBindingType { Undefined = 0, Uniform = 1, Storage = 2, ReadOnlyStorage = 3 }
public enum SamplerBindingType { Undefined = 0, Filtering = 1, NonFiltering = 2, Comparison = 3 }
public enum TextureSampleType { Undefined = 0, Float = 1, UnfilterableFloat = 2, Depth = 3, Sint = 4, Uint = 5 }
public enum TextureViewDimension { Undefined = 0, Dimension1D = 1, Dimension2D = 2, Dimension2DArray = 3, Cube = 4, CubeArray = 5, Dimension3D = 6 }
public enum StorageTextureAccess { Undefined = 0, WriteOnly = 1, ReadOnly = 2, ReadWrite = 3 }
public enum VertexFormat { Undefined = 0, Float32x2 = 20, Float32x3 = 21, Float32x4 = 22 }
public enum VertexStepMode { VertexBufferNotUsed = 1, Vertex = 2, Instance = 3 }
public enum BlendOperation { Add = 1, Subtract = 2, ReverseSubtract = 3, Min = 4, Max = 5 }
public enum BlendFactor { Zero = 1, One = 2, Src = 3, OneMinusSrc = 4, SrcAlpha = 5, OneMinusSrcAlpha = 6, Dst = 7, OneMinusDst = 8, DstAlpha = 9, OneMinusDstAlpha = 10, SrcAlphaSaturated = 11, Constant = 12, OneMinusConstant = 13 }
public enum PrimitiveTopology { PointList = 1, LineList = 2, LineStrip = 3, TriangleList = 4, TriangleStrip = 5 }
public enum IndexFormat { Undefined = 0, Uint16 = 1, Uint32 = 2 }
public enum FrontFace { Ccw = 1, CW = 2 }
public enum CullMode { None = 1, Front = 2, Back = 3 }
public enum TextureDimension { Dimension1D = 1, Dimension2D = 2, Dimension3D = 3 }
public enum AddressMode { ClampToEdge = 1, Repeat = 2, MirrorRepeat = 3 }
public enum FilterMode { Nearest = 1, Linear = 2 }
public enum MipmapFilterMode { Nearest = 1, Linear = 2 }
public enum CompareFunction { Undefined = 0, Never = 1, Less = 2, Equal = 3, LessEqual = 4, Greater = 5, NotEqual = 6, GreaterEqual = 7, Always = 8 }
public enum TextureAspect { All = 1, StencilOnly = 2, DepthOnly = 3 }
public enum FeatureName { Undefined = 0, DepthClipControl = 1, Depth32FloatStencil8 = 2, TimestampQuery = 3, TextureCompressionBC = 4, TextureCompressionETC2 = 5, TextureCompressionASTC = 6 }

public enum TextureFormat { Undefined = 0, Rgba8Unorm = 18, Bgra8Unorm = 23, Depth16Unorm = 39, Depth24Plus = 40, Depth24PlusStencil8 = 41, Depth32Float = 42, BC7RgbaUnorm = 56, ETC2Rgba8Unorm = 62, ASTC4x4Unorm = 68 }
public enum StencilOperation { Keep = 1, Zero = 2, Replace = 3, Invert = 4, IncrementClamp = 5, DecrementClamp = 6, IncrementWrap = 7, DecrementWrap = 8 }
#endregion

#region 連鎖構造体・基本
public unsafe struct ChainedStruct
{
    public ChainedStruct* Next;
    public SType SType;
}

public struct Color { public double R, G, B, A; }
public struct Extent3D { public uint Width, Height, DepthOrArrayLayers; }
public struct Origin3D { public uint X, Y, Z; }
#endregion

#region 記述子・状態構造体
public unsafe struct InstanceFeatures
{
    public ChainedStruct* NextInChain;
    public uint TimedWaitAnyEnable;
    public nuint TimedWaitAnyMaxCount;
}

public unsafe struct InstanceDescriptor
{
    public ChainedStruct* NextInChain;
    public InstanceFeatures Features;
}

public unsafe struct SurfaceDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
}

public unsafe struct SurfaceDescriptorFromWindowsHWND
{
    public ChainedStruct Chain;
    public void* Hinstance;
    public void* Hwnd;
}

public unsafe struct SurfaceDescriptorFromMetalLayer
{
    public ChainedStruct Chain;
    public void* Layer;
}

public unsafe struct SurfaceDescriptorFromCanvasHTMLSelector
{
    public ChainedStruct Chain;
    public byte* Selector;
}

public unsafe struct RequestAdapterOptions
{
    public ChainedStruct* NextInChain;
    public Surface* CompatibleSurface;
    public PowerPreference PowerPreference;
    public BackendType BackendType;
    public uint ForceFallbackAdapter;
    public uint CompatibilityMode;
}

public unsafe struct QueueDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
}

public unsafe struct DeviceDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public nuint RequiredFeatureCount;
    public FeatureName* RequiredFeatures;
    public void* RequiredLimits;
    public QueueDescriptor DefaultQueue;
    public void* DeviceLostCallback;
    public void* DeviceLostUserdata;
}

public unsafe struct ShaderModuleWGSLDescriptor
{
    public ChainedStruct Chain;
    public byte* Code;
}

public unsafe struct ShaderModuleDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public nuint HintCount;
    public void* Hints;
}

public unsafe struct BufferBindingLayout
{
    public ChainedStruct* NextInChain;
    public BufferBindingType Type;
    public uint HasDynamicOffset;
    public ulong MinBindingSize;
}

public unsafe struct SamplerBindingLayout
{
    public ChainedStruct* NextInChain;
    public SamplerBindingType Type;
}

public unsafe struct TextureBindingLayout
{
    public ChainedStruct* NextInChain;
    public TextureSampleType SampleType;
    public TextureViewDimension ViewDimension;
    public uint Multisampled;
}

public unsafe struct StorageTextureBindingLayout
{
    public ChainedStruct* NextInChain;
    public StorageTextureAccess Access;
    public TextureFormat Format;
    public TextureViewDimension ViewDimension;
}

public unsafe struct BindGroupLayoutEntry
{
    public ChainedStruct* NextInChain;
    public uint Binding;
    public ShaderStage Visibility;
    public BufferBindingLayout Buffer;
    public SamplerBindingLayout Sampler;
    public TextureBindingLayout Texture;
    public StorageTextureBindingLayout StorageTexture;
}

public unsafe struct BindGroupLayoutDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public nuint EntryCount;
    public BindGroupLayoutEntry* Entries;
}

public unsafe struct PipelineLayoutDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public nuint BindGroupLayoutCount;
    public BindGroupLayout** BindGroupLayouts;
}

public unsafe struct BindGroupEntry
{
    public ChainedStruct* NextInChain;
    public uint Binding;
    public Buffer* Buffer;
    public ulong Offset;
    public ulong Size;
    public Sampler* Sampler;
    public TextureView* TextureView;
}

public unsafe struct BindGroupDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public BindGroupLayout* Layout;
    public nuint EntryCount;
    public BindGroupEntry* Entries;
}

public struct VertexAttribute
{
    public VertexFormat Format;
    public ulong Offset;
    public uint ShaderLocation;
}

public unsafe struct VertexBufferLayout
{
    public ulong ArrayStride;
    public VertexStepMode StepMode;
    public nuint AttributeCount;
    public VertexAttribute* Attributes;
}

public unsafe struct VertexState
{
    public ChainedStruct* NextInChain;
    public ShaderModule* Module;
    public byte* EntryPoint;
    public nuint ConstantCount;
    public void* Constants;
    public nuint BufferCount;
    public VertexBufferLayout* Buffers;
}

public struct BlendComponent
{
    public BlendOperation Operation;
    public BlendFactor SrcFactor;
    public BlendFactor DstFactor;
}

public struct BlendState
{
    public BlendComponent Color;
    public BlendComponent Alpha;
}

public unsafe struct ColorTargetState
{
    public ChainedStruct* NextInChain;
    public TextureFormat Format;
    public BlendState* Blend;
    public ColorWriteMask WriteMask;
}

public unsafe struct FragmentState
{
    public ChainedStruct* NextInChain;
    public ShaderModule* Module;
    public byte* EntryPoint;
    public nuint ConstantCount;
    public void* Constants;
    public nuint TargetCount;
    public ColorTargetState* Targets;
}

public unsafe struct PrimitiveState
{
    public ChainedStruct* NextInChain;
    public PrimitiveTopology Topology;
    public IndexFormat StripIndexFormat;
    public FrontFace FrontFace;
    public CullMode CullMode;
}

public unsafe struct MultisampleState
{
    public ChainedStruct* NextInChain;
    public uint Count;
    public uint Mask;
    public uint AlphaToCoverageEnabled;
}

public struct StencilFaceState
{
    public CompareFunction Compare;
    public StencilOperation FailOp;
    public StencilOperation DepthFailOp;
    public StencilOperation PassOp;
}

public unsafe struct DepthStencilState
{
    public ChainedStruct* NextInChain;
    public TextureFormat Format;
    public uint DepthWriteEnabled;
    public CompareFunction DepthCompare;
    public StencilFaceState StencilFront;
    public StencilFaceState StencilBack;
    public uint StencilReadMask;
    public uint StencilWriteMask;
    public int DepthBias;
    public float DepthBiasSlopeScale;
    public float DepthBiasClamp;
}

public unsafe struct RenderPipelineDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public PipelineLayout* Layout;
    public VertexState Vertex;
    public PrimitiveState Primitive;
    public void* DepthStencil;
    public MultisampleState Multisample;
    public FragmentState* Fragment;
}

public unsafe struct BufferDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public BufferUsage Usage;
    public ulong Size;
    public uint MappedAtCreation;
}

public unsafe struct TextureDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public TextureUsage Usage;
    public TextureDimension Dimension;
    public Extent3D Size;
    public TextureFormat Format;
    public uint MipLevelCount;
    public uint SampleCount;
    public nuint ViewFormatCount;
    public TextureFormat* ViewFormats;
}

public unsafe struct TextureDataLayout
{
    public ChainedStruct* NextInChain;
    public ulong Offset;
    public uint BytesPerRow;
    public uint RowsPerImage;
}

public unsafe struct ImageCopyTexture
{
    public ChainedStruct* NextInChain;
    public Texture* Texture;
    public uint MipLevel;
    public Origin3D Origin;
    public TextureAspect Aspect;
}

public unsafe struct SamplerDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public AddressMode AddressModeU;
    public AddressMode AddressModeV;
    public AddressMode AddressModeW;
    public FilterMode MagFilter;
    public FilterMode MinFilter;
    public MipmapFilterMode MipmapFilter;
    public float LodMinClamp;
    public float LodMaxClamp;
    public CompareFunction Compare;
    public ushort MaxAnisotropy;
}

public unsafe struct CommandEncoderDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
}

public unsafe struct RenderPassColorAttachment
{
    public ChainedStruct* NextInChain;
    public TextureView* View;
    public uint DepthSlice;
    public TextureView* ResolveTarget;
    public LoadOp LoadOp;
    public StoreOp StoreOp;
    public Color ClearValue;
}

public unsafe struct RenderPassDepthStencilAttachment
{
    public TextureView* View;
    public LoadOp DepthLoadOp;
    public StoreOp DepthStoreOp;
    public float DepthClearValue;
    public uint DepthReadOnly;
    public LoadOp StencilLoadOp;
    public StoreOp StencilStoreOp;
    public uint StencilClearValue;
    public uint StencilReadOnly;
}

public unsafe struct RenderPassDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public nuint ColorAttachmentCount;
    public RenderPassColorAttachment* ColorAttachments;
    public void* DepthStencilAttachment;
    public void* OcclusionQuerySet;
    public void* TimestampWrites;
}

public unsafe struct CommandBufferDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
}

public unsafe struct SwapChainDescriptor
{
    public ChainedStruct* NextInChain;
    public byte* Label;
    public TextureUsage Usage;
    public TextureFormat Format;
    public uint Width;
    public uint Height;
    public PresentMode PresentMode;
}
#endregion
