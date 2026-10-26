using System.Runtime.InteropServices;
using System.Text;

namespace ClassDailyLand.Installer.Offline.Services;

/// <summary>
/// 创建桌面与开始菜单快捷方式（COM 互操作 IShellLinkW + IPersistFile）。
///
/// 不用 WScript.Shell 的 late binding：那要 dynamic + ProgID，出错时只有运行时才知道，
/// 而且 32/64 位注册表重定向下时灵时不灵。IShellLink 是系统自带的稳定接口。
/// </summary>
internal static class ShortcutService
{
    public static void CreateAll(string installDir, string appExePath)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CreateShortcut(
            Path.Combine(desktop, SetupEndpoints.DisplayName + ".lnk"),
            appExePath, installDir, SetupEndpoints.DisplayName);

        // 开始菜单：一个以产品名命名的文件夹，主程序与卸载入口都放里面
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var startMenuFolder = Path.Combine(programs, SetupEndpoints.DisplayName);
        Directory.CreateDirectory(startMenuFolder);

        CreateShortcut(
            Path.Combine(startMenuFolder, SetupEndpoints.DisplayName + ".lnk"),
            appExePath, installDir, SetupEndpoints.DisplayName);

        // 卸载快捷方式：卸载器不存在就跳过，宁可少一个入口，也不造一个点了没反应的快捷方式
        var uninstaller = Path.Combine(installDir, SetupEndpoints.UninstallerExeName);
        if (File.Exists(uninstaller))
        {
            CreateShortcut(
                Path.Combine(startMenuFolder, "卸载 " + SetupEndpoints.DisplayName + ".lnk"),
                uninstaller, installDir, "卸载 " + SetupEndpoints.DisplayName);
        }
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workingDirectory, string description)
    {
        object? shellLink = null;

        try
        {
            // 用 CLSID 创建 ShellLink 对象（等价于 CoCreateInstance）：
            // 不写 [ComImport] coclass + new，是为了避开"导入的 coclass 没有构造函数"那类编译/运行差异。
            Type coclass;
            try
            {
                coclass = Type.GetTypeFromCLSID(ShellLinkClsid);
            }
            catch (Exception ex)
            {
                throw new InstallFailedException("系统缺少 Shell Link 组件，无法创建快捷方式：" + ex.Message, ex);
            }

            var created = Activator.CreateInstance(coclass)
                          ?? throw new InstallFailedException("无法创建 Shell Link 对象。");

            shellLink = created;                // 留给 finally 释放 RCW
            var link = (IShellLinkW)created;    // 从非空局部变量转，避免 CS8600

            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);

            var hr = ((IPersistFile)link).Save(linkPath, true);
            if (hr != 0) throw new InstallFailedException($"保存快捷方式失败（HRESULT 0x{hr:X8}）：{linkPath}");
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
                Marshal.ReleaseComObject(shellLink);
        }
    }

    // ShellLink 的 CLSID（{00021401-0000-0000-C000-000000000046}）
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    // ————————————————————— COM 契约 —————————————————————

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                               [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
