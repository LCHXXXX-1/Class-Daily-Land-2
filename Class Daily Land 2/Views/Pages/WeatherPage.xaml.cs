using ClassDailyLand2.App.Controls;
using ClassDailyLand2.App.Integrations.Disaster;
using ClassDailyLand2.App.Integrations.Weather;
using ClassDailyLand2.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClassDailyLand2.App.Views.Pages;

/// <summary>
/// 「设置 → 天气」：开关 / 显示位置 / 定位方式 / 刷新与收起，外加**全天气动画预览**。
///
/// <para>预览区是这个功能的回归面板 —— 11 种天气各铺一格同时循环播，
/// 不用等真的下雨就能一眼看出哪种画错了；晴天拆成日 / 夜两格（太阳 vs 月牙）。</para>
/// </summary>
public partial class WeatherPage : UserControl
{
    bool _loading;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public WeatherPage()
    {
        InitializeComponent();

        // 订阅只做一次：放在 Loaded 里的话，页面反复进出会越挂越多
        _statusTimer.Tick += OnStatusTick;

        Loaded += (_, _) =>
        {
            _loading = true;
            InitShowOn();
            _loading = false;

            InitLocation();
            BuildPreview();
            UpdateStatus();

            // 定位与天气都是异步回来的，页面开着的时候每秒刷一次状态，
            // 用户点完「重新定位」能立刻看到它在换源、而不是卡住不动
            _statusTimer.Start();
        };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    void OnStatusTick(object? sender, EventArgs e)
    {
        UpdateStatus();
        // IP 结果是异步回来的：结果一到就把省/市/区刷进下拉框
        if (AutoLocateBox.IsChecked == true) ShowAutoLocation();
    }

    void InitShowOn()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var want = vm.WeatherShowOn;
        foreach (var item in ShowOnBox.Items)
            if (item is ComboBoxItem ci && (string?)ci.Tag == want)
            {
                ShowOnBox.SelectedItem = ci;
                break;
            }
    }

    void OnShowOnChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 期间也会触发一次，那时 DataContext 还没接上 —— 靠 _loading 挡掉
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (ShowOnBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            vm.WeatherShowOn = tag;
    }

    void OnRelocate(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (!vm.AutoLocate)
        {
            DialogLauncher.Toast(this, InfoBarSeverity.Warning, "重新定位",
                "自动定位是关的 —— 先把它打开，或直接改下面手选的省市");
            return;
        }
        vm.Ctx.Weather?.Relocate();
        UpdateStatus();
        DialogLauncher.Toast(this, InfoBarSeverity.Information, "重新定位", "已丢弃当前坐标，下一秒重新走一遍 IP 源");
    }

    void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.Ctx.Weather?.RefreshNow();
        UpdateStatus();
        DialogLauncher.Toast(this, InfoBarSeverity.Information, "刷新天气", "已排入下一秒的请求");
    }

    // ————————————————————— 位置（与灾害预警共用 disaster_* 那组键） —————————————————————

    const string AnyCity = "（不限）";
    const string AnyDistrict = "（不限）";

    /// <summary>上一次填进下拉框的自动定位结果（省|市|区），避免每秒重置一次选择。</summary>
    string _autoLocKey = "";

    void InitLocation()
    {
        if (DataContext is not SettingsViewModel vm) return;

        _loading = true;
        ProvinceBox.Items.Clear();
        foreach (var p in Cities.Provinces) ProvinceBox.Items.Add(p);
        ProvinceBox.Items.Add(Cities.NoWeather);            // 关掉气象预警
        SelectItem(ProvinceBox, string.IsNullOrEmpty(vm.DisasterProvince) ? "四川省" : vm.DisasterProvince);

        FillCities(vm.DisasterCity);
        FillDistricts(vm.DisasterDistrict);

        LatBox.Text = vm.DisasterLat;
        LonBox.Text = vm.DisasterLon;
        _loading = false;

        UpdateAutoLocateUi();
    }

    static void SelectItem(ComboBox box, string value)
    {
        foreach (var item in box.Items)
        {
            if (string.Equals(item?.ToString(), value, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    void FillCities(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        CityBox.Items.Clear();
        CityBox.Items.Add(AnyCity);
        foreach (var c in Cities.CitiesOf(province)) CityBox.Items.Add(c);
        SelectItem(CityBox, string.IsNullOrEmpty(keep) ? AnyCity : keep);
    }

    void FillDistricts(string keep)
    {
        var province = ProvinceBox.SelectedItem?.ToString() ?? "";
        var city = CityBox.SelectedItem?.ToString() ?? AnyCity;
        DistrictBox.Items.Clear();
        DistrictBox.Items.Add(AnyDistrict);
        foreach (var d in Cities.DistrictsOf(province, city == AnyCity ? "" : city))
            DistrictBox.Items.Add(d);
        SelectItem(DistrictBox, string.IsNullOrEmpty(keep) ? AnyDistrict : keep);
    }

    string Province => ProvinceBox.SelectedItem?.ToString() ?? "";
    string CityValue => CityBox.SelectedItem?.ToString() == AnyCity ? "" : CityBox.SelectedItem?.ToString() ?? "";
    string DistrictValue => DistrictBox.SelectedItem?.ToString() == AnyDistrict ? "" : DistrictBox.SelectedItem?.ToString() ?? "";

    void OnProvinceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillCities("");
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, "", "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnCityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        _loading = true;
        FillDistricts("");
        _loading = false;

        vm.SetDisasterLocation(Province, CityValue, "");
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    void OnDistrictChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterLocation(Province, CityValue, DistrictValue);
        LatBox.Text = "";
        LonBox.Text = "";
        ApplyCoord();
    }

    /// <summary>把（可能空着的）经纬度写回设置：空 = 按省市区查表。</summary>
    void ApplyCoord()
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SetDisasterCoord(LatBox.Text?.Trim() ?? "", LonBox.Text?.Trim() ?? "");
        UpdateStatus();
    }

    void OnAutoLocateChanged(object sender, RoutedEventArgs e) => UpdateAutoLocateUi();

    /// <summary>
    /// 自动定位开着时，**省/市/区和手填经纬度全部由 IP 结果决定** ——
    /// 这几行一起禁掉，并把解析出来的省/市/区填进下拉框。
    /// 只禁经纬度不禁省市，会出现"界面显示四川省/不限，实际用的是成都"的错觉。
    /// </summary>
    void UpdateAutoLocateUi()
    {
        var auto = AutoLocateBox.IsChecked == true;

        LatBox.IsEnabled = !auto;
        LonBox.IsEnabled = !auto;
        ApplyCoordButton.IsEnabled = !auto;
        ProvinceBox.IsEnabled = !auto;
        CityBox.IsEnabled = !auto;
        DistrictBox.IsEnabled = !auto;

        if (auto)
        {
            ShowAutoLocation();
            return;
        }

        // 关掉自动定位 → 把手选值恢复回来（否则下拉框还停在 IP 结果上，用户一保存就被覆盖）
        if (_autoLocKey.Length == 0) return;
        _autoLocKey = "";
        if (DataContext is not SettingsViewModel vm) return;

        _loading = true;
        SelectItem(ProvinceBox, vm.DisasterProvince);
        FillCities(vm.DisasterCity);
        FillDistricts(vm.DisasterDistrict);
        _loading = false;
    }

    /// <summary>把 IP 解析出来的省/市/区填进下拉框。值没变就什么都不做（这个每秒会被调一次）。</summary>
    void ShowAutoLocation()
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (ProvinceBox.Items.Count == 0) return;          // 还没初始化，下拉框是空的

        var loc = vm.Ctx.Weather?.Location;
        if (loc is null) return;
        if (!Cities.Provinces.Contains(loc.Province)) return;   // 英文/异常省名不往界面里塞

        var key = $"{loc.Province}|{loc.City}|{loc.District}";
        if (key == _autoLocKey) return;
        _autoLocKey = key;

        _loading = true;
        SelectItem(ProvinceBox, loc.Province);
        FillCities(loc.City);
        FillDistricts(loc.District);
        _loading = false;
    }

    void UpdateStatus()
    {
        if (DataContext is SettingsViewModel vm)
            StatusText.Text = vm.Ctx.Weather?.StatusText ?? "天气服务未就绪";
    }

    // ————————————————————— 动画预览 —————————————————————

    /// <summary>把全部天气铺成网格；晴天拆日 / 夜两格。</summary>
    void BuildPreview()
    {
        PreviewPanel.Children.Clear();
        foreach (var (kind, label) in WeatherArtKinds.All)
        {
            if (kind == WeatherArtKinds.Clear)
            {
                AddTile(kind, label + "（日）", true);
                AddTile(kind, label + "（夜）", false);
            }
            else
            {
                AddTile(kind, label, true);
            }
        }
    }

    void AddTile(string kind, string label, bool isDay)
    {
        var art = new WeatherArt
        {
            Kind = kind,
            IsDay = isDay,
            Width = 52,
            Height = 52,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // 颜色走令牌：之前写死 #8A8A8A，深色主题下就是一块脏底
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var stack = new StackPanel { Width = 88 };
        stack.Children.Add(art);
        stack.Children.Add(caption);

        var tile = new Border
        {
            Child = stack,
            Padding = new Thickness(10, 12, 10, 10),
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        // DynamicResource 的代码版：主题切换时跟着换，不会在浅色主题下留一块深底；
        // 描边是这格"卡片感"的来源 —— 没有它，白底上的浅色云会和页面糊成一片
        tile.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        PreviewPanel.Children.Add(tile);
    }
}
