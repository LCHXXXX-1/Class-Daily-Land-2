using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiquidGlass.Demo;

public partial class App : Application
{
    /// <summary>
    /// 无头验证模式：<c>--capture &lt;输出png&gt;</c> 时，窗口渲染完成后抓图并退出。
    /// </summary>
    /// <remarks>
    /// 用来在没有人工观察的场合验证"折射是否真的发生"：抓图后可直接做像素比对。
    /// 走真实窗口（而不是离屏 Measure/Arrange），因为玻璃层依赖 Loaded 事件去订阅
    /// 背景服务 —— 离屏渲染拿不到那个时机，测出来的会是空白。
    /// </remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--capture")
        {
            var output = e.Args[1];
            var mode = e.Args.Length >= 3 ? e.Args[2] : null;
            _ = mode;
            var window = new MainWindow();
            window.Show();

            // 等两帧：第一帧完成布局，第二帧背景快照与着色器才真正出图
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Capture(window, output);
                Shutdown();
            };
            timer.Start();
        }
    }

    static void Capture(Window window, string output)
    {
        var root = (FrameworkElement)window.Content;
        var w = (int)Math.Round(root.ActualWidth);
        var h = (int)Math.Round(root.ActualHeight);
        if (w <= 0 || h <= 0) { Console.Error.WriteLine("capture: 窗口尺寸为 0"); return; }

        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap(
            (int)Math.Round(w * dpi.DpiScaleX),
            (int)Math.Round(h * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var fs = File.Create(output);
        encoder.Save(fs);

        Console.WriteLine($"capture saved: {output} ({w}x{h})");
    }
}
