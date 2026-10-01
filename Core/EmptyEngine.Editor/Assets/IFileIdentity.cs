using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmptyEngine.Editor.Assets;

/// <summary>移動・リネームで変わらないファイル固有 ID の取得</summary>
/// <remarks>取れなければ <c>null</c> を返してよい</remarks>
public interface IFileIdentity
{
    /// <summary><paramref name="fullPath"/> の固有 ID の取得</summary>
    string? TryGet(string fullPath);
}

/// <summary>移動・リネーム後も同じファイルを識別できる ID を取得する</summary>
/// <remarks>Windows 以外では <c>null</c> を返す。</remarks>
internal sealed class NativeFileIdentity : IFileIdentity
{
    public string? TryGet(string fullPath)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FileIdInfoNative info, Marshal.SizeOf<FileIdInfoNative>()))
                return null;

            return info.VolumeSerialNumber.ToString("x16")
                + ":" + info.FileIdHigh.ToString("x16")
                + info.FileIdLow.ToString("x16");
        }
        catch
        {
            return null;
        }
    }

    private const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoNative
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int infoClass, out FileIdInfoNative info, int size);
}
