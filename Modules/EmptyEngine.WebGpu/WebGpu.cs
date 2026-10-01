using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EmptyEngine.WebGpu;

/// <summary>自前 webgpu.h バインディング（P/Invoke 本体）</summary>
public static unsafe class WGPU
{
#if IOS
    // iOS の静的リンク先は動的解決できないため、__Internal で解決する。
    private const string Lib = "__Internal";
#else
    private const string Lib = "webgpu_dawn";
#endif

    [DllImport(Lib)] public static extern Instance* wgpuCreateInstance(InstanceDescriptor* descriptor);
    [DllImport(Lib)] public static extern Surface* wgpuInstanceCreateSurface(Instance* instance, SurfaceDescriptor* descriptor);

    [DllImport(Lib)]
    public static extern void wgpuInstanceRequestAdapter(
        Instance* instance, RequestAdapterOptions* options,
        delegate* unmanaged[Cdecl]<uint, Adapter*, byte*, void*, void> callback, void* userdata);

    [DllImport(Lib)]
    public static extern void wgpuAdapterRequestDevice(
        Adapter* adapter, DeviceDescriptor* descriptor,
        delegate* unmanaged[Cdecl]<uint, Device*, byte*, void*, void> callback, void* userdata);

    /// <remarks>返るのは WGPUBool（uint32_t）。0 が「その機能は無い」。</remarks>
    [DllImport(Lib)] public static extern uint wgpuAdapterHasFeature(Adapter* adapter, FeatureName feature);

    [DllImport(Lib)] public static extern Queue* wgpuDeviceGetQueue(Device* device);

    /// <remarks>ブラウザでは呼び出せない。</remarks>
    [DllImport(Lib)] public static extern void wgpuInstanceProcessEvents(Instance* instance);

    [DllImport(Lib)]
    public static extern void wgpuDeviceSetUncapturedErrorCallback(
        Device* device,
        delegate* unmanaged[Cdecl]<ErrorType, byte*, void*, void> callback, void* userdata);

    [DllImport(Lib)] public static extern SwapChain* wgpuDeviceCreateSwapChain(Device* device, Surface* surface, SwapChainDescriptor* descriptor);
    [DllImport(Lib)] public static extern TextureView* wgpuSwapChainGetCurrentTextureView(SwapChain* swapChain);
    [DllImport(Lib)] public static extern void wgpuSwapChainPresent(SwapChain* swapChain);

    [DllImport(Lib)] public static extern ShaderModule* wgpuDeviceCreateShaderModule(Device* device, ShaderModuleDescriptor* descriptor);
    [DllImport(Lib)] public static extern BindGroupLayout* wgpuDeviceCreateBindGroupLayout(Device* device, BindGroupLayoutDescriptor* descriptor);
    [DllImport(Lib)] public static extern PipelineLayout* wgpuDeviceCreatePipelineLayout(Device* device, PipelineLayoutDescriptor* descriptor);
    [DllImport(Lib)] public static extern RenderPipeline* wgpuDeviceCreateRenderPipeline(Device* device, RenderPipelineDescriptor* descriptor);
    [DllImport(Lib)] public static extern Buffer* wgpuDeviceCreateBuffer(Device* device, BufferDescriptor* descriptor);
    [DllImport(Lib)] public static extern Texture* wgpuDeviceCreateTexture(Device* device, TextureDescriptor* descriptor);
    [DllImport(Lib)] public static extern Sampler* wgpuDeviceCreateSampler(Device* device, SamplerDescriptor* descriptor);
    [DllImport(Lib)] public static extern BindGroup* wgpuDeviceCreateBindGroup(Device* device, BindGroupDescriptor* descriptor);
    [DllImport(Lib)] public static extern CommandEncoder* wgpuDeviceCreateCommandEncoder(Device* device, CommandEncoderDescriptor* descriptor);

    [DllImport(Lib)] public static extern TextureView* wgpuTextureCreateView(Texture* texture, void* descriptor);

    [DllImport(Lib)] public static extern void wgpuQueueWriteBuffer(Queue* queue, Buffer* buffer, ulong bufferOffset, void* data, nuint size);
    [DllImport(Lib)] public static extern void wgpuQueueWriteTexture(Queue* queue, ImageCopyTexture* destination, void* data, nuint dataSize, TextureDataLayout* dataLayout, Extent3D* writeSize);
    [DllImport(Lib)] public static extern void wgpuQueueSubmit(Queue* queue, nuint commandCount, CommandBuffer** commands);

    [DllImport(Lib)] public static extern RenderPassEncoder* wgpuCommandEncoderBeginRenderPass(CommandEncoder* encoder, RenderPassDescriptor* descriptor);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderSetPipeline(RenderPassEncoder* pass, RenderPipeline* pipeline);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderSetBindGroup(RenderPassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderSetVertexBuffer(RenderPassEncoder* pass, uint slot, Buffer* buffer, ulong offset, ulong size);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderSetIndexBuffer(RenderPassEncoder* pass, Buffer* buffer, IndexFormat format, ulong offset, ulong size);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderDraw(RenderPassEncoder* pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderDrawIndexed(RenderPassEncoder* pass, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderEnd(RenderPassEncoder* pass);

    [DllImport(Lib)] public static extern CommandBuffer* wgpuCommandEncoderFinish(CommandEncoder* encoder, CommandBufferDescriptor* descriptor);

    [DllImport(Lib)] public static extern void wgpuShaderModuleRelease(ShaderModule* shaderModule);
    [DllImport(Lib)] public static extern void wgpuRenderPassEncoderRelease(RenderPassEncoder* pass);
    [DllImport(Lib)] public static extern void wgpuCommandBufferRelease(CommandBuffer* commandBuffer);
    [DllImport(Lib)] public static extern void wgpuCommandEncoderRelease(CommandEncoder* encoder);
    [DllImport(Lib)] public static extern void wgpuTextureViewRelease(TextureView* view);
    [DllImport(Lib)] public static extern void wgpuSwapChainRelease(SwapChain* swapChain);
    [DllImport(Lib)] public static extern void wgpuTextureRelease(Texture* texture);
    [DllImport(Lib)] public static extern void wgpuSamplerRelease(Sampler* sampler);
    [DllImport(Lib)] public static extern void wgpuBufferRelease(Buffer* buffer);
    [DllImport(Lib)] public static extern void wgpuBindGroupRelease(BindGroup* bindGroup);
    [DllImport(Lib)] public static extern void wgpuRenderPipelineRelease(RenderPipeline* pipeline);
    [DllImport(Lib)] public static extern void wgpuPipelineLayoutRelease(PipelineLayout* layout);
    [DllImport(Lib)] public static extern void wgpuBindGroupLayoutRelease(BindGroupLayout* layout);
    [DllImport(Lib)] public static extern void wgpuDeviceRelease(Device* device);
    [DllImport(Lib)] public static extern void wgpuAdapterRelease(Adapter* adapter);
    [DllImport(Lib)] public static extern void wgpuSurfaceRelease(Surface* surface);
    [DllImport(Lib)] public static extern void wgpuInstanceRelease(Instance* instance);

    private unsafe struct RequestState
    {
        public void* Result;
        public uint Status;
        public int Completed;
    }

    public static Adapter* RequestAdapterSync(Instance* instance, RequestAdapterOptions* options)
    {
        var state = new RequestState { Status = uint.MaxValue };
        wgpuInstanceRequestAdapter(instance, options, &OnAdapterSync, &state);
        WaitForRequest(instance, &state, "adapter");
        return (Adapter*)state.Result;
    }

    public static Device* RequestDeviceSync(Instance* instance, Adapter* adapter, DeviceDescriptor* descriptor)
    {
        var state = new RequestState { Status = uint.MaxValue };
        wgpuAdapterRequestDevice(adapter, descriptor, &OnDeviceSync, &state);
        WaitForRequest(instance, &state, "device");
        return (Device*)state.Result;
    }

    private static void WaitForRequest(Instance* instance, RequestState* state, string kind)
    {
        long deadline = Environment.TickCount64 + 15_000;
        while (Volatile.Read(ref state->Completed) == 0)
        {
            wgpuInstanceProcessEvents(instance);
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"WebGPU {kind} request did not complete within 15 seconds.");
            Thread.Yield();
        }

        if (state->Status != 0 || state->Result is null)
            throw new InvalidOperationException($"WebGPU {kind} request failed (status {state->Status}).");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdapterSync(uint status, Adapter* adapter, byte* message, void* userdata)
        => CompleteRequest((RequestState*)userdata, status, adapter, message, "adapter");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDeviceSync(uint status, Device* device, byte* message, void* userdata)
        => CompleteRequest((RequestState*)userdata, status, device, message, "device");

    private static void CompleteRequest(RequestState* state, uint status, void* result, byte* message, string kind)
    {
        state->Status = status;
        state->Result = result;
        if (status != 0)
            Console.Error.WriteLine($"[wgpu] {kind} request: {Marshal.PtrToStringUTF8((nint)message) ?? "unknown error"}");
        Volatile.Write(ref state->Completed, 1);
    }

    /// <summary>捕捉されなかったデバイスエラーの標準エラー出力への素通し</summary>
    public static void InstallErrorLogger(Device* device)
        => wgpuDeviceSetUncapturedErrorCallback(device, &OnUncapturedError, null);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnUncapturedError(ErrorType type, byte* message, void* userdata)
        => Console.Error.WriteLine($"[wgpu] {type}: {Marshal.PtrToStringUTF8((nint)message)}");
}
