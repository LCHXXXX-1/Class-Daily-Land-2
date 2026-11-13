using System;

namespace LiquidGlass.Core;

/// <summary>
/// 主题调色板：浅色 / 深色两套，供玻璃控件与 Demo 共用。
/// </summary>
/// <remarks>
/// 玻璃控件的绝大部分颜色来自「采样到的背景」，这里只提供少量必须由主题决定的量：
/// 文字色、边框色、色膜基色、高光色、阴影色。
/// </remarks>
public sealed class GlassPalette
{
    public required string TextPrimary { get; init; }
    public required string TextSecondary { get; init; }
    public required string TextDisabled { get; init; }
    public required string Accent { get; init; }
    public required string AccentText { get; init; }
    public required string SurfaceTint { get; init; }
    public required string Highlight { get; init; }
    public required string Shadow { get; init; }
    public required string BorderStroke { get; init; }
    public required string Scrim { get; init; }

    /// <summary>浅色主题。</summary>
    public static GlassPalette Light { get; } = new()
    {
        TextPrimary = "#FF1B1B1F",
        TextSecondary = "#992B2B31",
        TextDisabled = "#5C2B2B31",
        Accent = "#FF0A84FF",
        AccentText = "#FFFFFFFF",
        SurfaceTint = "#FFFFFFFF",
        Highlight = "#B3FFFFFF",
        Shadow = "#FF141418",
        BorderStroke = "#26FFFFFF",
        Scrim = "#4D000000",
    };

    /// <summary>深色主题。</summary>
    public static GlassPalette Dark { get; } = new()
    {
        TextPrimary = "#FFF5F5F7",
        TextSecondary = "#B0EBEBF0",
        TextDisabled = "#5CEBEBF0",
        Accent = "#FF3D9BFF",
        AccentText = "#FFFFFFFF",
        SurfaceTint = "#FF101014",
        Highlight = "#8CFFFFFF",
        Shadow = "#FF000000",
        BorderStroke = "#33FFFFFF",
        Scrim = "#66000000",
    };

    /// <summary>按外观取调色板。</summary>
    public static GlassPalette For(GlassAppearance appearance) =>
        appearance == GlassAppearance.Dark ? Dark : Light;
}
