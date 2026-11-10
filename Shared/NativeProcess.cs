using System.Runtime.InteropServices;

namespace ClassDailyLand2.Shared;

/// <summary>
/// 纯 P/Invoke 的进程查询与结束 —— 这里**不用** <c>System.Diagnostics.Process</c>。
///
/// 为什么要这么较真：
///   · <c>Process.GetProcessById(pid)</c> / <c>GetProcessesByName(name)</c> 在进程已退出、
///     访问被拒（高权限/受保护进程）、或句柄失效时抛 <see cref="System.ComponentModel.Win32Exception"/>;
///   · 这些调用常出现在"每秒/每几百毫秒轮询一次"的路径上（等待主程序退出、判断主程序是否在跑），
///     于是一次异常就是日志里的一行，刷屏且掩盖真问题；
///   · <c>Process.Kill()</c> 同样会在权限不足时抛 Win32Exception；
///   · Toolhelp 快照（<c>CreateToolhelp32Snapshot</c>）连进程句柄都不用打开，
///     只要枚举权限，拿到的 <c>szExeFile</c> 就是我们要的文件名。
///
/// 所以这里的方法都**不会抛异常**，失败时返回 false / 0 的保守值，由调用方决定怎么记日志。
/// </summary>
internal static class NativeProcess
{
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>
    /// 等某个 PID 结束。返回 true 表示"它已经不在了"（包括本来就不存在、打不开句柄），
    /// false 表示超时。**不抛异常。**
    /// </summary>
    public static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;

        var handle = OpenProcess(SYNCHRONIZE, false, pid);
        if (handle == IntPtr.Zero)
        {
            // 打不开：已退出，或是不允许等的受保护进程。两种都按"不必再等"处理。
            return true;
        }

        try
        {
            var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            var result = WaitForSingleObject(handle, milliseconds);

            return result == WAIT_OBJECT_0 || result == WAIT_FAILED;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 是否有同名的可执行文件正在运行（只需文件名，例如 <c>Class Daily Land 2.exe</c>）。
    /// 走 Toolhelp 快照，不打开任何进程句柄。**不抛异常。**
    /// </summary>
    public static bool AnyRunning(string exeFileName) => IdsByName(exeFileName).Count > 0;

    /// <summary>结束单个进程；返回是否成功。**不抛异常。**</summary>
    public static bool Terminate(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;

        var handle = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return TerminateProcess(handle, 1);
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 结束所有同名进程，返回成功结束的个数（不包含自己）。
    /// 更新器用它来关掉"还在运行的主程序"，好让替换阶段能独占程序目录里的文件。**不抛异常。**
    /// </summary>
    public static int TerminateByName(string exeFileName)
    {
        var killed = 0;

        foreach (var pid in IdsByName(exeFileName))
        {
            if (Terminate(pid)) killed++;
        }

        return killed;
    }

    /// <summary>按 exe 文件名列出所有匹配的 PID（Toolhelp 快照，无需进程句柄）。</summary>
    private static List<int> IdsByName(string exeFileName)
    {
        var result = new List<int>();

        if (string.IsNullOrWhiteSpace(exeFileName)) return result;

        // 允许传进来带路径的写法
        var name = exeFileName.Contains('\\') || exeFileName.Contains('/')
            ? Path.GetFileName(exeFileName)
            : exeFileName;

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };

            if (!Process32FirstW(snapshot, ref entry)) return result;

            do
            {
                if (string.Equals(entry.szExeFile, name, StringComparison.OrdinalIgnoreCase))
                    result.Add((int)entry.th32ProcessID);

                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch
        {
            // 理论上不会走到这里；真走到了也当作"没找到"
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
