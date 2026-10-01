using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmptyEngine.WebGpu;

/// <summary>web の提示（present）担当</summary>
public sealed unsafe class WebPresenter : IPresenter
{
    private static WebPresenter? _self;

    [DllImport("webgpu_dawn")]
    private static extern void ee_canvas_size(int* width, int* height);

    private readonly int _width;
    private readonly int _height;
    private readonly Action<string> _log;

    private Instance* _instance;
    private Surface* _surface;
    private Device* _device;
    private Queue* _queue;
    private SwapChain* _swapChain;
    private WebGpuRenderer? _renderer;
    private bool _captureOpaqueTexture;

    /// <summary>WebGPU の非同期初期化の着火</summary>
    public WebPresenter(Action<string> log)
    {
        _self = this;
        _log = log;

        int width = 0, height = 0;
        ee_canvas_size(&width, &height);
        _width = width > 0 ? width : 960;
        _height = height > 0 ? height : 540;
        _log($"[web] canvas size {_width}x{_height}");

        StartGraphics();
    }

    private void StartGraphics()
    {
        _instance = WGPU.wgpuCreateInstance(null);
        _log($"[web] instance=0x{(nint)_instance:x}");

        nint selector = Marshal.StringToCoTaskMemUTF8("#canvas");
        var fromCanvas = new SurfaceDescriptorFromCanvasHTMLSelector
        {
            Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromCanvasHTMLSelector },
            Selector = (byte*)selector,
        };
        var surfaceDescriptor = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromCanvas };
        _surface = WGPU.wgpuInstanceCreateSurface(_instance, &surfaceDescriptor);
        _log($"[web] surface=0x{(nint)_surface:x}");

        var options = new RequestAdapterOptions { CompatibleSurface = _surface };
        WGPU.wgpuInstanceRequestAdapter(_instance, &options, &OnAdapter, null);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdapter(uint status, Adapter* adapter, byte* message, void* userdata)
    {
        WebPresenter self = _self!;

        if (status != 0 || adapter == null)
        {
            string? msg = message != null ? Marshal.PtrToStringUTF8((nint)message) : null;
            self._log($"[web] Failed to acquire adapter status={status} msg={msg}. " +
                "WebGPU requires a secure context. Open the page via http://localhost or https, not a LAN IP over http.");
            return;
        }
        self._log($"[web] onAdapter status={status}");

        // BC 圧縮に非対応の GPU でも動くよう、対応時だけ機能を要求する。
        bool requestsCompression = GpuCapabilities.Configure(adapter, out FeatureName compressionFeature);
        self._log($"[web] texture compression={GpuCapabilities.PreferredTextureCompression}");

        var deviceDescriptor = new DeviceDescriptor
        {
            RequiredFeatureCount = requestsCompression ? 1u : 0u,
            RequiredFeatures = requestsCompression ? &compressionFeature : null,
        };
        WGPU.wgpuAdapterRequestDevice(adapter, &deviceDescriptor, &OnDevice, null);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDevice(uint status, Device* device, byte* message, void* userdata)
    {
        WebPresenter self = _self!;
        self._log($"[web] onDevice status={status}");

        // デバイス取得失敗の原因を失わないよう、キュー取得前に検査する。
        if (status != 0 || device == null)
        {
            string? msg = message != null ? Marshal.PtrToStringUTF8((nint)message) : null;
            self._log($"[web] Failed to acquire device status={status} msg={msg}");
            return;
        }

        self._device = device;
        self._queue = WGPU.wgpuDeviceGetQueue(device);

        var swapDesc = new SwapChainDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Format = TextureFormat.Bgra8Unorm,
            Width = (uint)self._width,
            Height = (uint)self._height,
            PresentMode = PresentMode.Fifo,
        };
        self._swapChain = WGPU.wgpuDeviceCreateSwapChain(self._device, self._surface, &swapDesc);

        self._renderer = new WebGpuRenderer();
        self._renderer.Initialize(self._device, self._queue, TextureFormat.Bgra8Unorm);
        self._renderer.CaptureOpaqueTexture = self._captureOpaqueTexture;
        self._log($"[web] swapChain=0x{(nint)self._swapChain:x} renderer ready");
    }

    /// <inheritdoc/>
    public bool CaptureOpaqueTexture
    {
        get => _captureOpaqueTexture;
        set
        {
            _captureOpaqueTexture = value;
            if (_renderer is not null) _renderer.CaptureOpaqueTexture = value;
        }
    }

    /// <summary>web でのリサイズ追従（何もしない）</summary>
    public void Resize(int width, int height) { }

    /// <summary>web での後始末（呼ばれない）</summary>
    public void Dispose() { }

    /// <inheritdoc/>
    public void RenderFrame(double delta, RenderWorld world)
    {
        world.AdvanceTime(delta);
        RenderFrame(world);
    }

    /// <inheritdoc/>
    public void RenderFrame(RenderWorld world)
    {
        if (_renderer is null || _swapChain == null)
        {
            return;
        }

        TextureView* view = WGPU.wgpuSwapChainGetCurrentTextureView(_swapChain);
        if (view == null)
        {
            return;
        }

        _renderer.RenderFrame(
            view,
            _width,
            _height,
            new Color { R = 0.1, G = 0.2, B = 0.45, A = 1.0 },
            world);

        WGPU.wgpuTextureViewRelease(view);
    }
}
