namespace ClassDailyLand.Updater;

/// <summary>
/// 更新器界面契约：编排层（<see cref="Program"/>）只认这几件事，不碰任何 WPF 类型。
/// 这样"下载 → 校验 → 清理 → 解压"的流程仍然可以在没有界面的情况下被复用
/// （比如将来做单元测试或命令行调试），界面只是挂上去的观察者。
/// </summary>
public interface IUpdateUi
{
    /// <summary>下载进度（字节数，由界面自己折算成百分比）。</summary>
    IProgress<DownloadProgressReport> Download { get; }

    /// <summary>一句话状态："正在连接服务器…"。</summary>
    void Status(string text);

    /// <summary>次要信息："12.3 / 45.6 MB"。</summary>
    void Detail(string text);

    /// <summary>进度条百分比（0..100）。</summary>
    void SetProgress(double percent);

    /// <summary>失败：界面上给红字，并把「重试」露出来。</summary>
    void Fail(string message);

    /// <summary>成功：界面显示完成态与「完成」按钮，等用户确认后再关闭。</summary>
    void Succeeded();
}
