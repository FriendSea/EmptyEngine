namespace EmptyEngine.WebGpu;

/// <summary>desktop の提示（present）担当</summary>
public sealed unsafe class DesktopPresenter : IPresenter
{
    private Instance* _instance;
    private Surface* _surface;
    private SwapChain* _swapChain;
    private Adapter* _adapter;
    private Device* _device;
    private Queue* _queue;
    private TextureFormat _format;
    private int _width;
    private int _height;
    private readonly WebGpuRenderer _renderer = new();

    /// <summary>ウィンドウのハンドルと初期サイズからの WebGPU 一式の構成</summary>
    public DesktopPresenter(nint window, nint hinstance, int width, int height)
    {
        var instanceDescriptor = new InstanceDescriptor();
        _instance = WGPU.wgpuCreateInstance(&instanceDescriptor);

        _surface = CreateSurface(window, hinstance);

        var adapterOptions = new RequestAdapterOptions { CompatibleSurface = _surface };
        _adapter = WGPU.RequestAdapterSync(_instance, &adapterOptions);

        bool requestsCompression = GpuCapabilities.Configure(_adapter, out FeatureName compressionFeature);
        var deviceDescriptor = new DeviceDescriptor
        {
            RequiredFeatureCount = requestsCompression ? 1u : 0u,
            RequiredFeatures = requestsCompression ? &compressionFeature : null,
        };
        _device = WGPU.RequestDeviceSync(_instance, _adapter, &deviceDescriptor);
        WGPU.InstallErrorLogger(_device);

        _queue = WGPU.wgpuDeviceGetQueue(_device);

        Configure(width, height);

        _renderer.Initialize(_device, _queue, _format);
    }

    private Surface* CreateSurface(nint window, nint hinstance)
    {
        if (OperatingSystem.IsWindows())
        {
            var fromHwnd = new SurfaceDescriptorFromWindowsHWND
            {
                Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHWND },
                Hinstance = (void*)hinstance,
                Hwnd = (void*)window,
            };
            var surfaceDescriptor = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromHwnd };
            return WGPU.wgpuInstanceCreateSurface(_instance, &surfaceDescriptor);
        }

        if (OperatingSystem.IsIOS())
        {
            var fromLayer = new SurfaceDescriptorFromMetalLayer
            {
                Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromMetalLayer },
                Layer = (void*)window,
            };
            var surfaceDescriptor = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromLayer };
            return WGPU.wgpuInstanceCreateSurface(_instance, &surfaceDescriptor);
        }

        if (OperatingSystem.IsMacOS())
        {
            var fromLayer = new SurfaceDescriptorFromMetalLayer
            {
                Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromMetalLayer },
                Layer = (void*)MetalLayer.AttachTo(window),
            };
            var surfaceDescriptor = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&fromLayer };
            return WGPU.wgpuInstanceCreateSurface(_instance, &surfaceDescriptor);
        }

        throw new PlatformNotSupportedException(
            "Surface creation is only supported on Windows (HWND) and macOS (Metal); X11/Wayland are not implemented.");
    }

    /// <inheritdoc/>
    public bool CaptureOpaqueTexture
    {
        get => _renderer.CaptureOpaqueTexture;
        set => _renderer.CaptureOpaqueTexture = value;
    }

    /// <summary>フレームバッファのリサイズへの追従による surface の再構成</summary>
    public void Resize(int width, int height) => Configure(width, height);

    private void Configure(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _width = width;
        _height = height;

        _format = TextureFormat.Bgra8Unorm;
        if (_swapChain is not null)
        {
            WGPU.wgpuSwapChainRelease(_swapChain);
        }

        var descriptor = new SwapChainDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Format = _format,
            PresentMode = PresentMode.Fifo,
            Width = (uint)width,
            Height = (uint)height,
        };
        _swapChain = WGPU.wgpuDeviceCreateSwapChain(_device, _surface, &descriptor);
    }

    /// <inheritdoc/>
    public void RenderFrame(double delta, RenderWorld world)
    {
        world.AdvanceTime(delta);
        RenderFrame(world);
    }

    /// <inheritdoc/>
    public void RenderFrame(RenderWorld world)
    {
        if (_swapChain is null)
        {
            return;
        }

        TextureView* view = WGPU.wgpuSwapChainGetCurrentTextureView(_swapChain);
        if (view is null)
        {
            return;
        }

        _renderer.RenderFrame(
            view,
            _width,
            _height,
            new Color { R = 0.1, G = 0.2, B = 0.45, A = 1.0 },
            world);

        WGPU.wgpuSwapChainPresent(_swapChain);

        WGPU.wgpuTextureViewRelease(view);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        if (_swapChain is not null) WGPU.wgpuSwapChainRelease(_swapChain);
        if (_device is not null) WGPU.wgpuDeviceRelease(_device);
        if (_adapter is not null) WGPU.wgpuAdapterRelease(_adapter);
        if (_surface is not null) WGPU.wgpuSurfaceRelease(_surface);
        if (_instance is not null) WGPU.wgpuInstanceRelease(_instance);
    }
}
