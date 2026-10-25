using System.Reflection;
using System.Runtime.Loader;

namespace ClassDailyLand.App.Plugins;

/// <summary>
/// 每个插件一个（可卸载的）<see cref="AssemblyLoadContext"/>，好处有三：
/// <list type="bullet">
///   <item>插件之间、插件与宿主之间版本不打架（各自找自己目录里的依赖）；</item>
///   <item>禁用插件时能 <c>Unload()</c>，不用重启整个应用（改一次插件不用重开电脑）；</item>
///   <item>插件加载失败时上下文一起丢掉，不会污染后续加载。</item>
/// </list>
///
/// <para><b>关键一条</b>：契约程序集（<c>ClassDailyLand.Plugin.Abstractions</c>）必须返回
/// <c>null</c> 交给默认上下文解析。否则插件会加载到自己的那份契约副本，类型与宿主的不一致，
/// 表现为 <c>IPlugin</c> 永远 <c>IsAssignableFrom == false</c> —— 这是插件坑里最经典的第一个。</para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    static readonly string ContractName =
        typeof(ClassDailyLand.Plugin.IPlugin).Assembly.GetName().Name ?? "ClassDailyLand.Plugin.Abstractions";

    readonly string _directory;

    public PluginLoadContext(string pluginDirectory)
        : base("plugin:" + Path.GetFileName(pluginDirectory), isCollectible: true)
        => _directory = pluginDirectory;

    public Assembly LoadEntry(string path) => LoadFromAssemblyPath(Path.GetFullPath(path));

    protected override Assembly? Load(AssemblyName name)
    {
        if (string.Equals(name.Name, ContractName, StringComparison.OrdinalIgnoreCase)) return null;

        if (!string.IsNullOrEmpty(name.Name))
        {
            var candidate = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(candidate)) return LoadFromAssemblyPath(candidate);
        }

        // 其余（WPF / BCL / 宿主自身）一律交给默认上下文，避免重复加载框架程序集
        return null;
    }
}
