using ClassDailyLand2.Core.Storage;
using System.Text.Json;

namespace ClassDailyLand2.Core.Plugins;

/// <summary>
/// 插件的启用/禁用状态（<c>settings/plugins.json</c>）。
///
/// 与 settings.json 分开存：插件是「装在程序目录、数据在 settings 目录」的东西，
/// 用户重装插件不该丢启用状态；而恢复设置默认值也不该把插件全打开或全关掉。
/// </summary>
public sealed class PluginState
{
    readonly string _path;
    readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    PluginState(string path) => _path = path;

    public IReadOnlyCollection<string> DisabledIds => _disabled;

    public static PluginState Load(string path)
    {
        var state = new PluginState(path);
        if (!File.Exists(path)) return state;

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return state;
            var dto = JsonSerializer.Deserialize<StateDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (dto?.Disabled is null) return state;
            foreach (var id in dto.Disabled)
            {
                if (!string.IsNullOrWhiteSpace(id)) state._disabled.Add(id.Trim());
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 读不了就当全部启用：比起"插件全消失"，多启一个更容易被发现和修
        }
        return state;
    }

    public bool IsEnabled(string id) => !_disabled.Contains(id);

    public void SetEnabled(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (enabled) _disabled.Remove(id);
        else _disabled.Add(id);
    }

    public void Forget(string id) => _disabled.Remove(id);

    public void Save()
    {
        try
        {
            AtomicJson.Write(_path, new StateDto { Disabled = _disabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() });
        }
        catch (Exception)
        {
            // 落盘失败不能影响运行期行为：内存状态仍然生效
        }
    }

    sealed class StateDto
    {
        public List<string> Disabled { get; set; } = new();
    }
}
