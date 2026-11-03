namespace ClassDailyLand2.Core.Diagnostics;

/// <summary>
/// 报错报告的落盘载荷：主程序把它写到 logs\report-*.json，
/// 再拉起一个自身副本（--error-report）去显示。
///
/// <para>为什么要落盘而不是只传内存对象：主程序可能在"随时会挂"的状态下报错，
/// 报告必须先在磁盘上活下来，窗口挂了也还能再开一次。</para>
/// </summary>
public sealed class ErrorPayload
{
    public string Kind { get; set; } = "";
    public string Module { get; set; } = "";
    public string Code { get; set; } = "";
    public string CodeName { get; set; } = "";
    public string CodeMeaning { get; set; } = "";
    public string Level { get; set; } = "ERROR";
    public string Detail { get; set; } = "";
    public string? Snapshot { get; set; }
    /// <summary>主程序的进程号（报信进程重启/重装时要找到并结束它）。</summary>
    public int MainPid { get; set; }
    /// <summary>数据根目录（运行标记所在处）。</summary>
    public string DataRoot { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Version { get; set; } = "";
}
