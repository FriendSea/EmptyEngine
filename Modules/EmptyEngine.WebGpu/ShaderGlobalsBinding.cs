using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>シェーダが宣言したグローバル値の <c>group(1)</c> への橋渡し</summary>
/// <remarks>
/// 宣言の無いシェーダでは何も作らず、パイプラインレイアウトも bind group も group(0) だけのまま。
/// 自前でパイプラインを組む描画コンポーネントは、<see cref="Build(in RenderContext, ShaderParam[], object?)"/> の
/// レイアウトをパイプラインへ渡し、描画時に <see cref="Bind"/> を呼ぶ。
/// </remarks>
public sealed unsafe class ShaderGlobalsBinding
{
    private ShaderParam[] _declarations = [];
    private float[] _values = [];
    private BindGroupLayout* _layout;
    private WgpuBuffer* _buffer;
    private BindGroup* _bindGroup;
    private ulong _size;
    private int _writtenVersion = -1;

    /// <summary>パイプラインレイアウトへ足す group(1) のレイアウト（宣言が無ければ <c>null</c>）</summary>
    public BindGroupLayout* Layout => _layout;

    /// <summary>アセットのシェーダが宣言した分の buffer と bind group の用意</summary>
    public void Build(in RenderContext context, ShaderAsset? shader, object? poolOwner = null)
        => Build(in context, shader?.Globals ?? [], poolOwner);

    /// <summary>指定した宣言に合わせた buffer と bind group の用意</summary>
    /// <param name="poolOwner">
    /// 解放を <see cref="RenderContext.Resources"/> へ任せる持ち主。<c>null</c> なら <see cref="Release"/> で自分で解放する。
    /// </param>
    /// <remarks>パイプライン生成より前に呼ぶ。</remarks>
    public void Build(in RenderContext context, ShaderParam[] declarations, object? poolOwner = null)
    {
        _declarations = declarations ?? [];
        _writtenVersion = -1;
        if (_declarations.Length == 0)
        {
            _values = [];
            return;
        }

        _size = (ulong)Math.Max(16, ((ShaderParamsHost.ValueCount(_declarations) * sizeof(float)) + 15) & ~15);
        _values = new float[_size / sizeof(float)];

        var layoutEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = _size },
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &layoutEntry };
        _layout = WGPU.wgpuDeviceCreateBindGroupLayout(context.Device, &layoutDescriptor);

        var bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = _size };
        _buffer = WGPU.wgpuDeviceCreateBuffer(context.Device, &bufferDescriptor);

        var entry = new BindGroupEntry { Binding = 0, Buffer = _buffer, Offset = 0, Size = _size };
        var descriptor = new BindGroupDescriptor { Layout = _layout, EntryCount = 1, Entries = &entry };
        _bindGroup = WGPU.wgpuDeviceCreateBindGroup(context.Device, &descriptor);

        if (poolOwner is not null) context.Resources.Track(poolOwner, Release);
    }

    /// <summary>値が変わっていれば書き直したうえでの group(1) への接続</summary>
    /// <remarks>bind group はパイプラインの差し替えで外れるので、SetPipeline のあとに呼ぶ。</remarks>
    public void Bind(in RenderContext context)
    {
        if (_bindGroup is null) return;

        int version = context.Globals.Version;
        if (_writtenVersion != version)
        {
            context.Globals.Pack(_declarations, _values);
            fixed (float* values = _values)
                WGPU.wgpuQueueWriteBuffer(context.Queue, _buffer, 0, values, (nuint)_size);
            _writtenVersion = version;
        }

        WGPU.wgpuRenderPassEncoderSetBindGroup(context.Pass, 1, _bindGroup, 0, null);
    }

    /// <summary>確保した分の解放</summary>
    /// <remarks>pool に任せた分を重ねて呼んでも安全。</remarks>
    public void Release()
    {
        if (_bindGroup is not null) { WGPU.wgpuBindGroupRelease(_bindGroup); _bindGroup = null; }
        if (_buffer is not null) { WGPU.wgpuBufferRelease(_buffer); _buffer = null; }
        if (_layout is not null) { WGPU.wgpuBindGroupLayoutRelease(_layout); _layout = null; }
        _declarations = [];
        _values = [];
        _size = 0;
        _writtenVersion = -1;
    }
}
