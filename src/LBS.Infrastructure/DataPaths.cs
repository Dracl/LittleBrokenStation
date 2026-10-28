namespace LBS.Infrastructure;

/// <summary>
/// 全站唯一数据目录布局。容器内 LBS_DATA_DIR=/data，开发期默认 ./data。
/// 视频库、SQLite、cookies、缓存与 downkyicore 数据全在这里，备份根目录即备份整座分站。
/// </summary>
public sealed record DataPaths(string Root, string Videos, string Downkyi, string Cookies, string Cache)
{
    public const string RootEnvVar = "LBS_DATA_DIR";

    /// <summary>upstream 约定：downkyicore 系列组件的数据目录由 DOWNKYI_DATA_DIR 指定。</summary>
    public const string DownkyiEnvVar = "DOWNKYI_DATA_DIR";

    public string DatabaseFile => Path.Combine(Root, "station.db");

    /// <summary>评论头像等小图的本地缓存区（热链不过夜，备份 root 依旧一站式）。</summary>
    public string Avatars => Path.Combine(Root, "avatars");

    /// <summary>评论区表情（content.emote 里用到的那些）落盘区。</summary>
    public string Emotes => Path.Combine(Root, "emotes");

    /// <summary>评论区图片原图落盘区（灯箱放大用）。</summary>
    public string CmtImg => Path.Combine(Root, "cmt-img");

    /// <summary>
    /// 悬停预览成片区（首页卡片鼠标一悬就播的那段低码率无声短片），独立于 videos：
    /// 不污染视频库体积（AdminStats 全量扫 videos 算总容量）、不被回收站的前缀补扫与 ClassifyArtifact 认成产物。
    /// 派生物，随时可删可再生，所以回收站不收它。
    /// </summary>
    public string Previews => Path.Combine(Root, "previews");

    /// <summary>
    /// 回收站：删掉的馆藏文件从 videos 扰到这里，台账以清单形式封存在 recycle_bin 表。
    /// 不挂静态目录，所以删完就是真查无此片；只有站长室的回收站卡片能隔着端点看到它们。
    /// </summary>
    public string Recycle => Path.Combine(Root, "recycle");

    public static DataPaths Resolve()
    {
        var root = Environment.GetEnvironmentVariable(RootEnvVar);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Directory.GetCurrentDirectory(), "data");
        }

        root = Path.GetFullPath(root);

        var paths = new DataPaths(
            root,
            Path.Combine(root, "videos"),
            Path.Combine(root, "downkyi"),
            Path.Combine(root, "cookies"),
            Path.Combine(root, "cache"));

        foreach (var dir in new[] { paths.Root, paths.Videos, paths.Downkyi, paths.Cookies, paths.Cache, paths.Avatars, paths.Emotes, paths.CmtImg, paths.Recycle, paths.Previews })
        {
            Directory.CreateDirectory(dir);
        }

        // 让所有经由 upstream 代码的路径写入都落在 /data 内，即使未来某个组件直接读本进程环境变量。
        Environment.SetEnvironmentVariable(DownkyiEnvVar, paths.Downkyi);
        return paths;
    }
}
