namespace LBS.Infrastructure;

/// <summary>
/// 全站唯一数据目录布局。容器内 LBS_DATA_DIR=/data，开发期默认 ./data
/// （工作目录下没库时会沿祖先目录往上找现成的那座，见 <see cref="ResolveDataRoot"/>）。
/// 视频库、SQLite、cookies、缓存与 downkyicore 数据全在这里，备份根目录即备份整座分站。
/// </summary>
public sealed record DataPaths(string Root, string Videos, string Downkyi, string Cookies, string Cache)
{
    public const string RootEnvVar = "LBS_DATA_DIR";

    /// <summary>upstream 约定：downkyicore 系列组件的数据目录由 DOWNKYI_DATA_DIR 指定。</summary>
    public const string DownkyiEnvVar = "DOWNKYI_DATA_DIR";

    public string DatabaseFile => Path.Combine(Root, DatabaseFileName);

    /// <summary>
    /// 数据根是「推」出来的时候才非空（只见于没设 <see cref="RootEnvVar"/>、而工作目录下又没库的情形）。
    /// 开站日志把它念出来，免得下次又有人盯着座空站以为数据没了——那是开错了库，不是数据丢了。
    /// </summary>
    public string? RootHint { get; init; }

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

    /// <summary>
    /// 封面缩略件区（站长 11-03「缩略图要本站自己从本地原图裁，不许靠 B 站 CDN 缩放」）。
    /// 与 <see cref="Previews"/> 同理独立于 videos：不被 ClassifyArtifact 认成产物（否则会被插成第二行
    /// kind='cover'，而全站读侧取的是最早那一行）、不被 IsMediaMissing 当必备产物验盘（否则档位文件
    /// 没落全就把整条稿件 ResetToPending 重下正片）、不进回收站前缀补扫、也不进 AdminStats 的容量账。
    /// 派生物，随时可删可再生。
    /// </summary>
    public string Thumbs => Path.Combine(Root, "thumbs");

    /// <summary>
    /// 切片中的临时件区。名字以点开头是有意的：<c>/thumbs</c> 静态区的半成品闸会把含 <c>/.</c> 的路径一律 404，
    /// 切到一半的缩略图永不可能从静态区溜出去。
    /// </summary>
    public string ThumbsStaging => Path.Combine(Root, "thumbs", ".staging");

    /// <summary>
    /// UP 历史脸档案区（周巡脸员每周从 card 口取一张脸存这里）：与 <see cref="Avatars"/> 分开一座——
    /// 那座是「评论区当前在用的脸」（同图异处共用、sha1 命名天然去重），这座是「某人历史上存过的脸」，
    /// 台账在 up_faces 表，件名依旧走 AvatarFileName 那套 sha1 规矩。
    /// 同样是派生物：不进 videos、不进 artifacts、不进 AdminStats 的容量账，随时可删可再生（只是脸得重下）。
    /// </summary>
    public string UpFaces => Path.Combine(Root, "up_faces");

    public static DataPaths Resolve()
    {
        var (root, hint) = ResolveDataRoot(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable(RootEnvVar));
        root = Path.GetFullPath(root);

        var paths = new DataPaths(
            root,
            Path.Combine(root, "videos"),
            Path.Combine(root, "downkyi"),
            Path.Combine(root, "cookies"),
            Path.Combine(root, "cache"))
        {
            RootHint = hint,
        };

        foreach (var dir in new[] { paths.Root, paths.Videos, paths.Downkyi, paths.Cookies, paths.Cache, paths.Avatars, paths.Emotes, paths.CmtImg, paths.Recycle, paths.Previews, paths.Thumbs, paths.ThumbsStaging, paths.UpFaces })
        {
            Directory.CreateDirectory(dir);
        }

        // 让所有经由 upstream 代码的路径写入都落在 /data 内，即使未来某个组件直接读本进程环境变量。
        Environment.SetEnvironmentVariable(DownkyiEnvVar, paths.Downkyi);
        return paths;
    }

    /// <summary>
    /// 没设 <see cref="RootEnvVar"/> 时数据根到底落哪儿，全由这条说了算（拆成纯函数是为了能测）：
    /// 1. 设了环境变量就照它说，容器与手工指定都不受影响；
    /// 2. 工作目录下 ./data 里已有 station.db，跟从前一样跟着工作目录走；
    /// 3. 否则沿祖先目录往上找最近一个带 station.db 的 data，到仓库根（.git）为止、最多上溯 <see cref="ProbeMaxHops"/> 层；
    /// 4. 哪儿都没库（首次开机）才回到 ./data 新建，行为与从前一致。
    /// 第 3 段专治这一类事故：`dotnet run --project src/LBS.Server` 会把工作目录切到项目目录，
    /// 照老写法就当场在 src/LBS.Server 下自举出一座空站，站长看到的是「数据没了」。
    /// </summary>
    internal static (string Root, string? Hint) ResolveDataRoot(string cwd, string? envOverride)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return (envOverride, null);
        }

        var local = Path.Combine(cwd, "data");
        if (File.Exists(Path.Combine(local, DatabaseFileName)))
        {
            return (local, null);
        }

        var probed = ProbeUpward(cwd);
        return probed is null
            ? (local, null)
            : (probed, $"工作目录 {cwd} 下没有现成的库，取的是往上找到的这座；想钉死就设 {RootEnvVar}");
    }

    private const string DatabaseFileName = "station.db";
    private const int ProbeMaxHops = 6;

    /// <summary>沿祖先目录往上找 data/station.db：认库不只认目录，免得抓个同名的空壳当库；到仓库根就收手。</summary>
    private static string? ProbeUpward(string cwd)
    {
        var dir = Directory.GetParent(Path.GetFullPath(cwd));
        for (var hop = 0; dir is not null && hop < ProbeMaxHops; hop++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, DatabaseFileName)))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return null;
            }
        }

        return null;
    }
}
