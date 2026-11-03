using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassDailyLand2.MarketContract;

namespace DeveloperTool.Market;

/// <summary>
/// 发布通道：BuildAndPack 产出的 .cblplugin → POST /api/files/upload。
/// HTTP 细节（multipart、Token 注入、错误码）全在共享的 <see cref="MarketApiClient"/>，
/// 这里只做发布前的本地检查，让 MainWindow 的 catch 分支干净。
/// </summary>
public sealed class MarketUploadService
{
    readonly MarketAuthService _auth;

    public MarketUploadService(MarketAuthService auth) => _auth = auth;

    /// <summary>
    /// 上传编译产物。失败抛 <see cref="MarketApiException"/>
    /// （403 = 需先设用户名、409 = 版本重复，MainWindow 按码分文案）。
    /// </summary>
    public async Task<MarketFileEntry> PublishAsync(string packagePath, Action<string> log, CancellationToken ct = default)
    {
        if (!_auth.IsLoggedIn)
            throw new MarketApiException(0, "尚未登录插件市场");
        if (!File.Exists(packagePath))
            throw new MarketApiException(0, "找不到编译产物：" + packagePath);

        var sizeMb = new FileInfo(packagePath).Length / 1024.0 / 1024.0;
        if (sizeMb > 64)
            throw new MarketApiException(0, $"包体积 {sizeMb:F1} MB，超过后端 64MB 上限");

        log($"上传 {Path.GetFileName(packagePath)}（{sizeMb:F1} MB）…");
        var file = await _auth.Api.UploadPluginAsync(packagePath, ct).ConfigureAwait(false);
        log($"上传成功：{file.PackageName} v{file.Version}（status={file.Status}）");
        return file;
    }
}
