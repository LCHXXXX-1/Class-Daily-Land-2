using ClassDailyLand2.Core.Plugins;
using ClassDailyLand2.Plugin;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 插件系统的纯逻辑部分：清单校验、目录扫描、启停状态、安装安全性、显示位竞争。
/// 这些都不加载真实程序集，所以能当普通单元测试跑。
/// </summary>
public sealed class PluginSystemTests : IDisposable
{
    readonly string _root;

    public PluginSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdl-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清不掉不影响断言 */ }
    }

    // ————————————————————— 工具 —————————————————————

    string MakePluginDir(string folder, string id = "hello", string entry = "Hello.dll", int api = 1)
    {
        var dir = Path.Combine(_root, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, entry), "not a real assembly");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
        {
          "id": "{{id}}",
          "name": "测试插件",
          "version": "1.2.3",
          "description": "用于测试",
          "author": "tester",
          "entry": "{{entry}}",
          "type": "Test.Plugin",
          "apiVersion": {{api}}
        }
        """);
        return dir;
    }

    static string ValidJson(string id = "hello", string entry = "Hello.dll", int api = 1) => $$"""
    {
      "id": "{{id}}",
      "name": "测试插件",
      "version": "1.2.3",
      "entry": "{{entry}}",
      "type": "Test.Plugin",
      "apiVersion": {{api}}
    }
    """;

    // ————————————————————— 清单 —————————————————————

    [Fact]
    public void Manifest_ParsesValidJson()
    {
        var ok = PluginManifest.TryParse(ValidJson(), "C:/p", out var m, out var error);

        Assert.True(ok, error);
        Assert.Equal("hello", m.Id);
        Assert.Equal("测试插件", m.Name);
        Assert.Equal("1.2.3", m.Version);
        Assert.Equal("Hello.dll", m.Entry);
        Assert.Equal("Test.Plugin", m.Type);
        Assert.Equal(1, m.ApiVersion);
    }

    [Fact]
    public void Manifest_FillsDefaults()
    {
        var ok = PluginManifest.TryParse("""{ "id": "x", "entry": "A.dll", "type": "T" }""",
            "C:/p", out var m, out var error);

        Assert.True(ok, error);
        Assert.Equal("x", m.Name);         // 名字缺省用 id
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal(PluginManifest.HostApiVersion, m.ApiVersion);
    }

    [Theory]
    [InlineData("""{ "entry": "A.dll", "type": "T" }""", "id")]                        // 缺 id
    [InlineData("""{ "id": "a/b", "entry": "A.dll", "type": "T" }""", "不合法")]        // id 带路径
    [InlineData("""{ "id": "x", "type": "T" }""", "entry")]                            // 缺 entry
    [InlineData("""{ "id": "x", "entry": "../evil.dll", "type": "T" }""", "不合法")]    // entry 穿越
    [InlineData("""{ "id": "x", "entry": "A.dll" }""", "type")]                        // 缺 type
    [InlineData("""{ "id": "x", "entry": "A.exe", "type": "T" }""", ".dll")]           // 不是 dll
    public void Manifest_RejectsBadInput(string json, string expectedFragment)
    {
        var ok = PluginManifest.TryParse(json, "C:/p", out _, out var error);

        Assert.False(ok);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_RejectsNewerApiVersion()
    {
        var ok = PluginManifest.TryParse(ValidJson(api: PluginManifest.HostApiVersion + 1), "C:/p",
            out _, out var error);

        Assert.False(ok);
        Assert.Contains($"v{PluginManifest.HostApiVersion}", error);
    }

    [Fact]
    public void Manifest_TryLoad_ReportsMissingFiles()
    {
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);

        Assert.False(PluginManifest.TryLoad(dir, out _, out var error));
        Assert.Contains("plugin.json", error);

        // 有清单但没有入口程序集
        File.WriteAllText(Path.Combine(dir, "plugin.json"), ValidJson());
        Assert.False(PluginManifest.TryLoad(dir, out _, out var error2));
        Assert.Contains("Hello.dll", error2);
    }

    // ————————————————————— 目录扫描 —————————————————————

    [Fact]
    public void Catalog_FindsPlugins_AndSkipsTempDirs()
    {
        var plugins = Path.Combine(_root, "plugins");
        MakePluginDir("hello");
        MakePluginDir("_install_tmp", id: "tmp");
        MakePluginDir(".hidden", id: "hidden");

        var found = PluginCatalog.Scan(plugins);

        Assert.Single(found);
        Assert.Equal("hello", found[0].Id);
        Assert.True(found[0].Ok);
    }

    [Fact]
    public void Catalog_ReportsBrokenDirectory()
    {
        var plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "broken"));

        var found = PluginCatalog.Scan(plugins);

        Assert.Single(found);
        Assert.False(found[0].Ok);
        Assert.Contains("plugin.json", found[0].Error);
    }

    [Fact]
    public void Catalog_FlagsDuplicateIds()
    {
        var plugins = Path.Combine(_root, "plugins");
        MakePluginDir("a", id: "same");
        MakePluginDir("b", id: "same");

        var found = PluginCatalog.Scan(plugins);

        Assert.Equal(2, found.Count);
        Assert.Single(found, p => p.Ok);
        Assert.Single(found, p => !p.Ok && p.Error!.Contains("重复"));
    }

    [Fact]
    public void Catalog_MissingRoot_ReturnsEmpty() =>
        Assert.Empty(PluginCatalog.Scan(Path.Combine(_root, "nope")));

    // ————————————————————— 启停状态 —————————————————————

    [Fact]
    public void State_RoundTripsDisabledList()
    {
        var path = Path.Combine(_root, "plugins.json");
        var state = PluginState.Load(path);
        Assert.True(state.IsEnabled("a"));

        state.SetEnabled("a", false);
        state.Save();

        var reloaded = PluginState.Load(path);
        Assert.False(reloaded.IsEnabled("a"));
        Assert.True(reloaded.IsEnabled("b"));
    }

    [Fact]
    public void State_IgnoresBrokenFile()
    {
        var path = Path.Combine(_root, "plugins.json");
        File.WriteAllText(path, "{ this is not json");

        var state = PluginState.Load(path);

        Assert.True(state.IsEnabled("anything"));   // 坏文件按"全启用"，插件不会凭空消失
    }

    // ————————————————————— 安装 —————————————————————

    [Fact]
    public void Install_FromFolder_CopiesIntoPluginsRoot()
    {
        var source = MakePluginDir("src", id: "demo");
        var pluginsRoot = Path.Combine(_root, "target");

        var result = PluginInstaller.Install(source, pluginsRoot);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("demo", result.PluginId);
        Assert.True(File.Exists(Path.Combine(pluginsRoot, "demo", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(pluginsRoot, "demo", "Hello.dll")));
    }

    [Fact]
    public void Install_Twice_FailsWithPluginId()
    {
        var source = MakePluginDir("src", id: "demo");
        var pluginsRoot = Path.Combine(_root, "target");
        Assert.True(PluginInstaller.Install(source, pluginsRoot).Ok);

        var again = PluginInstaller.Install(source, pluginsRoot);

        Assert.False(again.Ok);
        Assert.Equal("demo", again.PluginId);          // 界面靠它问"要覆盖吗"
        Assert.Contains("已经装过", again.Error);
    }

    [Fact]
    public void Install_Overwrite_ReplacesFiles()
    {
        var source = MakePluginDir("src", id: "demo");
        var pluginsRoot = Path.Combine(_root, "target");
        Assert.True(PluginInstaller.Install(source, pluginsRoot).Ok);

        // 改一版再覆盖
        File.WriteAllText(Path.Combine(source, "plugin.json"),
            ValidJson(id: "demo").Replace("1.2.3", "2.0.0"));
        var overwrite = PluginInstaller.Install(source, pluginsRoot, overwrite: true);

        Assert.True(overwrite.Ok, overwrite.Error);
        var installed = PluginManifest.TryLoad(Path.Combine(pluginsRoot, "demo"), out var m, out _);
        Assert.True(installed);
        Assert.Equal("2.0.0", m.Version);
        Assert.False(Directory.Exists(Path.Combine(pluginsRoot, "demo.bak")), "备份目录应该清掉");
    }

    [Fact]
    public void Install_FromZip_HandlesNestedRootFolder()
    {
        var source = MakePluginDir("src", id: "zipped");
        var package = Path.Combine(_root, "zipped.cblplugin");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            // 整包套了一层文件夹，是打 zip 时最常见的样子
            zip.CreateEntryFromFile(Path.Combine(source, "plugin.json"), "zipped/plugin.json");
            zip.CreateEntryFromFile(Path.Combine(source, "Hello.dll"), "zipped/Hello.dll");
        }
        var pluginsRoot = Path.Combine(_root, "target");

        var result = PluginInstaller.Install(package, pluginsRoot);

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(pluginsRoot, "zipped", "plugin.json")));
    }

    [Fact]
    public void Install_RejectsZipPathTraversal()
    {
        var package = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../escaped.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("boom");
        }
        var pluginsRoot = Path.Combine(_root, "target");

        var result = PluginInstaller.Install(package, pluginsRoot);

        Assert.False(result.Ok);
        Assert.Contains("越界", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void Install_RejectsPackageWithoutManifest()
    {
        var package = Path.Combine(_root, "empty.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("nothing here");
        }

        var result = PluginInstaller.Install(package, Path.Combine(_root, "target"));

        Assert.False(result.Ok);
        Assert.Contains("plugin.json", result.Error);
    }

    [Fact]
    public void Uninstall_RemovesDirectory()
    {
        var source = MakePluginDir("src", id: "bye");
        var pluginsRoot = Path.Combine(_root, "target");
        Assert.True(PluginInstaller.Install(source, pluginsRoot).Ok);

        var result = PluginInstaller.Uninstall("bye", pluginsRoot);

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(Path.Combine(pluginsRoot, "bye")));
    }

    [Fact]
    public void Uninstall_RejectsPathInId()
    {
        var result = PluginInstaller.Uninstall("../settings", Path.Combine(_root, "target"));

        Assert.False(result.Ok);
        Assert.Contains("不合法", result.Error);
    }

    // ————————————————————— 显示位竞争 —————————————————————

    [Fact]
    public void Registry_IslandText_HighestPriorityWins()
    {
        var registry = new PluginRegistry();
        registry.AddIslandText("a", new IslandTextSpec(() => "低", Priority: 1));
        registry.AddIslandText("b", new IslandTextSpec(() => "高", Priority: 9));

        Assert.Equal("高", registry.IslandText());
    }

    [Fact]
    public void Registry_IslandText_TieGoesToFirstRegistered()
    {
        var registry = new PluginRegistry();
        registry.AddIslandText("first", new IslandTextSpec(() => "先", Priority: 5));
        registry.AddIslandText("second", new IslandTextSpec(() => "后", Priority: 5));

        Assert.Equal("先", registry.IslandText());
    }

    [Fact]
    public void Registry_IslandText_SwallowsPluginExceptions()
    {
        var errors = new List<string>();
        var registry = new PluginRegistry(errors.Add);
        registry.AddIslandText("bad", new IslandTextSpec(() => throw new InvalidOperationException("炸了")));

        Assert.Null(registry.IslandText());
        Assert.Single(errors);
        Assert.Contains("炸了", errors[0]);
    }

    [Fact]
    public void Registry_IslandExtra_ClampsHugeWidth()
    {
        var registry = new PluginRegistry();
        registry.AddIslandExtra("a", new IslandExtraSpec(5000));

        Assert.Equal(400, registry.IslandExtraWidth());
    }

    [Fact]
    public void Registry_Badge_TrimsAndIgnoresBlank()
    {
        var registry = new PluginRegistry();
        registry.AddIslandBadge("a", new IslandBadgeSpec(() => new IslandBadge("  DEMO  ", BadgeTone.Accent)));

        var badge = registry.IslandBadgeValue();
        Assert.NotNull(badge);
        Assert.Equal("DEMO", badge!.Text);
        Assert.Equal(BadgeTone.Accent, badge.Tone);

        registry.AddIslandBadge("b", new IslandBadgeSpec(() => new IslandBadge("   "), Priority: 10));
        Assert.NotNull(registry.IslandBadgeValue());     // 空白徽标不该顶掉有效的那一个
        Assert.Equal("DEMO", registry.IslandBadgeValue()!.Text);
    }

    [Fact]
    public void Registry_SubIsland_ClampsProgress_AndSkipsEmpty()
    {
        var registry = new PluginRegistry();
        registry.AddSubIsland("a", new SubIslandSpec(() => new SubIslandInfo("天气", "晴", 3.4)));

        Assert.Equal(1, registry.SubIsland()!.Progress);

        var empty = new PluginRegistry();
        empty.AddSubIsland("b", new SubIslandSpec(() => new SubIslandInfo("", "", 0)));
        Assert.Null(empty.SubIsland());
    }

    [Fact]
    public void Registry_Panels_KeepAllAndDropNull()
    {
        var registry = new PluginRegistry();
        registry.AddPanel("a", new PanelSpec("甲", 200, () => new[] { "1" }));
        registry.AddPanel("b", new PanelSpec("乙", 200, () => null));            // 本秒不显示
        registry.AddPanel("c", new PanelSpec("丙", 200, () => new[] { "x", "y" }, Priority: 9));

        var panels = registry.Panels();

        Assert.Equal(2, panels.Count);
        Assert.Equal("丙", panels[0].Spec.Title);       // Priority 高的排前面
        Assert.Equal(2, panels[0].Lines.Count);
        Assert.Equal("甲", panels[1].Spec.Title);
    }

    [Fact]
    public void Registry_BoardSections_KeepAll()
    {
        var registry = new PluginRegistry();
        registry.AddBoardSection("a", new BoardSectionSpec(() => new BoardSection("甲", new[] { "a" })));
        registry.AddBoardSection("b", new BoardSectionSpec(() => new BoardSection("乙", new[] { "b" })));

        Assert.Equal(2, registry.BoardSections().Count);
    }

    [Fact]
    public void Registry_MenuItems_SortByOrder()
    {
        var registry = new PluginRegistry();
        registry.AddBoardAction("a", new BoardActionSpec("后", () => { }, Order: 80));
        registry.AddBoardAction("b", new BoardActionSpec("前", () => { }, Order: 20));

        var actions = registry.BoardActions();

        Assert.Equal("前", actions[0].Text);
        Assert.Equal("后", actions[1].Text);
    }

    [Fact]
    public void Registry_RemovePlugin_ClearsEverything()
    {
        var registry = new PluginRegistry();
        registry.AddIslandText("p", new IslandTextSpec(() => "x"));
        registry.AddPanel("p", new PanelSpec("面板", 200, () => new[] { "1" }));
        registry.AddBoardAction("p", new BoardActionSpec("项", () => { }));

        Assert.Equal(3, registry.CountOf("p"));
        var removed = registry.RemovePlugin("p");

        Assert.Equal(3, removed);
        Assert.Equal(0, registry.Count);
        Assert.Null(registry.IslandText());
    }

    [Fact]
    public void Registry_UnregisterById_LeavesOthersAlone()
    {
        var registry = new PluginRegistry();
        var keep = registry.AddIslandText("p", new IslandTextSpec(() => "留下"));
        var drop = registry.AddIslandText("p", new IslandTextSpec(() => "删掉", Priority: 99));

        registry.Remove(drop);
        registry.Remove("不存在的 id");     // 不该抛

        Assert.Equal("留下", registry.IslandText());
        Assert.NotEmpty(keep);
    }
}
