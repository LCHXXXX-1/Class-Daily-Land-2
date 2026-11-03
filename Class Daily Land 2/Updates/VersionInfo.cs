namespace ClassDailyLand2.Core;

/// <summary>
/// 鍏ㄥ眬鐗堟湰鍙蜂笌閮ㄧ讲閰嶇疆鐨勫敮涓€鐪熸簮锛氬彂鐗堝彧鏀硅繖涓€澶勩€?
/// 涓荤▼搴忋€佸畨瑁呭櫒銆佹彃浠?IDE 閮界洿鎺ヨ杩欓噷鐨勫父閲忥紝
/// 涓嶅啀渚濊禆绋嬪簭闆嗗睘鎬ф垨 MSBuild 鐨?&lt;Version&gt;銆?
/// </summary>
public static class VersionInfo
{
    public const string Version = "1.7.12";

    /// <summary>涓绘洿鏂版簮锛坴ersion.json锛夈€?/summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";

    /// <summary>澶囩敤鏇存柊婧愶紙涓绘簮澶辫触鏃朵娇鐢級銆?/summary>
    public const string VersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/update/version.json";

    // 鈥斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€?鎻掍欢甯傚満 鈥斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€斺€?
    //
    // 绱㈠紩涓庡畨瑁呭寘閮芥斁鍦?plugins 鍒嗘敮涓婏紝涓嶄緷璧栦换浣曟湇鍔″櫒锛?
    //   路 涓绘簮 Gitee raw锛堝浗鍐呭揩锛夛紝澶囨簮 GitHub raw锛圙itee 鎸備簡鎵嶇敤锛?
    //   路 绱㈠紩閲岀殑 path 鏄浉瀵硅矾寰勶紝涓诲婧愬叡鐢ㄥ悓涓€鏉¤褰?鈥斺€?鎹㈠煙鍚嶄笉鐢ㄦ敼绱㈠紩
    //
    // 鈽?Gitee 鐨?/raw/ 鐜板湪浼?302 鍒?raw.giteeusercontent.com 鐨勭鍚嶅湴鍧€锛?
    //   HttpClient 蹇呴』 AllowAutoRedirect锛圡arketClient 閲屽凡寮€锛夈€?

    /// <summary>鎻掍欢甯傚満绱㈠紩锛堜富婧愶級銆?/summary>
    public const string MarketIndexUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/index.json";

    /// <summary>鎻掍欢甯傚満绱㈠紩锛堝鐢ㄦ簮锛夈€?/summary>
    public const string MarketIndexUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/index.json";

    /// <summary>瀹夎鍖呯浉瀵硅矾寰勭殑鎷兼帴鏍癸紙涓庝富婧愬搴旓級銆?/summary>
    public const string MarketRawRoot =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/";

    /// <summary>瀹夎鍖呯浉瀵硅矾寰勭殑鎷兼帴鏍癸紙涓庡鐢ㄦ簮瀵瑰簲锛夈€?/summary>
    public const string MarketRawRootFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/";

    /// <summary>涓荤▼搴?exe 鏂囦欢鍚嶃€?/summary>
    public const string ExeName = "Class Daily Land 2.exe";

    /// <summary>鏇存柊鍣ㄦ墍鍦ㄥ瓙鐩綍锛堢▼搴忕洰褰曚笅锛夈€?/summary>
    public const string UpdaterDir = "Update";

    /// <summary>璧勬簮鍖呭瓙鐩綍锛堢▼搴忕洰褰曚笅锛夈€?/summary>
    public const string AssetsDir = "assets";
}
