using ClassDailyLand2.Core.Plugins;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 安装残留：覆盖安装留下的 <c>&lt;id&gt;.bak-时间戳</c>、卸载删不掉时改名的 <c>_trash-*</c>。
/// 这两种目录既不该出现在插件列表里，也不该让「卸载」看起来失败。
/// </summary>
public sealed class PluginLeftoverTests : IDisposable
{
    readonly string _root;

    public PluginLeftoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdl-leftover-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清不掉不影响断言 */ }
    }

    string MakePluginDir(string folder, string id = "demo")
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Demo.dll"), "not a real assembly");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
        { "id": "{{id}}", "name": "测试插件", "version": "1.0.0", "entry": "Demo.dll", "type": "Test.Plugin" }
        """);
        return dir;
    }

    [Theory]
    [InlineData("demo.bak-20261101120000", true)]
    [InlineData("_trash-1a2b3c4d", true)]
    [InlineData("demo", false)]
    [InlineData("demo.backup", false)]
    public void IsLeftover_RecognisesOnlyOurFolders(string folder, bool expected) =>
        Assert.Equal(expected, PluginCatalog.IsLeftover(folder));

    [Fact]
    public void Scan_SkipsBackupAndTrashFolders()
    {
        MakePluginDir("demo", "demo");
        Directory.CreateDirectory(Path.Combine(_root, "demo.bak-20261101120000"));
        Directory.CreateDirectory(Path.Combine(_root, "_trash-1a2b3c4d"));

        var packages = PluginCatalog.Scan(_root);

        var only = Assert.Single(packages);
        Assert.Equal("demo", only.Id);
        Assert.True(only.Ok);
    }

    [Fact]
    public void SweepLeftovers_RemovesBothKinds_KeepsRealPlugin()
    {
        var real = MakePluginDir("demo", "demo");
        var backup = Path.Combine(_root, "demo.bak-20261101120000");
        var trash = Path.Combine(_root, "_trash-1a2b3c4d");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(backup, "leftover.txt"), "x");

        var cleaned = PluginInstaller.SweepLeftovers(_root);

        Assert.Equal(2, cleaned);
        Assert.True(Directory.Exists(real));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(trash));
    }

    [Fact]
    public void SweepLeftovers_MissingRoot_IsNoOp() =>
        Assert.Equal(0, PluginInstaller.SweepLeftovers(Path.Combine(_root, "not-there")));

    /// <summary>
    /// 卸载时目录里有文件被独占打开（等价于旧 dll 句柄还没释放）：
    /// 既删不掉也改不了名（Windows 的 rename 需要 DELETE 共享），
    /// 这时必须给出人话原因；句柄释放后立刻就能删干净。
    /// </summary>
    [Fact]
    public void Uninstall_WithLockedFile_ExplainsAndSucceedsAfterRelease()
    {
        var dir = MakePluginDir("locked", "locked");
        var locked = Path.Combine(dir, "Demo.dll");

        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = PluginInstaller.Uninstall("locked", _root);

            Assert.False(blocked.Ok);
            Assert.Contains("占用", blocked.Error);
            Assert.True(Directory.Exists(dir));      // 失败时不能把目录弄成半截
        }

        // 句柄释放后（等价于可卸载 ALC 被回收）再卸一次：必须干净删掉
        var retry = PluginInstaller.Uninstall("locked", _root);

        Assert.True(retry.Ok, retry.Error);
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>没有占用时就是老老实实删掉，不留 <c>_trash-</c>。</summary>
    [Fact]
    public void Uninstall_Normal_DeletesDirectory()
    {
        var dir = MakePluginDir("plain", "plain");

        var outcome = PluginInstaller.Uninstall("plain", _root);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.False(Directory.Exists(dir));
        Assert.DoesNotContain(Directory.GetDirectories(_root),
            d => Path.GetFileName(d)!.StartsWith("_trash-", StringComparison.OrdinalIgnoreCase));
    }
}
