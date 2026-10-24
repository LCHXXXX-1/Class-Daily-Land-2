namespace ClassDailyLand.Plugin;

/// <summary>
/// 插件入口。宿主通过 plugin.json 里的 <c>entry</c>（形如 <c>Foo.dll</c>）
/// 与 <c>type</c>（完整类型名）反射找到实现并 <c>new</c> 出来，因此：
/// <list type="bullet">
///   <item>实现类型必须 <c>public</c> 且有无参构造函数；</item>
///   <item>一个程序集可以有多个插件类，但一个 plugin.json 只指向一个。</item>
/// </list>
///
/// 元数据（名称 / 版本 / 作者 / 描述）不在代码里，统一由 plugin.json 提供 ——
/// 这样装了但加载失败的插件也能在设置中心列出名称与版本，便于排查。
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// 加载成功后调用一次，且**只在 UI 线程**调用。
    /// 只应做注册与轻量初始化：这里做 IO / 网络会直接拖慢启动。
    /// </summary>
    void Initialize(IPluginHost host);

    /// <summary>
    /// 禁用 / 重载 / 退出前调用。返回后宿主会清掉这个插件注册的全部显示位，
    /// 之后不应期望任何回调再被触发。
    /// 不要在这里抛异常（宿主会吞掉并记账，但日志会很难看）。
    /// </summary>
    void Shutdown();
}
