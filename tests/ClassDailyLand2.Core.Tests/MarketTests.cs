using ClassDailyLand2.Core.Market;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 插件市场的纯逻辑：索引解析、兼容性判定、安装包校验。
/// 全部不联网、不加载程序集 —— 和 PluginSystemTests 一样当普通单元测试跑。
/// </summary>
public sealed class MarketTests : IDisposable
{
    /// <summary>一个格式合法的 sha256（内容对不对由下载校验负责，这里只管形状）。</summary>
    const string Sha = "9742EF5D012D763421767EE66E7AD9E7E4773C8740A593E606EF37A859CDF7D5";

    readonly string _root;

    public MarketTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdl-market-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清不掉不影响断言 */ }
    }

    // ————————————————————— 工具 —————————————————————

    static string EntryJson(
        string id,
        string version = "1.0.0",
        string sha = Sha,
        string category = "reminder",
        string trust = "official",
        int api = 1,
        string extra = "") => $$"""
        {
          "id": "{{id}}",
          "name": "测试插件",
          "summary": "一句话说明",
          "author": "tester",
          "version": "{{version}}",
          "api_version": {{api}},
          "category": "{{category}}",
          "trust": "{{trust}}",
          "path": "packages/{{id}}/{{id}}-{{version}}.cblplugin",
          "sha256": "{{sha}}",
          "size": 1024,
          "published_at": "2026-11-01"
          {{extra}}
        }
        """;

    static string IndexJson(params string[] entries) => $$"""
        {
          "schema": 1,
          "generated_at": "2026-11-01T21:00:00+08:00",
          "count": {{entries.Length}},
          "plugins": [ {{string.Join(",", entries)}} ]
        }
        """;

    static MarketIndex ParseOk(string json)
    {
        var parsed = MarketIndex.Parse(json);
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Index!;
    }

    static MarketView Evaluate(MarketEntry entry, string? installed = null, string hostVersion = "1.2", int api = 1) =>
        MarketPolicy.Evaluate(entry, installed, hostVersion, "1.1.0", api);

    // ————————————————————— 索引解析 —————————————————————

    [Fact]
    public void Parse_KeepsValidEntries_AndDropsBrokenOnes()
    {
        var json = IndexJson(
            EntryJson("good.one"),
            EntryJson("bad-hash", sha: "todo"),
            EntryJson("bad/id"),
            EntryJson("good.two", version: "2.1.0", category: "weather", trust: "community"));

        var parsed = MarketIndex.Parse(json);

        Assert.True(parsed.Ok);
        Assert.Equal(2, parsed.Index!.Plugins.Count);
        Assert.Equal(2, parsed.Warnings.Count);
        Assert.Contains(parsed.Index.Plugins, p => p.Id == "good.one");
        Assert.Contains(parsed.Index.Plugins, p => p.Id == "good.two");
    }

    [Fact]
    public void Parse_RejectsNewerSchema()
    {
        var json = IndexJson(EntryJson("x")).Replace("\"schema\": 1", "\"schema\": 2");
        var parsed = MarketIndex.Parse(json);

        Assert.False(parsed.Ok);
        Assert.Contains("v2", parsed.Error);
    }

    [Fact]
    public void Parse_BrokenJson_ReportsError()
    {
        var parsed = MarketIndex.Parse("{ this is not json }");
        Assert.False(parsed.Ok);
        Assert.Contains("JSON", parsed.Error);
    }

    [Fact]
    public void Parse_UnknownCategoryAndTrust_FallBack()
    {
        var index = ParseOk(IndexJson(EntryJson("weird.one", category: "外星分类", trust: "随便写的")));
        var entry = index.Plugins[0];

        Assert.Equal("other", entry.CategoryOrDefault);
        // 未知信任级别按 community 处理：宁可多问用户一句
        Assert.Equal("community", entry.TrustOrDefault);
    }

    [Fact]
    public void Parse_DuplicateId_KeepsHigherVersion()
    {
        var index = ParseOk(IndexJson(
            EntryJson("dup.plugin", version: "1.0.0"),
            EntryJson("dup.plugin", version: "1.4.0")));

        var entry = Assert.Single(index.Plugins);
        Assert.Equal("1.4.0", entry.Version);
    }

    // ————————————————————— 状态判定 —————————————————————

    [Fact]
    public void Policy_NotInstalled_UpToDate_Update_Downgrade()
    {
        var entry = ParseOk(IndexJson(EntryJson("p", version: "1.2.0"))).Plugins[0];

        Assert.Equal(MarketState.NotInstalled, Evaluate(entry).State);
        Assert.Equal(MarketState.UpToDate, Evaluate(entry, "1.2.0").State);
        Assert.Equal(MarketState.Update, Evaluate(entry, "1.1.9").State);
        Assert.Equal(MarketState.Downgrade, Evaluate(entry, "1.3.0").State);
    }

    [Fact]
    public void Policy_UnparsableInstalledVersion_TreatedAsUpdate()
    {
        var entry = ParseOk(IndexJson(EntryJson("p"))).Plugins[0];
        var view = Evaluate(entry, "whatever");

        Assert.Equal(MarketState.Update, view.State);
        Assert.NotNull(view.Reason);
    }

    [Fact]
    public void Policy_BlocksIncompatibleContract()
    {
        var entry = ParseOk(IndexJson(EntryJson("p", api: 2))).Plugins[0];
        var view = Evaluate(entry, api: 1);

        Assert.Equal(MarketState.Incompatible, view.State);
        Assert.False(view.CanInstall);
        Assert.Contains("契约", view.Reason);
    }

    [Fact]
    public void Policy_BlocksWhenHostVersionTooOld()
    {
        var entry = ParseOk(IndexJson(EntryJson("p", extra: ", \"min_app_version\": \"9.0\""))).Plugins[0];
        var view = Evaluate(entry, hostVersion: "1.2");

        Assert.Equal(MarketState.Incompatible, view.State);
        Assert.Contains("主程序", view.Reason);
    }

    [Fact]
    public void Policy_BlocksWhenSdkTooOld()
    {
        var entry = ParseOk(IndexJson(EntryJson("p", extra: ", \"target_sdk_version\": \"9.9\""))).Plugins[0];
        var view = Evaluate(entry);

        Assert.Equal(MarketState.Incompatible, view.State);
        Assert.Contains("SDK", view.Reason);
    }

    [Fact]
    public void Policy_Revoked_CannotInstall()
    {
        var entry = ParseOk(IndexJson(EntryJson("p", extra: ", \"revoked\": true"))).Plugins[0];
        var view = Evaluate(entry);

        Assert.Equal(MarketState.Revoked, view.State);
        Assert.False(view.CanInstall);
    }

    [Fact]
    public void Policy_SortsUpdatesFirst()
    {
        var index = ParseOk(IndexJson(
            EntryJson("a.new", version: "1.0.0"),
            EntryJson("b.update", version: "2.0.0"),
            EntryJson("c.installed", version: "1.0.0")));

        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["b.update"] = "1.0.0",
            ["c.installed"] = "1.0.0",
        };

        var views = MarketPolicy.Build(index, installed, "1.2", "1.1.0", 1);

        Assert.Equal("b.update", views[0].Entry.Id);      // 可更新排最前
        Assert.Equal(MarketState.NotInstalled, views[1].State);
        Assert.Equal(MarketState.UpToDate, views[2].State);
    }

    // ————————————————————— 安装包校验 —————————————————————

    string MakePackage(string name, string id, string version = "1.0.0", string entry = "Hello.dll", bool includeEntry = true, bool includeContract = false, string? readme = null)
    {
        var file = Path.Combine(_root, name + ".cblplugin");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);

        void Put(string entryName, string content)
        {
            var item = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Put("plugin.json", $$"""
        {
          "id": "{{id}}",
          "name": "测试插件",
          "version": "{{version}}",
          "entry": "{{entry}}",
          "type": "Test.Plugin",
          "apiVersion": 1
        }
        """);

        if (includeEntry) Put(entry, "not a real assembly");
        if (includeContract) Put("ClassDailyLand2.Plugin.Abstractions.dll", "should not be here");
        if (readme is not null) Put("README.md", readme);

        return file;
    }

    static MarketEntry EntryFor(string id, string version = "1.0.0") =>
        ParseOk(IndexJson(EntryJson(id, version: version))).Plugins[0];

    [Fact]
    public void InspectPackage_AcceptsGoodPackage()
    {
        var package = MakePackage("good", "lch.demo");
        Assert.Null(MarketDownloader.InspectPackage(package, EntryFor("lch.demo")));
    }

    [Fact]
    public void InspectPackage_RejectsIdMismatch()
    {
        var package = MakePackage("wrong-id", "someone.else");
        var problem = MarketDownloader.InspectPackage(package, EntryFor("lch.demo"));

        Assert.NotNull(problem);
        Assert.Contains("不一致", problem);
    }

    [Fact]
    public void InspectPackage_RejectsVersionMismatch()
    {
        var package = MakePackage("wrong-version", "lch.demo", version: "1.0.0");
        var problem = MarketDownloader.InspectPackage(package, EntryFor("lch.demo", "1.2.0"));

        Assert.NotNull(problem);
        Assert.Contains("version", problem);
    }

    [Fact]
    public void InspectPackage_RejectsMissingEntryAssembly()
    {
        var package = MakePackage("no-dll", "lch.demo", includeEntry: false);
        var problem = MarketDownloader.InspectPackage(package, EntryFor("lch.demo"));

        Assert.NotNull(problem);
        Assert.Contains("入口程序集", problem);
    }

    [Fact]
    public void InspectPackage_RejectsBundledContractAssembly()
    {
        var package = MakePackage("with-contract", "lch.demo", includeContract: true);
        var problem = MarketDownloader.InspectPackage(package, EntryFor("lch.demo"));

        Assert.NotNull(problem);
        Assert.Contains("契约", problem);
    }

    [Fact]
    public void InspectPackage_RejectsNonZip()
    {
        var file = Path.Combine(_root, "fake.cblplugin");
        File.WriteAllText(file, "this is not a zip");

        var problem = MarketDownloader.InspectPackage(file, EntryFor("lch.demo"));
        Assert.NotNull(problem);
    }

    // ————————————————————— 下载地址 —————————————————————

    [Fact]
    public void PackageUrls_RelativePath_BuiltForBothSources()
    {
        var entry = EntryFor("lch.demo");
        var urls = MarketClient.PackageUrls(entry, new[]
        {
            "https://gitee.com/a/b/raw/plugins/",
            "https://raw.githubusercontent.com/a/b/plugins",
        });

        Assert.Equal(2, urls.Count);
        Assert.Equal("https://gitee.com/a/b/raw/plugins/packages/lch.demo/lch.demo-1.0.0.cblplugin", urls[0]);
        Assert.Equal("https://raw.githubusercontent.com/a/b/plugins/packages/lch.demo/lch.demo-1.0.0.cblplugin", urls[1]);
    }

    [Fact]
    public void PackageUrls_AbsolutePath_UsedDirectly()
    {
        var index = ParseOk("""
        {
          "schema": 1, "generated_at": "2026-11-01T21:00:00+08:00", "count": 1,
          "plugins": [{
            "id": "big.plugin", "name": "大插件", "summary": "走 Releases", "author": "t",
            "version": "1.0.0", "api_version": 1, "category": "tools", "trust": "official",
            "path": "https://example.com/big.cblplugin",
            "sha256": "9742EF5D012D763421767EE66E7AD9E7E4773C8740A593E606EF37A859CDF7D5",
            "published_at": "2026-11-01"
          }]
        }
        """);

        var urls = MarketClient.PackageUrls(index.Plugins[0], new[] { "https://gitee.com/a/b/raw/plugins/" });
        Assert.Single(urls);
        Assert.Equal("https://example.com/big.cblplugin", urls[0]);
    }

    [Fact]
    public void CacheBuster_AppendsUnixSeconds()
    {
        var url = MarketClient.WithCacheBuster("https://gitee.com/a/b/raw/plugins/index.json");
        Assert.Contains("?t=", url);

        var withQuery = MarketClient.WithCacheBuster("https://example.com/x.json?a=1");
        Assert.Contains("&t=", withQuery);
    }
}
