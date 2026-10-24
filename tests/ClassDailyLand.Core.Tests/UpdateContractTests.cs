using ClassDailyLand.UpdateContract;
using Xunit;

namespace ClassDailyLand.Core.Tests;

/// <summary>
/// 更新契约（job.json / version.json）的纯逻辑测试。
///
/// 这里守的是「更新不能吃掉用户数据」这条底线：
/// 路径校验、保留白名单、替换计划、快照比对 —— 四个都必须逐条有断言。
/// </summary>
public sealed class UpdateContractTests
{
    // ─────────────────────────── SemVer ───────────────────────────

    [Theory]
    [InlineData("1.0.0", 1, 0, 0, false)]
    [InlineData("1.0", 1, 0, 0, false)]
    [InlineData("2", 2, 0, 0, false)]
    [InlineData("1.1.0-beta.1", 1, 1, 0, true)]
    [InlineData("1.1.0+3f2a1b", 1, 1, 0, false)]
    [InlineData("1.1.0-beta.1+build", 1, 1, 0, true)]
    public void SemVer_Parse_Common(string text, int major, int minor, int patch, bool preRelease)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(preRelease, v.IsPreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.0.0.0")]
    [InlineData("1..2")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-..")]
    [InlineData("v1.0.0")]
    public void SemVer_Parse_RejectsBadInput(string? text) =>
        Assert.False(SemVer.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.0.0-beta", "1.0.0")]          // 预发布 < 正式版
    [InlineData("1.0.0-beta.1", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]   // 段数少的更小
    [InlineData("1.0.0-alpha", "1.0.0-beta")]    // 字母序
    [InlineData("1.0.0-beta.1", "1.0.0-beta.2")]
    public void SemVer_Compare_Less(string a, string b)
    {
        Assert.True(SemVer.TryParse(a, out var av));
        Assert.True(SemVer.TryParse(b, out var bv));
        Assert.True(av < bv, $"{a} 应小于 {b}");
        Assert.True(bv > av, $"{b} 应大于 {a}");
    }

    [Fact]
    public void SemVer_Compare_Treats10As100()
    {
        // 1.0 与 1.0.0 必须等价（否则永远报"有新版本"）
        Assert.True(SemVer.TryParse("1.0", out var a));
        Assert.True(SemVer.TryParse("1.0.0", out var b));
        Assert.Equal(0, a.CompareTo(b));
        Assert.False(a < b);
        Assert.False(a > b);
    }

    [Fact]
    public void SemVer_NumericSegmentBeatsAlphaSegment()
    {
        // semver 规定：数字段 < 字母段
        Assert.True(SemVer.TryParse("1.0.0-1", out var n));
        Assert.True(SemVer.TryParse("1.0.0-alpha", out var a));
        Assert.True(n < a);
    }

    [Fact]
    public void SemVer_ToString_RoundTrips()
    {
        Assert.True(SemVer.TryParse("1.1.0-beta.2", out var v));
        Assert.Equal("1.1.0-beta.2", v.ToString());
    }

    // ─────────────────────────── SafeRelativePath ───────────────────────────

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("dir/sub/file.dll", "dir/sub/file.dll")]
    [InlineData(@"dir\sub\file.dll", "dir/sub/file.dll")]
    [InlineData("./a.txt", "a.txt")]
    [InlineData("/leading/slash.txt", null)]          // 绝对路径
    [InlineData(@"..\..\windows\system32\x.dll", null)]
    [InlineData("a/../../b.txt", null)]
    [InlineData("C:/windows/x.dll", null)]           // 盘符
    [InlineData("file.txt:stream", null)]            // ADS
    [InlineData("CON", null)]                        // 保留设备名
    [InlineData("dir/COM1.txt", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void SafeRelativePath_NormalizesOrRejects(string input, string? expected)
    {
        var ok = SafeRelativePath.TryNormalize(input, out var rel, out var error);
        if (expected is null)
        {
            Assert.False(ok, $"「{input}」应当被拒绝");
            Assert.NotEqual("", error);
        }
        else
        {
            Assert.True(ok, $"「{input}」应当被接受：{error}");
            Assert.Equal(expected, rel);
        }
    }

    [Fact]
    public void SafeRelativePath_IsInside_CatchesEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-root");
        Assert.True(SafeRelativePath.IsInside(root, Path.Combine(root, "sub", "a.txt")));
        Assert.False(SafeRelativePath.IsInside(root, Path.Combine(Path.GetTempPath(), "other", "a.txt")));
    }

    // ─────────────────────────── PreserveRules（用户数据保护） ───────────────────────────

    static readonly string[] KeepDirs = { "settings", "plugins" };
    static readonly string[] KeepPatterns = { "*.log", "*.pdb", "*.bak-*" };

    [Theory]
    [InlineData("settings/settings.json", true)]       // 用户数据
    [InlineData("settings/schedule.json", true)]
    [InlineData("plugins/hello/plugin.json", true)]    // 用户装的插件
    [InlineData("plugins/foo.dll", true)]
    [InlineData("settings", true)]                     // 目录本身
    [InlineData("app.log", true)]                      // 运行日志
    [InlineData("sub/app.log", true)]
    [InlineData("x.pdb", true)]
    [InlineData("x.bak-20261025", true)]
    [InlineData("Class Daily Land.exe", false)]        // 程序本体，要更新
    [InlineData("Class Daily Land.dll", false)]
    [InlineData("Resources/cities.json", false)]       // 资源（走 resources 包）
    [InlineData("my-settings/keep.txt", false)]        // ★ 不能被误判成 settings
    [InlineData("assets/plugins/icon.svg", false)]     // ★ assets 下的 plugins 目录不该保留
    [InlineData("updater/Updater.dll", false)]         // 更新器自己要更新
    public void PreserveRules_KeepsUserDataAndPluginsOnly(string rel, bool expectPreserved)
    {
        var preserved = PreserveRules.IsPreserved(rel, KeepDirs, KeepPatterns);
        Assert.Equal(expectPreserved, preserved);
    }

    // ─────────────────────────── ReplacePlanner ───────────────────────────

    [Fact]
    public void ReplacePlanner_BuildsCorrectPlan()
    {
        var existing = new[]
        {
            "Class Daily Land.exe",
            "Class Daily Land.dll",
            "old.dll",                    // 新版本没有了 → 删
            "settings/settings.json",     // 用户数据 → 保留
            "settings/schedule.json",
            "plugins/hello/plugin.json",  // 用户插件 → 保留
            "app.log",                    // 模式保留
        };

        var payload = new[]
        {
            "Class Daily Land.exe",       // 已存在 → 覆盖
            "Class Daily Land.dll",
            "new.dll",                    // 新增
            "settings/settings.json",     // ★ payload 里居然带了用户数据 → 绝不落地
        };

        var plan = ReplacePlanner.Build(existing, payload, KeepDirs, KeepPatterns);

        Assert.Equal(new[] { "new.dll" }, plan.ToCreate);
        Assert.Equal(new[] { "Class Daily Land.dll", "Class Daily Land.exe" }, plan.ToUpdate);
        Assert.Equal(new[] { "old.dll" }, plan.ToDelete);
        // settings/* 与 plugins/* 与 app.log 都在保留列表里（含 payload 里混进来的 settings）
        Assert.Contains("settings/settings.json", plan.Preserved);
        Assert.Contains("settings/schedule.json", plan.Preserved);
        Assert.Contains("plugins/hello/plugin.json", plan.Preserved);
        Assert.Contains("app.log", plan.Preserved);
    }

    [Fact]
    public void ReplacePlanner_NeverDeletesUserData()
    {
        var existing = new[]
        {
            "settings/homework.json",
            "plugins/bar/Bar.dll",
            "app.log",
        };
        var plan = ReplacePlanner.Build(existing, Array.Empty<string>(), KeepDirs, KeepPatterns);

        // payload 为空（极端情况：空包）也绝不能删用户数据
        Assert.Empty(plan.ToDelete);
        Assert.Equal(3, plan.Preserved.Count);
    }

    [Fact]
    public void ReplacePlanner_SweepsStaleProgramFiles()
    {
        var existing = new[] { "a.dll", "b.dll", "stale/c.dll" };
        var payload = new[] { "a.dll" };
        var plan = ReplacePlanner.Build(existing, payload, KeepDirs, KeepPatterns);

        Assert.Equal(new[] { "b.dll", "stale/c.dll" }, plan.ToDelete);
    }

    // ─────────────────────────── PreservedSnapshot（前后快照比对） ───────────────────────────

    static FileFingerprint Fp(string path, long len = 10, long ticks = 100) => new(path, len, ticks);

    [Fact]
    public void Snapshot_Compare_Identical()
    {
        var before = new List<FileFingerprint> { Fp("settings/a.json"), Fp("plugins/b.dll", 20, 200) };
        var after = new List<FileFingerprint> { Fp("settings/a.json"), Fp("plugins/b.dll", 20, 200) };
        Assert.Null(PreservedSnapshot.Compare(before, after));
    }

    [Fact]
    public void Snapshot_Compare_DetectsRewrite()
    {
        var before = new List<FileFingerprint> { Fp("settings/schedule.json", 100, 1) };
        var after = new List<FileFingerprint> { Fp("settings/schedule.json", 200, 1) };
        var diff = PreservedSnapshot.Compare(before, after);
        Assert.NotNull(diff);
        Assert.Contains("schedule.json", diff);
    }

    [Fact]
    public void Snapshot_Compare_DetectsLossAndIntrusion()
    {
        var before = new List<FileFingerprint> { Fp("settings/a.json"), Fp("plugins/b.dll") };
        var after = new List<FileFingerprint> { Fp("settings/a.json"), Fp("plugins/evil.dll") };

        var diff = PreservedSnapshot.Compare(before, after);
        Assert.NotNull(diff);
        Assert.Contains("丢失", diff!);   // plugins/b.dll 不见了
        Assert.Contains("新增", diff);     // plugins/evil.dll 冒出来
    }

    // ─────────────────────────── Hashing ───────────────────────────

    [Fact]
    public void Hashing_LooksLikeSha256()
    {
        var good = new string('a', 64);
        Assert.True(Hashing.LooksLikeSha256(good));
        Assert.True(Hashing.LooksLikeSha256(good.ToUpperInvariant()));
        Assert.False(Hashing.LooksLikeSha256(new string('g', 64)));  // 非十六进制
        Assert.False(Hashing.LooksLikeSha256(new string('a', 63)));  // 少一位
        Assert.False(Hashing.LooksLikeSha256("todo"));
        Assert.False(Hashing.LooksLikeSha256(null));
    }

    [Fact]
    public void Hashing_Equals_IgnoresCaseAndWhitespace()
    {
        var a = new string('b', 64);
        Assert.True(Hashing.Sha256Equals(a, "  " + a.ToUpperInvariant() + " "));
        Assert.False(Hashing.Sha256Equals(a, new string('c', 64)));
        Assert.False(Hashing.Sha256Equals("", ""));
    }

    // ─────────────────────────── UpdateJob 容错 ───────────────────────────

    [Fact]
    public void UpdateJob_Normalize_FillsDefaults()
    {
        var job = new UpdateJob();
        job.Normalize();

        Assert.Equal(UpdateJob.CurrentSchema, job.Schema);
        Assert.NotEqual("", job.JobId);
        Assert.Equal("update", job.Action);
        Assert.Equal("stable", job.Channel);
        Assert.Equal("Class Daily Land.exe", job.App.ExeName);
        Assert.Equal(2, job.Preserve.Count);
        Assert.Contains("settings", job.Preserve);
        Assert.Contains("plugins", job.Preserve);
        Assert.Equal(3, job.PreservePatterns.Count);
        Assert.Equal(120, job.Download.TimeoutSeconds);
        Assert.Equal(3, job.Download.MaxAttempts);
        Assert.Equal(5000, job.Limits.MaxEntries);
        Assert.NotEqual("", job.Paths.WorkDir);
        Assert.NotEqual("", job.Paths.BackupRoot);
        Assert.NotEqual("", job.Paths.Log);
        Assert.NotEqual("", job.Paths.Result);
    }

    [Fact]
    public void UpdateJob_Schema_TooNewIsRejected()
    {
        var job = new UpdateJob { Schema = 99 };
        Assert.False(job.IsSchemaSupported);
        job.Schema = 1;
        Assert.True(job.IsSchemaSupported);
    }

    [Fact]
    public void UpdateJob_Schema_MissingIsTreatedAsOne()
    {
        // job.json 可能漏写 schema：反序列化拿到 0，Normalize 会补成 1 而不是拒收
        var job = new UpdateJob { Schema = 0 };
        job.Normalize();
        Assert.Equal(UpdateJob.CurrentSchema, job.Schema);
        Assert.True(job.IsSchemaSupported);
    }

    // ─────────────────────────── RemoteVersion 容错 ───────────────────────────

    [Fact]
    public void RemoteVersion_ReadsSampleJson()
    {
        const string json = """
        {
          "stable": {
            "version": "1.0.0",
            "release_date": "2026-10-24",
            "download_url": "https://example.com/downloads/ClassDailyLand2_1.0.0.zip",
            "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "changelog": ["正式版发布", "修复主窗口字体实时缩放问题"],
            "is_mandatory": false
          },
          "beta": {
            "version": "1.1.0-beta.1",
            "release_date": "2026-10-25",
            "download_url": "https://example.com/downloads/ClassDailyLand2_1.1.0-beta.1.zip",
            "sha256": "a1b2c3d4e5f67890a1B2C3D4E5F67890a1b2c3d4e5f67890a1b2c3d4e5f67890",
            "changelog": ["测试：全新插件系统架构"],
            "is_mandatory": false
          },
          "resources": {
            "version": "1.2.0",
            "release_date": "2026-10-20",
            "download_url": "https://example.com/downloads/resources_v1.2.0.zip",
            "sha256": "f9e8d7c6b5a43210f9e8d7c6b5a43210f9e8d7c6b5a43210f9e8d7c6b5a43210",
            "changelog": [
              "更新默认图标集与主题色",
              "更新 2027 年法定节假日数据",
              "新增默认课表模板"
            ]
          }
        }
        """;

        var file = System.Text.Json.JsonSerializer.Deserialize<RemoteVersionFile>(json, UpdateJson.Options);
        Assert.NotNull(file);
        Assert.Equal("1.0.0", file!.Stable!.Version);
        Assert.False(file.Stable.IsMandatory);
        Assert.Equal(2, file.Stable.Changelog.Count);

        Assert.Equal("1.1.0-beta.1", file.Beta!.Version);
        Assert.True(file.Beta.VersionValue.IsPreRelease);

        // resources 块没有 is_mandatory —— 不能因此解析失败
        Assert.NotNull(file.Resources);
        Assert.Equal("1.2.0", file.Resources!.Version);
        Assert.Null(file.Resources.IsMandatory);
        Assert.Equal(3, file.Resources.Changelog.Count);
    }

    [Fact]
    public void RemoteVersion_ChangelogAcceptsSingleString()
    {
        // 远端是手写的，changelog 写成一个字符串很常见
        const string json = """
        { "stable": { "version": "1.0.0", "changelog": "只有这一条", "download_url": "https://x/y.zip", "sha256": "" } }
        """;
        var file = System.Text.Json.JsonSerializer.Deserialize<RemoteVersionFile>(json, UpdateJson.Options);
        Assert.Single(file!.Stable!.Changelog);
        Assert.Equal("只有这一条", file.Stable.Changelog[0]);
    }

    [Fact]
    public void RemoteVersion_ToleratesBrokenChannel()
    {
        // stable 的 sha256 是错的、beta 正常 —— 整个文件不能解析失败
        const string json = """
        {
          "stable": { "version": "1.0.0", "download_url": "https://x/y.zip", "sha256": "todo" },
          "beta":   { "version": "1.1.0", "download_url": "https://x/z.zip", "sha256": "" }
        }
        """;
        var file = System.Text.Json.JsonSerializer.Deserialize<RemoteVersionFile>(json, UpdateJson.Options);
        Assert.NotNull(file);
        Assert.NotNull(file!.Stable);
        Assert.NotNull(file.Beta);
    }
}
