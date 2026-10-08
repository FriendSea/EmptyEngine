using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EmptyEngine.Host;

/// <summary>Host が死んだら子プロセスのツリーも OS に落とさせる Windows のジョブ</summary>
/// <remarks>
/// Host は端末ごと強制終了されることがあり（VSCode がターミナルを閉じるとき）、その場合は後始末のコードが一切走らない。
/// ジョブのハンドルは Host だけが持つので、Host がどう死んでもハンドルが閉じてジョブの中身が丸ごと終了する。
/// Windows 専用（ここで縛れなかった子は <see cref="ChildProcessLedger"/> が次回起動時に片付ける）。
/// </remarks>
internal sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint UseStdHandles = 0x00000100;

    private readonly SafeFileHandle _job;

    public ChildProcessJob()
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        _job = job;
    }

    /// <summary>ジョブを閉じる。中に残っているプロセスは OS が落とす</summary>
    public void Dispose() => _job.Dispose();

    /// <summary>子プロセスをジョブに入れた状態で起動する</summary>
    /// <remarks>
    /// 起動してからジョブへ入れると、その間に子が起こした孫がジョブから漏れる。
    /// 止めたまま作ってジョブへ入れ、それから走らせることで、子が何かを起こす前に所属を済ませる。
    /// 標準入出力は常にパイプへ繋ぎ、ウィンドウは作らない。
    /// </remarks>
    /// <returns>起動した子と、その標準出力・標準エラー。</returns>
    public (Process Process, StreamReader Output, StreamReader Error) Start(ProcessStartInfo startInfo)
    {
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = UseStdHandles,
                StdInput = stdin.ClientSafePipeHandle.DangerousGetHandle(),
                StdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                StdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };

            char[] commandLine = (BuildCommandLine(startInfo) + '\0').ToCharArray();
            string? workingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;
            if (!CreateProcessW(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                    CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow,
                    BuildEnvironmentBlock(startInfo.Environment), workingDirectory, ref startup, out ProcessInformation created))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            Process process;
            try
            {
                if (!AssignProcessToJobObject(_job, created.Process))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                process = Process.GetProcessById(created.ProcessId);
                // 終了コードを後から読めるよう、生きているうちにハンドルを握らせておく。
                _ = process.SafeHandle;

                if (ResumeThread(created.Thread) == uint.MaxValue)
                {
                    int error = Marshal.GetLastPInvokeError();
                    process.Dispose();
                    throw new Win32Exception(error);
                }
            }
            catch
            {
                TerminateProcess(created.Process, 1);
                throw;
            }
            finally
            {
                CloseHandle(created.Thread);
                CloseHandle(created.Process);
            }

            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();

            // 子の標準入力は、子を手放すまで開けたままにする（閉じると子が入力の終わりを読む）。
            process.Disposed += (_, _) => stdin.Dispose();

            Encoding encoding = startInfo.StandardOutputEncoding ?? Encoding.UTF8;
            return (
                process,
                new StreamReader(stdout, encoding),
                new StreamReader(stderr, startInfo.StandardErrorEncoding ?? encoding));
        }
        catch
        {
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        string fileName = startInfo.FileName.Trim();
        bool quoted = fileName.StartsWith('"') && fileName.EndsWith('"');
        string command = quoted ? fileName : '"' + fileName + '"';
        return string.IsNullOrEmpty(startInfo.Arguments) ? command : command + ' ' + startInfo.Arguments;
    }

    /// <summary>「名前=値」を NUL 区切りで名前順に並べ、NUL で閉じた環境ブロック</summary>
    private static char[] BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var block = new StringBuilder();
        foreach ((string key, string? value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(key).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return block.ToString().ToCharArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        [In] char[] environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
