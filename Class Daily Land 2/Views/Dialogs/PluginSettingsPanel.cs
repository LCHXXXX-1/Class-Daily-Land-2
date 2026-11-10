using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Plugins;
using ClassDailyLand2.App.ViewModels;
using ClassDailyLand2.Plugin;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Dialogs;

/// <summary>
/// 插件自己的设置页（声明式渲染）。
///
/// <para>插件在 <c>Initialize</c> 里用 <c>host.RegisterSettings(...)</c> 声明有哪些设置项，
/// 这里按 <see cref="SettingsPageSpec.Fields"/> 逐项画成宿主的原生控件 ——
/// 插件本身碰不到任何 WPF 类型，深色/浅色、触屏放大、键盘操作全都和设置中心其余页面一致。</para>
///
/// <para><b>值的落点</b>：插件私有 state（<c>plugins/&lt;id&gt;/data/state.json</c>）。
/// 保存时逐项 <c>WritePluginSetting</c>，它会落盘并触发插件的 <c>SettingChanged</c>，
/// 插件据此立刻刷新缓存，不需要用户再重载。</para>
/// </summary>
public sealed class PluginSettingsPanel : DialogPanel
{
    readonly PluginManager _plugins;
    readonly PluginInfo _info;
    readonly SettingsPageSpec _spec;

    /// <summary>界面上每个控件的取值器：保存时按声明顺序读一遍。</summary>
    readonly List<(SettingField Field, Func<string?> Read)> _getters = new();

    public PluginSettingsPanel(SettingsViewModel vm, PluginManager plugins, PluginInfo info, SettingsPageSpec spec)
        : base(vm, spec.Title, $"插件设置 · 保存在该插件自己的 data/state.json", 600)
    {
        _plugins = plugins;
        _info = info;
        _spec = spec;

        if (spec.Fields is null || spec.Fields.Count == 0)
        {
            Add(Empty("这个插件没有声明任何设置项。"));
        }
        else
        {
            foreach (var field in spec.Fields) Add(BuildField(field));
        }

        AddFooterInfo($"{info.Name} v{info.Version} · {spec.Fields?.Count ?? 0} 项");
        AddButton("取消", Close);
        AddButton("保存", Save, primary: true);
    }

    public override string ParentPageTitle => "插件";

    // ————————————————————— 逐项渲染 —————————————————————

    UIElement BuildField(SettingField field)
    {
        // 当前值：插件写过的优先，没写过就用声明里的默认值
        var current = _plugins.ReadPluginSetting(_info.Id, field.Key) ?? field.DefaultValue;

        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        box.Children.Add(new TextBlock { Text = field.Label, Style = S("EdFieldLabel") });

        switch (field.Kind)
        {
            case SettingFieldKind.Toggle:
                {
                    var toggle = new FluentToggle
                    {
                        Label = "",
                        IsChecked = string.Equals(current, "true", StringComparison.OrdinalIgnoreCase),
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    box.Children.Add(toggle);
                    _getters.Add((field, () => toggle.IsChecked == true ? "true" : "false"));
                    break;
                }

            case SettingFieldKind.Number:
                {
                    // Min/Max 都是 0 = 不限制（声明里这个约定要跟文档一致）
                    var limited = field.Min != 0 || field.Max != 0;
                    var min = limited ? (int)Math.Round(field.Min) : int.MinValue;
                    var max = limited ? (int)Math.Round(field.Max) : int.MaxValue;
                    var value = int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : (limited ? Math.Clamp(0, min, max) : 0);

                    var number = new NumberBox
                    {
                        Value = Math.Clamp(value, min, max),
                        Minimum = min,
                        Maximum = max,
                        Suffix = field.Unit ?? "",
                        Width = 150,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    box.Children.Add(number);
                    _getters.Add((field, () => number.Value.ToString(CultureInfo.InvariantCulture)));
                    break;
                }

            case SettingFieldKind.Choice:
                {
                    var options = field.Options ?? Array.Empty<SettingOption>();
                    var combo = new ComboBox
                    {
                        Style = S("EdSelect"),
                        MinWidth = 200,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };

                    var selected = 0;
                    for (var i = 0; i < options.Count; i++)
                    {
                        combo.Items.Add(options[i].Label);
                        if (string.Equals(options[i].Value, current, StringComparison.OrdinalIgnoreCase)) selected = i;
                    }
                    if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(selected, 0, combo.Items.Count - 1);

                    box.Children.Add(combo);
                    _getters.Add((field, () =>
                        options.Count == 0 ? null : options[Math.Clamp(combo.SelectedIndex, 0, options.Count - 1)].Value));
                    break;
                }

            default:
                {
                    var text = Input(current ?? "", 280);
                    text.HorizontalAlignment = HorizontalAlignment.Left;
                    box.Children.Add(text);
                    _getters.Add((field, () => string.IsNullOrWhiteSpace(text.Text) ? null : text.Text.Trim()));
                    break;
                }
        }

        if (!string.IsNullOrWhiteSpace(field.Hint))
            box.Children.Add(new TextBlock
            {
                Text = field.Hint,
                Style = S("EdCardSubtitle"),
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

        return box;
    }

    // ————————————————————— 保存 —————————————————————

    void Save()
    {
        var written = 0;
        var failed = 0;

        foreach (var (field, read) in _getters)
        {
            if (_plugins.WritePluginSetting(_info.Id, field.Key, read())) written++;
            else failed++;
        }

        if (failed > 0)
            Tell(this, $"有 {failed} 项没保存：插件当前没有在运行，先启用它再改。", "保存失败");
        else
            Tell(this, $"已保存 {written} 项设置，插件立即生效。", "已保存");

        Close();
    }
}
