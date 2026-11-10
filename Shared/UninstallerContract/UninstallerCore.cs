using ClassDailyLand2.Shared;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;

namespace ClassDailyLand2.UninstallerContract;

/// <summary>
/// 每个产品的卸载参数。两个卸载器核心逻辑完全一致，只有这份配置不同 ——
/// 共享源码分别编进两边各自的程序集（与更新契约同一手法），不做工程互引。
/// </summary>
/// <param name="ProductName">产品名（快捷方式与开始菜单文件夹都叫这个）。</param>
/// <param name="AppExeName">主程序 exe 文件名（判断在跑 / 结束进程用，带 .exe）。</param>
/// <param name="UninstallKeyPath">HKCU 卸载条目相对路径 —— 它的 InstallLocation 就是卸载目标。</param>
/// <param name="RunValueName">开机自启的 Run 值名；null = 该产品没有自启（IDE）。</param>
/// <param name="PreserveDirs">「保留数据」时跳过的目录（安装根下第一层）。</param>
/// <param name="PreserveFiles">「保留数据」时跳过的文件（安装根下第一层）。</param>
/// <param name="KeepDataHint">确认框里给用户看的数据说明。</param>
/// <param name="TempFolderName">%Temp% 下的自迁移目录名。</param>
public sealed record UninstallerSpec(
    string ProductName,
    string AppExeName,
    string UninstallKeyPath,
    string? RunValueName,
    string[] PreserveDirs,
    string[] PreserveFiles,
    string KeepDataHint,
    string TempFolderName);

/// <summary>
/// 卸载主流程：
///   第一跳（安装根里的单文件本体）：确认 → 问要不要保留数据 → 从注册表 InstallLocation
///   确定卸载目标 → 把自身复制到 %Temp% 重新拉起（原地删目录会把自己删死，与更新器
///   SelfRelocate 同一手法）→ 退出。
///   第二跳（Temp 副本）：弹进度窗，边干边报 —— 结束进程 → 快捷方式 → 注册表 → 删目录 → 收尾。
///
/// 返回进程退出码：0 = 完成或用户放弃，1 = 有东西没删掉 / 中止。
/// </summary>
public static class UninstallerCore
{
    public static int Run(string[] args, UninstallerSpec spec)
    {
        try
        {
            return args.Length > 0 && string.Equals(args[0], "--relocated", StringComparison.OrdinalIgnoreCase)
                ? RunRelocated(args, spec)
                : RunFrontman(args, spec);
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载过程出错：" + ex.Message, Title(spec),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    static string Title(UninstallerSpec spec) => spec.ProductName + " 卸载程序";

    // ─────────────────────────── 第一跳：确认 + 定目标 + 自我迁移 ───────────────────────────

    static int RunFrontman(string[] args, UninstallerSpec spec)
    {
        var confirm = MessageBox.Show(
            $"确定要卸载 {spec.ProductName} 吗？\n\n卸载会结束程序进程并删除安装目录里的程序文件。",
            Title(spec), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return 0;   // 用户放弃：正常退出

        var keepAnswer = MessageBox.Show(
            $"是否保留个人数据（{spec.KeepDataHint}）？\n\n" +
            "选「是」：这些内容原地保留，其余程序文件全部删除；\n" +
            "选「否」：安装目录整个清空，数据也不要了。",
            Title(spec), MessageBoxButton.YesNo, MessageBoxImage.Question);
        var keepData = keepAnswer == MessageBoxResult.Yes;

        // ★ 卸载目标以安装器写进注册表的 InstallLocation 为准 —— 卸载器可以被拷到任何地方运行，
        //   "清哪个目录"由注册表说了算，不是 exe 所在目录。注册表没有（条目被手工删过）才退回
        //   exe 所在目录（标准落位就在安装根）。
        var installDir = ResolveInstallLocation(spec)
                         ?? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        // 护栏：目标在 %Temp% 里 = 拿 Temp 副本裸跑（异常场景），绝不能当安装目录清空
        var temp = Path.GetTempPath();
        if (installDir.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("检测到的安装目录在临时目录里，已中止（注册表条目可能被人动过）。",
                Title(spec), MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        // 自我迁移：单文件卸载器只需拷这一个 exe；从 Temp 副本重新拉起干脏活
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
        {
            MessageBox.Show("找不到卸载器自身的路径，无法继续。", Title(spec),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        var tempDir = Path.Combine(temp, spec.TempFolderName);
        Directory.CreateDirectory(tempDir);
        var tempExe = Path.Combine(tempDir, Path.GetFileName(self));
        File.Copy(self, tempExe, overwrite: true);

        var psi = new ProcessStartInfo
        {
            FileName = tempExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--relocated");
        psi.ArgumentList.Add("--install-dir");
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add(keepData ? "--keep-data" : "--purge");

        Process.Start(psi)?.Dispose();
        return 0;
    }

    /// <summary>
    /// 从安装器写的卸载条目读 InstallLocation。只认"存在且不是盘根"的值 ——
    /// 盘根（C:\）宁可不认退回 exe 目录，也绝不拿去当删除目标。
    /// </summary>
    static string? ResolveInstallLocation(UninstallerSpec spec)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(spec.UninstallKeyPath);
            var raw = (key?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var full = Path.GetFullPath(raw);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                return null;

            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;   // 读不到就退回 exe 所在目录
        }
    }

    // ─────────────────────────── 第二跳：进度窗 + 真正动手 ───────────────────────────

    static int RunRelocated(string[] args, UninstallerSpec spec)
    {
        var installDir = ParseArg(args, "--install-dir");
        var keepData = args.Any(a => string.Equals(a, "--keep-data", StringComparison.OrdinalIgnoreCase));

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new UninstallProgressWindow(spec.ProductName);
        app.MainWindow = window;

        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                exitCode = await DoUninstallAsync(window, installDir, keepData, spec);
            }
            catch (Exception ex)
            {
                window.Fail("卸载过程出错：" + ex.Message);
                exitCode = 1;
            }
        };
        window.Finished += () => app.Shutdown();

        app.Run(window);
        return exitCode;
    }

    static async Task<int> DoUninstallAsync(
        UninstallProgressWindow window, string? installDir, bool keepData, UninstallerSpec spec)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            window.Fail("没找到安装目录，卸载中止：" + (installDir ?? "(空)"));
            return 1;
        }

        // ① 主程序必须不在：exe/dll 被映像锁着，删了也是半途残骸
        window.Report("检查主程序进程…", 2);
        if (NativeProcess.AnyRunning(spec.AppExeName))
        {
            var ask = MessageBox.Show(window,
                $"{spec.ProductName} 正在运行。\n\n卸载需要先退出它。" +
                "选「是」由卸载器直接结束它的进程；选「否」取消本次卸载。",
                Title(spec), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes)
            {
                window.Fail("已取消：请先退出程序，再重新运行卸载。");
                return 0;
            }

            NativeProcess.TerminateByName(spec.AppExeName);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (NativeProcess.AnyRunning(spec.AppExeName) && DateTime.UtcNow < deadline)
            {
                window.Report("等待进程退出…", 8);
                await Task.Delay(500);
            }

            if (NativeProcess.AnyRunning(spec.AppExeName))
            {
                window.Fail("进程没能结束，文件还被占用，卸载中止。请手动退出后重试。");
                return 1;
            }

            window.Report("等待文件句柄释放…", 10);
            await Task.Delay(1000);   // 进程没了不等于句柄放干净了（更新器同款余量）
        }

        var errors = new List<string>();

        // ② 快捷方式：桌面 .lnk + 开始菜单产品文件夹（尽力删，失败记账不拦路）
        window.Report("删除快捷方式…", 15);
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            spec.ProductName + ".lnk"), errors);
        TryDeleteTree(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            spec.ProductName), errors);

        // ③ 卸载注册表条目（整树删）+ 开机自启值（只删自己那一个值，键本身不动）
        window.Report("清理注册表…", 25);
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(spec.UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            errors.Add("卸载注册表条目：" + ex.Message);
        }

        if (spec.RunValueName is { Length: > 0 } runValue)
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                runKey?.DeleteValue(runValue, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                errors.Add("开机自启项：" + ex.Message);
            }
        }

        // ④ 安装目录（大头：30~90%）
        window.Report(keepData ? "删除程序文件（保留个人数据）…" : "删除安装目录…", 30);
        await Task.Run(() => DeleteInstallDir(installDir, keepData, spec, errors,
            (done, total) => window.Report(
                $"删除中 {done} / {total} 项…",
                30 + 60.0 * done / Math.Max(1, total))));

        // ⑤ 收尾：报结果 + 安排删掉 Temp 里的自己
        window.Report("收尾…", 95);
        ScheduleSelfDelete();

        if (errors.Count == 0)
        {
            window.Done(keepData
                ? $"已卸载 {spec.ProductName}。\n个人数据已保留：{installDir}"
                : $"已卸载 {spec.ProductName}。");
            return 0;
        }

        window.Fail($"卸载完成，但有 {errors.Count} 项没能删掉：\n"
                    + string.Join("\n", errors.Take(6))
                    + (errors.Count > 6 ? "\n…" : ""));
        return 1;
    }

    // ─────────────────────────── 删除细节 ───────────────────────────

    static void DeleteInstallDir(string installDir, bool keepData, UninstallerSpec spec,
        List<string> errors, Action<int, int>? progress = null)
    {
        if (!keepData)
        {
            try
            {
                DeleteTree(installDir);
                progress?.Invoke(1, 1);
                return;
            }
            catch (Exception ex)
            {
                errors.Add("安装目录整体删除失败：" + ex.Message);
            }
            // 整删没成（半路撞上占用）→ 退回逐条删，锁着的记下来
        }

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(installDir); }
        catch (Exception ex) { errors.Add("安装目录读不了：" + ex.Message); return; }

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var name = Path.GetFileName(entry);
            progress?.Invoke(i, entries.Length);

            if (keepData && IsPreserved(name, spec)) continue;

            try
            {
                if (Directory.Exists(entry)) DeleteTree(entry);
                else DeleteOneFile(entry);
            }
            catch (Exception ex)
            {
                errors.Add(name + "：" + ex.Message);
            }
        }
        progress?.Invoke(entries.Length, entries.Length);

        // 全清模式下目录壳也顺手删（保留数据模式下它装着用户数据，必须留着）
        if (!keepData)
        {
            try { Directory.Delete(installDir); }
            catch { /* 里面还剩残骸就留着，错误已经记过账 */ }
        }
    }

    static bool IsPreserved(string rootEntryName, UninstallerSpec spec) =>
        spec.PreserveDirs.Contains(rootEntryName, StringComparer.OrdinalIgnoreCase)
        || spec.PreserveFiles.Contains(rootEntryName, StringComparer.OrdinalIgnoreCase);

    static void DeleteTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            DeleteOneFile(file);

        foreach (var sub in Directory.EnumerateDirectories(dir))
            DeleteTree(sub);

        Directory.Delete(dir);
    }

    static void DeleteOneFile(string path)
    {
        // 只读文件先恢复属性，否则 Delete 会被拒（与更新器同一处理）
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    static void TryDeleteFile(string path, List<string> errors)
    {
        try { if (File.Exists(path)) DeleteOneFile(path); }
        catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
    }

    static void TryDeleteTree(string path, List<string> errors)
    {
        try { if (Directory.Exists(path)) DeleteTree(path); }
        catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
    }

    /// <summary>
    /// 借一条脱离本进程生命的 cmd 延迟删 %Temp% 副本：ping 大约 3 秒，够本进程退出，
    /// 之后 rd 把临时目录整个端走（更新器 Cleanup 同款思路）。删不掉也无所谓 —— 那是 %Temp%。
    /// </summary>
    static void ScheduleSelfDelete()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 4 127.0.0.1 >nul & rd /s /q \"{dir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch
        {
            // 临时副本留在 %Temp% 里不影响任何事
        }
    }

    static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
