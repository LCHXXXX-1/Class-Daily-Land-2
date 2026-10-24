namespace ClassDailyLand.Core.Storage;

/// <summary>
/// 数据根目录：优先程序目录下的 settings\（与规范一致）；
/// 若不可写（例如装在 Program Files），回退 %LOCALAPPDATA%\ClassDailyLand\settings，
/// 并通过 <see cref="FallbackNotice"/> 告知 UI 显示提示。
/// </summary>
public sealed class DataPaths
{
    public string Root { get; }
    public bool UsedFallback { get; }
    public string? FallbackNotice { get; }

    /// <summary>插件目录始终是程序目录下的 plugins\（与数据目录分离）。</summary>
    public string PluginsRoot { get; }

    public string Settings => Path.Combine(Root, "settings.json");
    public string Schedule => Path.Combine(Root, "schedule.json");
    public string Homework => Path.Combine(Root, "homework.json");
    public string Name => Path.Combine(Root, "name.json");
    public string Config => Path.Combine(Root, "config.json");
    public string Agreement => Path.Combine(Root, "agreement.json");
    public string Log => Path.Combine(Root, "app.log");

    public DataPaths(string root, string? pluginsRoot = null, bool usedFallback = false, string? notice = null)
    {
        Root = Path.GetFullPath(root);
        PluginsRoot = pluginsRoot is null ? Path.Combine(Root, "plugins") : Path.GetFullPath(pluginsRoot);
        UsedFallback = usedFallback;
        FallbackNotice = notice;
        try { Directory.CreateDirectory(Root); } catch { }
    }

    /// <summary>直接指定目录（单元测试 / 便携模式）。</summary>
    public static DataPaths CreateAt(string root, string? pluginsRoot = null) => new(root, pluginsRoot);

    public static DataPaths Create(string? baseDirectory = null)
    {
        var programDir = baseDirectory ?? AppContext.BaseDirectory;
        var plugins = Path.Combine(programDir, "plugins");
        var preferred = Path.Combine(programDir, "settings");
        if (EnsureWritable(preferred, out var error))
            return new DataPaths(preferred, plugins);

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassDailyLand", "settings");
        EnsureWritable(fallback, out _);
        var notice = $"程序目录不可写（{error}），数据已改存：{fallback}";
        return new DataPaths(fallback, plugins, true, notice);
    }

    /// <summary>迁移旧数据：若回退目录里空、程序目录里有旧文件，则复制过来。</summary>
    public void AdoptFrom(string candidateRoot)
    {
        if (string.Equals(Path.GetFullPath(candidateRoot), Root, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var f in new[] { "settings.json", "schedule.json", "homework.json", "name.json", "config.json", "agreement.json" })
        {
            var src = Path.Combine(candidateRoot, f);
            var dst = Path.Combine(Root, f);
            if (File.Exists(src) && !File.Exists(dst))
            {
                try { File.Copy(src, dst, overwrite: false); } catch { }
            }
        }
    }

    static bool EnsureWritable(string dir, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
