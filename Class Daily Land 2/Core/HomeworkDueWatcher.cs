using ClassDailyLand2.Core.Models;
using System.Text;

namespace ClassDailyLand2.Core;

/// <summary>
/// 每秒扫一遍作业的「提交时间」：
///   1. 有作业正好到点（程序在场）→ 返回给外面：打标记 + 弹 Windows 通知 + 重排作业区；
///   2. 排位（作业区分段）变了 → <see cref="LayoutChanged"/>，外面重画作业区即可。
///
/// "打标记"是调用方做的事（写进 homework.json 的 due_fired），
/// 这样重启程序不会把同一条作业再通知一遍 —— 标记在文件里，不在内存里。
/// </summary>
public sealed class HomeworkDueWatcher
{
    string? _layout;
    /// <summary>本进程已经通知过的（防止写盘失败 / 逻辑抖动导致同一秒里连弹多条）。</summary>
    readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    /// <summary>上一次扫完之后排位有变化（需要重画作业区）；第一拍只建立基线。</summary>
    public bool LayoutChanged { get; private set; }

    /// <summary>本次新到点（需要通知 + 写 due_fired）的作业，按文件顺序。</summary>
    public List<HomeworkItem> Tick(IReadOnlyList<HomeworkItem> items, DateTime now)
    {
        var justDue = new List<HomeworkItem>();
        var signature = new StringBuilder(items.Count * 24);
        var catchUp = now.AddSeconds(-HomeworkDue.CatchUpSeconds);

        foreach (var item in items)
        {
            signature.Append((int)HomeworkDue.SlotOf(item, now)).Append(':')
                     .Append(HomeworkDue.KeyOf(item)).Append('\n');

            if (item.IsLate || item.DueFiredTime is not null) continue;

            var due = item.DueTime;
            if (due is null) continue;

            // 只接"刚刚到点"的：超过宽容窗口说明当时程序不在场，按"错过了"处理（沉底）
            if (due.Value <= now && due.Value >= catchUp && _notified.Add(HomeworkDue.KeyOf(item)))
                justDue.Add(item);
        }

        if (_notified.Count > 512)
        {
            var alive = new HashSet<string>(items.Select(HomeworkDue.KeyOf), StringComparer.Ordinal);
            _notified.RemoveWhere(key => !alive.Contains(key));
        }

        var current = signature.ToString();
        LayoutChanged = _layout is not null && !string.Equals(current, _layout, StringComparison.Ordinal);
        _layout = current;
        return justDue;
    }
}
