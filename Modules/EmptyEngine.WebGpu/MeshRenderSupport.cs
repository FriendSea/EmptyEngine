using System.Runtime.InteropServices;
using EmptyEngine.Core;
using WgpuBuffer = EmptyEngine.WebGpu.Buffer;

namespace EmptyEngine.WebGpu;

/// <summary>3D メッシュの描画に必要な GPU 資源を作成する</summary>
internal static unsafe class MeshRenderSupport
{
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
