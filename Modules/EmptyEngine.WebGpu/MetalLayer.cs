using System.Runtime.InteropServices;

namespace EmptyEngine.WebGpu;

/// <summary>macOS で WebGPU が描き込める CAMetalLayer の用意</summary>
/// <remarks>macOS でのみ呼び出せる。</remarks>
internal static class MetalLayer
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendGet(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern double SendGetDouble(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSet(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSetBool(nint receiver, nint selector, byte argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSetDouble(nint receiver, nint selector, double argument);

    /// <summary>NSWindow の contentView への CAMetalLayer の差し込み</summary>
    public static nint AttachTo(nint nsWindow)
    {
        nint layer = SendGet(GetClass("CAMetalLayer"), Selector("layer"));
        nint contentView = SendGet(nsWindow, Selector("contentView"));

        SendSetDouble(layer, Selector("setContentsScale:"), SendGetDouble(nsWindow, Selector("backingScaleFactor")));

        SendSet(contentView, Selector("setLayer:"), layer);
        SendSetBool(contentView, Selector("setWantsLayer:"), 1);

        return layer;
    }
}
