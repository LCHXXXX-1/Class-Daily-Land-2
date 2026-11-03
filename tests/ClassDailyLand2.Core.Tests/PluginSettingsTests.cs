using ClassDailyLand2.Core.Plugins;
using ClassDailyLand2.Plugin;
using Xunit;

namespace ClassDailyLand2.Core.Tests;

/// <summary>
/// 插件设置页的注册表部分：声明、覆盖、随插件卸载一起清掉，且不污染「显示位数量」。
/// </summary>
public sealed class PluginSettingsTests
{
    static SettingsPageSpec Spec(string title = "测试设置") => new(
        title,
        new[]
        {
            new SettingField("grade", "届别", SettingFieldKind.Choice,
                Options: new[] { new SettingOption("2028", "2028 届"), new SettingOption("2029", "2029 届") }),
            new SettingField("show", "显示", SettingFieldKind.Toggle, DefaultValue: "false"),
        },
        Summary: () => "2029 届 · 还有 100 天");

    [Fact]
    public void Settings_StoredPerPlugin()
    {
        var registry = new PluginRegistry();

        registry.SetSettings("a.plugin", Spec("A 的设置"));

        Assert.NotNull(registry.Settings("a.plugin"));
        Assert.Equal("A 的设置", registry.Settings("a.plugin")!.Title);
        Assert.Null(registry.Settings("b.plugin"));
    }

    [Fact]
    public void Settings_LastDeclarationWins()
    {
        var registry = new PluginRegistry();

        registry.SetSettings("a.plugin", Spec("旧的"));
        registry.SetSettings("a.plugin", Spec("新的"));

        Assert.Equal("新的", registry.Settings("a.plugin")!.Title);
    }

    [Fact]
    public void Settings_RemovedWithPlugin()
    {
        var registry = new PluginRegistry();
        registry.SetSettings("a.plugin", Spec());
        registry.AddPanel("a.plugin", new PanelSpec("面板", 200, () => new[] { "一行" }));

        var removed = registry.RemovePlugin("a.plugin");

        Assert.Equal(1, removed);                       // 只数显示位，设置页不算
        Assert.Null(registry.Settings("a.plugin"));
    }

    [Fact]
    public void Settings_DoNotCountAsDisplaySlots()
    {
        var registry = new PluginRegistry();

        registry.SetSettings("a.plugin", Spec());
        Assert.Equal(0, registry.CountOf("a.plugin"));

        registry.AddPanel("a.plugin", new PanelSpec("面板", 200, () => new[] { "一行" }));
        Assert.Equal(1, registry.CountOf("a.plugin"));
    }

    [Fact]
    public void RegisterSettings_ReturnsId()
    {
        var registry = new PluginRegistry();
        Assert.Null(registry.Settings("x"));

        registry.SetSettings("x", Spec());
        Assert.NotNull(registry.Settings("x"));
    }

    [Fact]
    public void Spec_DefaultsAreUsable()
    {
        var field = new SettingField("k", "标签", SettingFieldKind.Number, Min: 1, Max: 9, Unit: " 天");

        Assert.Equal(SettingFieldKind.Number, field.Kind);
        Assert.Equal(1, field.Min);
        Assert.Equal(9, field.Max);
        Assert.Equal(" 天", field.Unit);
        Assert.Null(field.DefaultValue);
    }
}
