using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using LiquidGlass.Core;
using LiquidGlass.Rendering.Wpf;

namespace LiquidGlass.Demo;

public partial class MainWindow : Window
{
    static readonly string[] SegmentNames = { "课堂", "日程", "统计" };

    long _frames;
    long _lastTicks;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public MainWindow()
    {
        InitializeComponent();

        // 背景源显式指定（不要用 XAML 绑定：绑定一旦失效会退回根容器 → 自截）
        GlassBackdrop.SetSource(Backdrop, Backdrop);

        // 自由拖动的那块：胶囊形，折射给足一点才看得出"液态"
        var free = GlassMaterialPresets.Create(GlassMaterial.Clear, GlassAppearance.Light, 20);
        free.CornerRadius = 44;
        free.CornerSmoothing = 0.9;
        free.Dispersion = 0.075;
        free.RimStrength = 0.85;
        free.BackdropScale = 1.0;
        free.ShadowLift = 0.02;
        free.RefractionBand = 16;
        free.Dispersion = 0.04;
        free.RimWidth = 1.0;
        // 调参用：--refr=60 --band=22 --disp=0.02 --rim=0.9
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2) continue;
            var v = double.TryParse(parts[1], out var d) ? d : double.NaN;
            switch (parts[0])
            {
                case "--refr": free.RefractionPixels = v; break;
                case "--band": free.RefractionBand = v; break;
                case "--disp": free.Dispersion = v; break;
                case "--rim": free.RimStrength = v; break;
                case "--lift": free.ShadowLift = v; break;
            }
        }
        FreeLens.Material = free;

        // 分段指示器：小一些，折射按比例收小（否则整块会被吸变形）
        var seg = GlassMaterialPresets.Create(GlassMaterial.Clear, GlassAppearance.Light, 12);
        seg.CornerRadius = 32;
        seg.CornerSmoothing = 0.9;
        seg.Dispersion = 0.04;
        seg.RimStrength = 0.85;
        seg.BackdropScale = 1.0;
        seg.ShadowLift = 0.02;
        seg.RefractionBand = 12;
        seg.RimWidth = 1.0;
        SegmentLens.Material = seg;

        SegmentLens.SelectedIndexChanged += (_, index) =>
            SegmentHint.Text = "当前：" + SegmentNames[Math.Clamp(index, 0, 2)];

        Loaded += (_, _) =>
        {
            // 先把背景快照抓下来，避免第一帧玻璃是空的
            GlassBackdrop.For(this).SampleNow();
            FreeLens.InvalidateVisual();
        };

        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// 点轨道上任意位置都切到最近的档位（否则要点中玻璃本身才响应，
    /// 而玻璃在最左档时右边一大片都是"死区"）。
    /// </summary>
    void OnSegmentTrackClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var p = e.GetPosition(SegmentTrack);
        var index = (int)Math.Round(p.X / SegmentTrack.ActualWidth * (SegmentLens.SnapDivisions - 1));
        SegmentLens.SelectedIndex = Math.Clamp(index, 0, SegmentLens.SnapDivisions - 1);
    }

    void OnRendering(object? sender, EventArgs e)
    {
        _frames++;
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastTicks < 500) return;

        var fps = _frames * 1000.0 / Math.Max(1, now - _lastTicks);
        _lastTicks = now;
        _frames = 0;
        Perf.Text = $"WPF ShaderEffect · PS2.0 · {fps:F0} fps";
    }
}
