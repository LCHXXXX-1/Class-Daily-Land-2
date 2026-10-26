using System.Globalization;
using System.Text.Json.Serialization;
namespace ClassDailyLand2.Core.Models;

/// <summary>homework.json 的元素：{"subject","content","date"}。</summary>
public sealed class HomeworkItem
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";

    [JsonIgnore]
    public string Display
    {
        get
        {
            var s = string.IsNullOrWhiteSpace(Subject) ? "" : Subject.Trim() + "：";
            return s + (Content ?? "").Trim();
        }
    }
}

/// <summary>
/// config.json：值日生游标 + 出勤人数。字段类型与 Python 版一致（should/actual 是字符串）。
/// </summary>
public sealed class DutyConfig
{
    [JsonPropertyName("current_index")] public int CurrentIndex { get; set; }
    [JsonPropertyName("last_date")] public string LastDate { get; set; } = "";
    [JsonPropertyName("should")] public string Should { get; set; } = "40";
    [JsonPropertyName("actual")] public string Actual { get; set; } = "0";

    [JsonIgnore] public int ShouldCount => ParseInt(Should, 0);
    [JsonIgnore] public int ActualCount => ParseInt(Actual, 0);

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
