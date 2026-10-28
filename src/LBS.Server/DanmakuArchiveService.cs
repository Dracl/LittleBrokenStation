using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.DanmakuApi;
using DownKyi.Core.BiliApi.DanmakuApi.Models;
using DownKyi.Core.Danmaku2Ass;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>B 站弹幕重拉结果：Added 是这次新收进来的，Total 是留档总条数。</summary>
public sealed record DanmakuArchiveOutcome(bool Ok, string Message, int Added, int Total, int Pages);

/// <summary>
/// B 站弹幕快照的增量归档：只进不退。
/// 每个分 P 旁边多一份原始清单 {base}.danmaku.json（带 B 站弹幕 id），重拉是把 B 站现在的弹幕与清单并集，
/// B 站那边被删掉、被撤回的弹幕继续留在档里，绝不因为「刷新」而变薄。
/// 分站朋友发的站内弹幕住在 danmaku_local 表，这条链路一个字节都不碰。
/// 祖传 ass（没有清单、当年下载引擎直接生成的）只做追加：老行的颜色、字号、模式一律原样保留，
/// 免得一次重拉把老弹幕洗成默认样式。落盘一律先写临时件、验货过再原子替换。
/// </summary>
public sealed class DanmakuArchiveService(
    StationDb db,
    DataPaths paths,
    LbsQueryClient client,
    LbsCollectPolicy policy,
    ISettingsStore settings,
    ILogger<DanmakuArchiveService> logger)
{
    /// <summary>重拉一条馆藏的全部分 P；B 站空手而归、缺 cid、被占用都不算炸，只是回报里说清楚。</summary>
    public async Task<DanmakuArchiveOutcome> TryRefreshAsync(long videoId, CancellationToken cancellationToken)
    {
        try
        {
            var brief = db.GetVideoBrief(videoId);
            if (brief is null)
            {
                return new DanmakuArchiveOutcome(false, "馆藏里没有这个 id", 0, 0, 0);
            }

            if (brief.Value.Status != VideoStatus.Arrived)
            {
                return new DanmakuArchiveOutcome(false, "这条还没到站，等成片落盘再拉弹幕", 0, 0, 0);
            }

            if (brief.Value.Aid is not { } aid || aid <= 0)
            {
                return new DanmakuArchiveOutcome(false, "还没有 av 号，先补采一次评论（它会顺带把 av 号解出来）", 0, 0, 0);
            }

            var targets = db.GetDanmakuPageTargets(videoId);
            if (targets.Count == 0)
            {
                return new DanmakuArchiveOutcome(false, "还没有分 P 记录，弹幕无处安放", 0, 0, 0);
            }

            var (added, total, pages, empty, noCid, locked) = await policy.RunForVideoAsync(
                videoId,
                () => PullAsync(aid, brief.Value.Bvid, targets, cancellationToken));

            if (pages == 0)
            {
                var why = empty > 0
                    ? "B 站这边空手而归（多半是风控或这条真没弹幕），留档原样保留"
                    : noCid > 0
                        ? "分 P 缺少 cid，B 站没法按它取弹幕"
                        : locked > 0
                            ? "弹幕文件正被播放器占着，等它松手再试"
                            : "B 站这次没有新东西可收";
                return new DanmakuArchiveOutcome(false, $"「{brief.Value.Bvid}」弹幕没刷动：{why}", 0, 0, 0);
            }

            db.FinalizeArtifacts(videoId, paths.Videos);
            logger.LogInformation(
                "馆藏 {VideoId} B 站弹幕增量归档：新增 {Added} 条，留档 {Total} 条，{Pages} 个分 P 落盘（空手 {Empty}，缺 cid {NoCid}，占用 {Locked}）",
                videoId, added, total, pages, empty, noCid, locked);
            var tail = empty > 0 || noCid > 0 || locked > 0
                ? $"（{empty} 个分 P 空手，{noCid} 个缺 cid，{locked} 个被占用）"
                : "";
            return new DanmakuArchiveOutcome(
                true,
                $"「{brief.Value.Bvid}」B 站弹幕已增量更新：新增 {added} 条，留档共 {total} 条{tail}",
                added,
                total,
                pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "馆藏 {VideoId} 弹幕增量归档炸了（不影响馆藏）", videoId);
            return new DanmakuArchiveOutcome(false, "弹幕归档出错，分站日志里有详情", 0, 0, 0);
        }
    }

    private async Task<(int Added, int Total, int Pages, int Empty, int NoCid, int Locked)> PullAsync(
        long aid,
        string bvid,
        IReadOnlyList<(int Idx, long? Cid, string BasePath)> targets,
        CancellationToken cancellationToken)
    {
        var dm = settings.Current.Danmaku;
        var config = new Config
        {
            Title = bvid,
            ScreenWidth = dm.ScreenWidth,
            ScreenHeight = dm.ScreenHeight,
            FontName = dm.FontName,
            BaseFontSize = dm.FontSize,
            LineCount = dm.LineCount,
            LayoutAlgorithm = LayoutAlgorithmValue(dm.LayoutAlgorithm),
            TuneDuration = 0,
            DropOffset = 0,
            BottomMargin = 0,
            CustomOffset = 0,
        };

        // 过滤口径跟下载那趟一致（站长设的屏蔽词/屏蔽 uid 照旧生效）。
        var converter = new BilibiliDanmakuConverter()
            .SetTopFilter(dm.TopFilter == AllowStatus.Yes)
            .SetBottomFilter(dm.BottomFilter == AllowStatus.Yes)
            .SetScrollFilter(dm.ScrollFilter == AllowStatus.Yes)
            .SetCustomFilter(
                dm.RemoveEmojiAndSpecialCharacters == AllowStatus.Yes,
                dm.BlockedKeywords,
                dm.BlockedSenderUids);

        var added = 0;
        var total = 0;
        var pages = 0;
        var empty = 0;
        var noCid = 0;
        var locked = 0;

        foreach (var (_, cid, basePath) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cid is not { } cidValue || cidValue <= 0)
            {
                noCid++;
                continue;
            }

            var assPath = basePath + ".ass";
            var xmlPath = basePath + ".xml";
            var sidecarPath = basePath + SidecarExtension;
            var archived = DanmakuArchiveStore.Load(sidecarPath);
            var known = archived?.ToDanmakus() ?? [];
            // 祖传 ass 的老行数量要在动文件之前数好：这个数一旦丢了，下次就会走整档重生成，
            // 把当年下载引擎写下的颜色、字号、模式洗成默认样式。
            var legacyLines = archived?.LegacyDialogue ?? (File.Exists(assPath) ? CountDialogue(assPath) : 0);
            var fetched = await converter.GetDanmakusAsync(client, aid, cidValue, cancellationToken);

            if (fetched.Count == 0 && (known.Count > 0 || File.Exists(assPath)))
            {
                // B 站抽风/风控：留档一个字都不动。
                empty++;
                continue;
            }

            var union = MergeById(known, fetched).OrderBy(d => d.Progress).ToList();

            // 只追加还是整档重生成，看的是「ass 里有没有本站没存过原始字段的祖传行」，
            // 不是「有没有清单」——祖传档第一次刷完就有了清单，第二次刷绝不能把它重写成只剩清单里那些。
            var appendOnly = AppendsOnly(archived, File.Exists(assPath));
            var pageAdded = appendOnly
                ? TryAppendNewLines(assPath, converter.CreateAss(config, union, cancellationToken), cancellationToken)
                : TryWriteFull(assPath, xmlPath, converter, config, union, known.Count, cancellationToken);

            if (pageAdded < 0)
            {
                locked++;
                continue;
            }

            // 清单要在文件落盘成功之后才更新：文件没换成，清单也别抢跑。
            DanmakuArchiveStore.Save(sidecarPath, new DanmakuArchiveFile(1, legacyLines, union.Select(RawDanmaku.From).ToList()));

            added += pageAdded;
            total += union.Count;
            pages++;
        }

        return (added, total, pages, empty, noCid, locked);
    }

    /// <summary>本站自己生成的档：按并集整档重生成 ass（有 xml 就一并刷）。返回新增几条，-1 是没写成。</summary>
    private int TryWriteFull(
        string assPath,
        string xmlPath,
        BilibiliDanmakuConverter converter,
        Config config,
        IReadOnlyList<BiliDanmaku> union,
        int knownCount,
        CancellationToken cancellationToken)
    {
        var ass = converter.CreateAss(config, union, cancellationToken);
        if (!IsUsableAss(ass))
        {
            logger.LogWarning("馆藏 ass 验货不过，保留旧档：{Path}", assPath);
            return -1;
        }

        var ok = TryWriteAtomic(assPath, ass, cancellationToken);
        if (File.Exists(xmlPath))
        {
            var xml = BilibiliDanmakuXmlWriter.CreateDocument(union, cancellationToken)
                .ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
            if (IsUsableXml(xml))
            {
                // xml 是当年下载引擎的伴生产物，前端不读它；刷不动也不算这一 P 失败。
                TryWriteAtomic(xmlPath, xml, cancellationToken);
            }
        }

        return ok ? Math.Max(union.Count - knownCount, 0) : -1;
    }

    /// <summary>
    /// 祖传 ass：只把「原文件里没有的那几行」追加到 [Events] 末尾。
    /// 老行原样不动，所以下载引擎当年写下的颜色、字号、模式不会被洗成默认样式。
    /// </summary>
    private int TryAppendNewLines(string assPath, string generatedAss, CancellationToken cancellationToken)
    {
        var original = File.ReadAllText(assPath, Encoding.UTF8);
        var (text, added) = ComposeAppendedAss(original, generatedAss);
        if (added < 0)
        {
            logger.LogWarning("祖传 ass 连头部都没有，不敢往里追加：{Path}", assPath);
            return -1;
        }

        if (added == 0)
        {
            // B 站这次的弹幕原样都在档里，一个字不用改。
            return 0;
        }

        return TryWriteAtomic(assPath, text, cancellationToken) ? added : -1;
    }

    /// <summary>
    /// 追加正文的纯逻辑：把 generated 里「original 没有的那几行 Dialogue」接在原文末尾。
    /// 整行比对，所以同一条弹幕（时间/样式/文本都一样）生成两次也只会追一次。
    /// Added = -1 表示原文连个 ass 样子都没有，不该动它。
    /// </summary>
    internal static (string Text, int Added) ComposeAppendedAss(string original, string generatedAss)
    {
        if (!IsUsableAss(original))
        {
            return (original, -1);
        }

        var existing = DialogueLines(original).ToHashSet(StringComparer.Ordinal);
        var fresh = DialogueLines(generatedAss).Where(line => !existing.Contains(line)).ToList();
        if (fresh.Count == 0)
        {
            return (original, 0);
        }

        return (original.TrimEnd('\r', '\n') + Environment.NewLine + string.Join(Environment.NewLine, fresh), fresh.Count);
    }

    /// <summary>
    /// 这一 P 是不是只能追加：祖传 ass（清单里记着 LegacyDialogue &gt; 0，或根本还没清单但盘上有老 ass）走追加，
    /// 本站自己从头生成的档才整档重生成。
    /// </summary>
    internal static bool AppendsOnly(DanmakuArchiveFile? archived, bool assExists) =>
        (archived?.LegacyDialogue ?? 0) > 0 || (archived is null && assExists);

    /// <summary>按 B 站弹幕 id 并集；id 不可用（0）时退化到「出现时间 + 文本」这个次级钥匙。</summary>
    internal static IReadOnlyList<BiliDanmaku> MergeById(IReadOnlyList<BiliDanmaku> archived, IReadOnlyList<BiliDanmaku> fetched)
    {
        var merged = new List<BiliDanmaku>(archived);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var danmaku in archived)
        {
            seen.Add(KeyOf(danmaku));
        }

        foreach (var danmaku in fetched)
        {
            if (seen.Add(KeyOf(danmaku)))
            {
                merged.Add(danmaku);
            }
        }

        return merged;
    }

    private static string KeyOf(BiliDanmaku danmaku) => danmaku.Id > 0
        ? $"id:{danmaku.Id}"
        : $"tc:{danmaku.Progress}|{danmaku.Content}";

    /// <summary>把 ass 文本里的 Dialogue 行原样取出来（整行比对，同一条弹幕生成出来必然一模一样）。</summary>
    internal static IEnumerable<string> DialogueLines(string ass) =>
        ass.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal));

    /// <summary>旧档里有多少条弹幕（Dialogue 行）；文件不在或读不动给 -1，当作没得比较。</summary>
    internal static int CountDialogue(string assPath)
    {
        if (!File.Exists(assPath))
        {
            return -1;
        }

        try
        {
            using var reader = new StreamReader(assPath, Encoding.UTF8);
            var lines = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("Dialogue:", StringComparison.Ordinal))
                {
                    lines++;
                }
            }

            return lines;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    /// <summary>先写临时文件再原子替换：中途炸了旧档还在，播放器顶多读到上一版。</summary>
    private bool TryWriteAtomic(string target, string content, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var staging = target + ".refreshing";
            File.WriteAllText(staging, content, new UTF8Encoding(false));
            if (File.Exists(target))
            {
                File.Replace(staging, target, null);
                return true;
            }

            File.Move(staging, target);
            return true;
        }
        catch (IOException)
        {
            // 正被播放器/杀软占着：旧档原样留着，回报里说一声就好。
            return false;
        }
    }

    /// <summary>ass 验货：头部模板与事件段都得在。</summary>
    internal static bool IsUsableAss(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("[Script Info]", StringComparison.Ordinal)
        && text.Contains("[Events]", StringComparison.Ordinal);

    /// <summary>xml 验货：B 站那副 &lt;i&gt;&lt;d p="…"&gt; 的壳子得在。</summary>
    internal static bool IsUsableXml(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("<i>", StringComparison.Ordinal)
        && text.Contains("<d ", StringComparison.Ordinal);

    /// <summary>排版算法 → Danmaku2Ass 认的字符串（对齐引擎自家映射）。</summary>
    internal static string LayoutAlgorithmValue(DanmakuLayoutAlgorithm algorithm) => algorithm switch
    {
        DanmakuLayoutAlgorithm.None => "none",
        DanmakuLayoutAlgorithm.Async => "async",
        DanmakuLayoutAlgorithm.Sync => "sync",
        _ => "async",
    };

    /// <summary>原始弹幕清单的落盘后缀（跟 ass/xml 并排放在分 P 旁边）。</summary>
    internal const string SidecarExtension = ".danmaku.json";

    /// <summary>清单里的一条原始弹幕（BiliDanmaku 的可序列化替身，字段一一对上）。</summary>
    public sealed record RawDanmaku(
        long Id,
        int Progress,
        int Mode,
        int Fontsize,
        uint Color,
        string MidHash,
        string Content,
        long Ctime,
        int Weight,
        int Pool)
    {
        public BiliDanmaku ToDanmaku() => new()
        {
            Id = Id,
            Progress = Progress,
            Mode = Mode,
            Fontsize = Fontsize,
            Color = Color,
            MidHash = MidHash ?? string.Empty,
            Content = Content ?? string.Empty,
            Ctime = Ctime,
            Weight = Weight,
            Pool = Pool,
        };

        public static RawDanmaku From(BiliDanmaku danmaku) => new(
            danmaku.Id,
            danmaku.Progress,
            danmaku.Mode,
            danmaku.Fontsize,
            danmaku.Color,
            danmaku.MidHash,
            danmaku.Content,
            danmaku.Ctime,
            danmaku.Weight,
            danmaku.Pool);
    }

    /// <summary>
    /// {base}.danmaku.json：这个分 P 的弹幕留档。
    /// LegacyDialogue &gt; 0 表示 ass 里有当年下载引擎直接写的老行（本站没有它们的原始字段），
    /// 这类档永远只做追加，不整档重生成。
    /// </summary>
    public sealed record DanmakuArchiveFile(int Schema, int LegacyDialogue, IReadOnlyList<RawDanmaku> Danmakus)
    {
        public IReadOnlyList<BiliDanmaku> ToDanmakus() => Danmakus.Select(r => r.ToDanmaku()).ToList();
    }

    /// <summary>清单读写：读坏了当没有（宁可退回追加模式，也不要把留档当空档）。</summary>
    internal static class DanmakuArchiveStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static DanmakuArchiveFile? Load(string sidecarPath)
        {
            if (!File.Exists(sidecarPath))
            {
                return null;
            }

            try
            {
                var file = JsonSerializer.Deserialize<DanmakuArchiveFile>(File.ReadAllText(sidecarPath), Json);
                return file is { Danmakus.Count: >= 0 } ? file with { Danmakus = file.Danmakus ?? [] } : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static void Save(string sidecarPath, DanmakuArchiveFile file)
        {
            var directory = Path.GetDirectoryName(sidecarPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
        }
    }
}
