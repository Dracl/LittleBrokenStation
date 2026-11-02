namespace LBS.Infrastructure;

/// <summary>
/// 封面缩略档位表：全站唯一的「切哪几档、文件叫什么名」口径。
/// 前端 <c>web/src/utils/coverSrc.js</c> 里的 COVER_TIERS 与本表一字对齐，改一处必须同步改另一头。
///
/// 全部按 16:10 切（站长 11-03 定：全站封面卡片位统一 16:10，与首页卡阵、合集抽屉以及 B 站投稿规范对齐）。
///
/// 铁律（站长 11-03原话：「缩略图宁愿大一点缩小，也不要小一点放大」）：
/// 任何一档都不许把原图拉大。它同时在两头把守——后端在 <see cref="CoverThumbs.TryBuildAsync"/> 逐档收手，
/// 前端在 <c>utils/coverSrc.js</c> 的 pickTier 里「五档都不够宽就不给档位、退回原图」。
/// 只改一头都会漏：后端不拦就会切出放大件，前端不拦就会把切小的档拉到显示尺寸上放大。
/// </summary>
public static class CoverThumbTiers
{
    public const string FormatWebp = "webp";
    public const string FormatJpg = "jpg";

    /// <summary>
    /// 切片算法版本，嵌在版本戳尾部。判据一变就 +1（a2：把「不放大」从只看宽补成「宽高都不许放大」）。
    /// <para>
    /// 为什么非有它不可：已落盘的件靠同名存在就直接跳过不重切，改了判据存量永远追不上新规矩。
    /// 戳一变旧行就落后，<c>ListVideosNeedingCoverThumbs</c> 把它们重新点名，<c>TryBuildAsync</c> 里的
    /// <c>PruneStaleFiles</c> 再把旧件扫干净——存量自己追平，不必人工清盘也不靠站长按钮。
    /// </para>
    /// </summary>
    public const string AlgoVersion = "a2";

    /// <summary>一枚档位：显示框的 2 倍（DPR 2）像素尺幅，全部 16:10。</summary>
    public readonly record struct Tier(int Width, int Height)
    {
        /// <summary>档位令牌，同时是台账里 tier 列与文件名里那一段，例如 352x220。</summary>
        public string Token => $"{Width}x{Height}";
    }

    /// <summary>五档由小到大。前端按「显示宽 × DPR」从这张表里挑最不浪费的一档。</summary>
    public static readonly Tier[] All =
    [
        new(160, 100), // 安利弹窗片缩略、站长室下载队列
        new(352, 220), // 馆藏壁卡（176×110）、接下来播放、历史页、周报热榜、大幕下方 tab
        new(512, 320), // 首页卡阵（≈257×161）、分区横滚行、抽屉
        new(768, 480), // 动态视频卡（≈576 宽）、首页三张入口卡
        new(1280, 800), // 首页大幕、周报头图
    ];

    /// <summary>
    /// 缩略文件名：<c>&lt;bvid&gt;_&lt;档&gt;-&lt;版本&gt;.&lt;后缀&gt;</c>，例如 <c>BV1xx411c7mD_352x220-63890123-3f2a1.webp</c>。
    /// <para>
    /// 两个设计点都是硬要求，不许顺手改：
    /// 一、BV 号必须独立成段（前后是 <c>_</c> 或 <c>.</c>）——私有稿件反查在台账之外还留着「从文件名里抠
    /// 整段 12 位 BV 号」那条腿（<c>StationDb.Visibility.cs</c> 的 BvidInFileName），名字糊在一起
    /// （比如 <c>BV1xx@352x220.webp</c>）会让那段抠不出来，闸认不出归属就 fail-open 放行。
    /// 二、版本段进文件名而不是进 <c>?v=</c>——这样同一个 URL 的内容永不改变，<c>/thumbs</c> 才敢挂
    /// <c>immutable</c> 长缓存；原图重拉过一次版本就变，旧缩略自动作废，不会把旧封面钉死在浏览器里。
    /// </para>
    /// </summary>
    public static string FileName(string bvid, Tier tier, string stamp, string format)
        => $"{bvid}_{tier.Token}-{stamp}.{format}";

    /// <summary>
    /// 原图的版本戳：<c>&lt;mtimeTicks 十六进制&gt;-&lt;字节数 十六进制&gt;-&lt;算法版本&gt;</c>。
    /// 前两段与 <see cref="MediaCachePolicy"/> 的 ETag 同一个思路——原地换主必然改这两个数，
    /// 拿它当文件名的一段，天然做到「内容变了地址就变」，不必额外记批次；
    /// 第三段是算法版本，让切片规矩的变更也能追到存量（见 <see cref="AlgoVersion"/>）。
    /// </summary>
    public static string StampOf(long lastWriteUtcTicks, long length)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{lastWriteUtcTicks:x}-{length:x}-{AlgoVersion}");
}
