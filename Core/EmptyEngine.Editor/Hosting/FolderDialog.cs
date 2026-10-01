using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace EmptyEngine.Editor.Hosting;

/// <summary>OS のフォルダ選択ダイアログ</summary>
internal static class FolderDialog
{
    /// <summary>この機械で出せるか</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || FindUnixTool() is not null;

    /// <summary>フォルダを選ばせる（取り消し・出せない機械では null）</summary>
    /// <param name="startPath">最初に見せる場所。無い場所なら在る親まで遡る。</param>
    public static Task<string?> PickAsync(string title, string startPath, ILogger logger)
    {
        // 選択元のウィンドウを親にするため、UI スレッドを離れる前に取得する。
        nint owner = OperatingSystem.IsWindows() ? Windows.GetForegroundWindow() : 0;
        string start = ExistingAncestor(startPath);

        return Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows()) return Windows.Pick(title, start, owner);
                if (OperatingSystem.IsMacOS()) return PickWithMac(title, start);

                return FindUnixTool() is { } tool ? PickWithUnixTool(tool, title, start) : null;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Folder dialog failed: {Error}", ex.Message);
                return null;
            }
        });
    }

    /// <summary>在るところまで遡った場所（ダイアログに渡せるのは実在するフォルダだけ）</summary>
    private static string ExistingAncestor(string path)
    {
        string? current = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        while (current is { Length: > 0 } && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);

        return current ?? string.Empty;
    }

    [SupportedOSPlatform("macos")]
    private static string? PickWithMac(string title, string start)
    {
        string location = start.Length == 0 ? string.Empty : $" default location POSIX file \"{start}\"";
        string script = $"POSIX path of (choose folder with prompt \"{title}\"{location})";
        return RunTool("osascript", ["-e", script]);
    }

    private static string? PickWithUnixTool(string tool, string title, string start) => tool switch
    {
        "zenity" => RunTool(tool, ["--file-selection", "--directory", $"--title={title}", $"--filename={start}/"]),
        _ => RunTool(tool, ["--getexistingdirectory", start.Length == 0 ? "." : start]),
    };

    /// <summary>ダイアログを出す道具（PATH に在るもの）</summary>
    private static string? FindUnixTool()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return null;

        string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string tool in (string[])["zenity", "kdialog"])
        {
            if (paths.Any(dir => File.Exists(Path.Combine(dir, tool)))) return tool;
        }

        return null;
    }

    /// <summary>道具の起動と 1 行の受け取り（取り消しは 0 以外で返る）</summary>
    private static string? RunTool(string fileName, string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments) psi.ArgumentList.Add(argument);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) return null;

        string picked = output.Trim();
        return picked.Length == 0 ? null : Path.TrimEndingDirectorySeparator(picked);
    }

    /// <summary>Windows のシェルのフォルダ選択（IFileOpenDialog）</summary>
    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const uint PickFolders = 0x20;
        private const uint ForceFileSystem = 0x40;
        private const uint PathMustExist = 0x800;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

        /// <summary>COM のダイアログは STA でしか出せないので、その 1 本だけ立てて待つ</summary>
        public static string? Pick(string title, string start, nint owner)
        {
            string? picked = null;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try { picked = Show(title, start, owner); }
                catch (Exception ex) { failure = ex; }
            })
            {
                IsBackground = true,
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            return failure is null ? picked : throw failure;
        }

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string path, nint bindContext, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

        private static string? Show(string title, string start, nint owner)
        {
            Type type = Type.GetTypeFromCLSID(FileOpenDialogClsid)
                ?? throw new InvalidOperationException("CLSID_FileOpenDialog is not registered.");
            var dialog = (IFileOpenDialog)Activator.CreateInstance(type)!;
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
                dialog.SetTitle(title);

                if (start.Length > 0)
                {
                    SHCreateItemFromParsingName(start, 0, typeof(IShellItem).GUID, out object folder);
                    dialog.SetFolder((IShellItem)folder);
                }

                int hr = dialog.Show(owner);
                if (hr == Cancelled) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out IShellItem item);
                item.GetDisplayName(FileSystemPath, out string path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        /// <summary>Windows のファイル選択ダイアログを操作する COM インターフェイス</summary>
        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(nint parent);

            void SetFileTypes(uint count, nint filterSpec);
            void SetFileTypeIndex(uint fileType);
            void GetFileTypeIndex(out uint fileType);
            void Advise(nint events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
