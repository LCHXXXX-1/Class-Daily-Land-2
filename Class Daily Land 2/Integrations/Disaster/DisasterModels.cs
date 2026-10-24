using System.Windows.Media;

namespace ClassDailyLand.App.Integrations.Disaster;

/// <summary>一条地震事件（已归一化，多个数据源共用同一形状）。</summary>
public sealed record QuakeEvent(
    string Uid,
    string Source,          // icl / wolfx_sc / wolfx_cenc / drill …
    string EventId,
    int Revision,
    double OriginTs,        // 发震时刻（epoch 秒）
    double UpdatedTs,
    double Lat,
    double Lon,
    double Depth,
    double Magnitude,
    string Epicenter,
    double? MaxIntensity = null,
    int? ReportCount = null,
    IReadOnlyList<CountyInfo>? Counties = null);

/// <summary>ICL 详情里的逐县数据（距离/烈度/倒计时）。</summary>
public sealed record CountyInfo(
    string Name,
    string FullName,
    string City,
    double? Distance,
    double? Intensity,
    double? Countdown);

/// <summary>一条气象灾害预警。</summary>
public sealed record WeatherEvent(
    string Uid,
    string AlertId,
    double? IssuedTs,
    string Title,
    string Signal,
    string Level,
    string Province,
    string County,
    string Url);

/// <summary>本地视角的地震派生数据。</summary>
public sealed record QuakeEvaluation(
    string Uid,
    double DistanceKm,
    double LocalIntensity,
    string Level,
    double CountdownSec,
    double ArriveTs,
    double RemainSec,
    string Phase,
    bool Triggered,
    bool Displayable,
    QuakeEvent Event)
{
    public bool Incoming => Phase == "incoming";
}

/// <summary>主岛要显示的紧急倒计时（红色条 + 文案）。</summary>
public sealed record IslandEmergency(string Text, double Ratio, Color BarColor, string Level);

/// <summary>数据源可用性 / 退避状态。</summary>
public sealed class SourceStatus
{
    public SourceStatus(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
    public bool Enabled { get; set; } = true;
    public bool? Ok { get; private set; }
    public int Fail { get; private set; }
    public double LastOkTs { get; private set; }
    public string LastError { get; private set; } = "";
    public double NextRetryTs { get; private set; }

    public void OnOk(double now)
    {
        Ok = true;
        Fail = 0;
        LastOkTs = now;
        NextRetryTs = 0;
        LastError = "";
    }

    public void OnFail(string error, double now)
    {
        Ok = false;
        Fail++;
        LastError = error.Length > 80 ? error[..80] : error;
        NextRetryTs = now + Math.Min(60.0, Math.Pow(2, Math.Min(Fail, 5)) * 2.5);
    }

    public bool Ready(double now) => Enabled && now >= NextRetryTs;

    public string Describe(double now)
    {
        if (!Enabled) return $"{Label} 未启用";
        if (Ok is null) return $"{Label} 等待首次请求";
        if (Ok == true) return $"{Label} 正常（{(int)Math.Max(0, now - LastOkTs)}s前）";
        return $"{Label} 失败×{Fail}";
    }
}
