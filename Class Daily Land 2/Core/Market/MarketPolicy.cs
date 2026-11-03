using ClassDailyLand2.UpdateContract;

namespace ClassDailyLand2.Core.Market;

/// <summary>一个市场条目相对「本机」的状态。</summary>
public enum MarketState
{
    /// <summary>本机没装。</summary>
    NotInstalled = 0,
    /// <summary>装了，版本与市场一致。</summary>
    UpToDate = 1,
    /// <summary>装了旧版本，市场有新的。</summary>
    Update = 2,
    /// <summary>本机版本比市场还新（装过测试包 / 市场回滚过）。</summary>
    Downgrade = 3,
    /// <summary>契约或主程序版本不够，装不了。</summary>
    Incompatible = 4,
    /// <summary>已下架，禁止新装。</summary>
    Revoked = 5,
}

/// <summary>市场页要显示的一行：条目 + 它相对本机的状态 + 原因。</summary>
public sealed record MarketView(MarketEntry Entry, MarketState State, string? InstalledVersion, string? Reason)
{
    /// <summary>能不能点「安装/更新/降级」。</summary>
    public bool CanInstall => State is MarketState.NotInstalled or MarketState.Update or MarketState.Downgrade;

    /// <summary>右侧按钮文案。</summary>
    public string ActionText => State switch
    {
        MarketState.NotInstalled => "安装",
        MarketState.Update => "更新",
        MarketState.Downgrade => "降级",
        MarketState.UpToDate => "已安装",
        MarketState.Incompatible => "不兼容",
        MarketState.Revoked => "已下架",
        _ => "不可用",
    };
}

/// <summary>
/// 把「远端索引」和「本机已装插件」拼成界面能直接用的视图，并负责兼容性判定。
///
/// <para>这里**不碰文件系统、不碰网络**：输入是索引 + 已装版本字典 + 三个版本号，
/// 输出是排好序的视图列表 —— 所以能当普通单元测试跑。</para>
/// </summary>
public static class MarketPolicy
{
    /// <summary>
    /// 组装视图。<paramref name="installed"/> 是「插件 id → 本机版本」。
    /// 排序：可更新 → 未安装 → 可降级 → 已安装 → 不兼容 → 已下架，同组按名称。
    /// </summary>
    public static IReadOnlyList<MarketView> Build(
        MarketIndex index,
        IReadOnlyDictionary<string, string> installed,
        string hostVersion,
        string sdkVersion,
        int hostApiVersion)
    {
        var views = new List<MarketView>(index.Plugins.Count);
        foreach (var entry in index.Plugins)
        {
            installed.TryGetValue(entry.Id, out var local);
            views.Add(Evaluate(entry, local, hostVersion, sdkVersion, hostApiVersion));
        }

        return views
            .OrderBy(v => Rank(v.State))
            .ThenBy(v => v.Entry.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>判定单个条目。规则顺序即优先级：先能不能装，再谈版本高低。</summary>
    public static MarketView Evaluate(
        MarketEntry entry,
        string? installedVersion,
        string hostVersion,
        string sdkVersion,
        int hostApiVersion)
    {
        if (entry.Revoked)
        {
            return new MarketView(entry, MarketState.Revoked, installedVersion,
                installedVersion is null ? "作者已下架" : "作者已下架，建议卸载");
        }

        if (entry.ApiVersion > hostApiVersion)
        {
            return new MarketView(entry, MarketState.Incompatible, installedVersion,
                $"需要契约 v{entry.ApiVersion}，当前 v{hostApiVersion}");
        }

        if (RequiresNewer(entry.MinAppVersion, hostVersion, out var appNeed))
        {
            return new MarketView(entry, MarketState.Incompatible, installedVersion,
                $"需要主程序 v{appNeed}，当前 v{hostVersion}");
        }

        if (RequiresNewer(entry.TargetSdkVersion, sdkVersion, out var sdkNeed))
        {
            return new MarketView(entry, MarketState.Incompatible, installedVersion,
                $"需要 SDK v{sdkNeed}，当前 v{sdkVersion}");
        }

        if (string.IsNullOrWhiteSpace(installedVersion))
            return new MarketView(entry, MarketState.NotInstalled, null, null);

        // 本机版本号解析不出来（手改过 plugin.json）时按「可更新」处理，别把人卡死
        if (!SemVer.TryParse(installedVersion, out var local))
            return new MarketView(entry, MarketState.Update, installedVersion, "本机版本号无法解析");

        var remote = entry.VersionValue;
        var state = remote > local ? MarketState.Update
                  : remote == local ? MarketState.UpToDate
                  : MarketState.Downgrade;
        return new MarketView(entry, state, installedVersion, null);
    }

    /// <summary>声明的版本门槛是否高于当前环境；没写或写错都不拦（与 PluginManager 一致）。</summary>
    static bool RequiresNewer(string? required, string current, out string needText)
    {
        needText = required?.Trim() ?? "";
        if (needText.Length == 0) return false;
        if (!SemVer.TryParse(needText, out var need)) return false;
        if (!SemVer.TryParse(current, out var have)) return false;
        return need > have;
    }

    static int Rank(MarketState state) => state switch
    {
        MarketState.Update => 0,
        MarketState.NotInstalled => 1,
        MarketState.Downgrade => 2,
        MarketState.UpToDate => 3,
        MarketState.Incompatible => 4,
        _ => 5,
    };
}
