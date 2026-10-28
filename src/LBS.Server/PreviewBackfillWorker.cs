using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 悬停预览补员：给到站却没预览片的视频，用本机 ffmpeg 从 data/videos 的成片转一段低码率无声短片，
/// 落 data/previews/&lt;bvid&gt;.mp4（独立目录，不进视频库、不进 artifacts），前端经 /previews 静态挂载直接播。
/// 节拍照封面补员：上岗 30 秒补首轮，之后每小时一轮，每轮封顶按设置（默认 40）、条间 300ms；单件失手只记账，不中断整轮。
/// 临时件写 &lt;bvid&gt;.mp4.part，ffprobe 验过货才 File.Move 转正，再写台账行——先文件后行，半成品永远看不见。
/// 没有 tmp 目录，也没有全局开机清扫：开机只清一处——首轮开工前把 data/previews/*.part 扫掉
/// （开机瞬间不可能有在途生成，凡 .part 皆上次崩溃的尸首）。
/// 转码是 CPU 活，NAS 上 ffmpeg 会拖慢下载：所以每条开工前先看看下载面板有没有事，有就歇 60 秒再看，连让 20 次本轮就收工。
/// </summary>
public sealed class PreviewBackfillWorker(
    StationDb db,
    DataPaths paths,
    PreviewSettings settings,
    DownloadPauseBoard pauseBoard,
    ILogger<PreviewBackfillWorker> logger) : BackgroundService
{
    private const int YieldLimit = 20;
    private const int YieldPauseSeconds = 60;

    private readonly SemaphoreSlim roundGate = new(1, 1);

    // 全局转码串行闸：并发度取站长设定（1..32，日常建议 1～4：每路 ffmpeg 吃一个核，开高了会把下载与页面应答一起拖慢）。换闸只发生在轮次开头（roundGate 在手，此刻绝无在途转码），
    // 所以不用锁；也不去蹭 vendor 的 AsyncConcurrencyGate（internal 不说，那闸是下载队的，不跟它合流抢）。
    private SemaphoreSlim encodeGate = new(PreviewSettings.ConcurrencyDefault, PreviewSettings.ConcurrencyMax);
    private int appliedConcurrency = PreviewSettings.ConcurrencyDefault;

    private int sweptParts;
    private int manualBusy;
    private int inFlight;
    private int roundTaken;
    private int roundDone;
    private int roundsDone;
    private double lastRoundSeconds;
    private long nextRoundAtTicks;
    private int roundFaults;
    private string[]? forcedBvids; // 站长点名要补的 bvid（「补今天新货」），取走即清，只作一轮
    private string state = "idle"; // 读写全走 Volatile.Read/Write（字段不加 volatile，否则 CS0420 一串）
    private CancellationToken stoppingToken;

    /// <summary>
    /// 补员员此刻在忙什么，只此五值（站长室的状态灯按这份名单认人）：
    /// disabled 总闸关着 / idle 在岗等下一轮 / running 正在转 / paused 给下载让路 / fault 连轮异常、本轮不干了。
    /// </summary>
    public string State => Volatile.Read(ref state);

    /// <summary>正在转的件数（站长室 inflight 就报这个数，台账里查不出来）。</summary>
    public int InFlight => Volatile.Read(ref inFlight);

    /// <summary>本轮已办几条 / 本轮取到几条（配成对给前端摆进度）。</summary>
    public int RoundDone => Volatile.Read(ref roundDone);

    public int RoundBatch => Volatile.Read(ref roundTaken);

    /// <summary>上一轮跑了多少秒（还没跑过回 null）。</summary>
    public double? LastRoundSeconds => Volatile.Read(ref roundsDone) == 0 ? null : Math.Round(Volatile.Read(ref lastRoundSeconds), 1);

    /// <summary>离下一轮例行巡工还有几秒（跑轮中或总闸关着回 null）。</summary>
    public double? NextRoundSeconds
    {
        get
        {
            var ticks = Interlocked.Read(ref nextRoundAtTicks);
            if (ticks == 0)
            {
                return null;
            }

            var left = (new DateTimeOffset(ticks, TimeSpan.Zero) - DateTimeOffset.UtcNow).TotalSeconds;
            return left > 0 ? Math.Round(left, 0) : 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.stoppingToken = stoppingToken;
        logger.LogInformation("预览补员到岗，30 秒后补首轮，之后每小时一轮（成片落 {Dir}）", paths.Previews);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await RunRoundAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            NoteRoundFault(ex, "预览补员首轮没跑成，等例行轮");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref nextRoundAtTicks, DateTimeOffset.UtcNow.Add(TimeSpan.FromHours(1)).Ticks);
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                NoteRoundFault(ex, "预览补员这轮出错，下一轮再试");
            }
        }
    }

    /// <summary>
    /// 站长催一轮：带 bvids 就是点名补件（「补今天新货」），无视 attempts 上限、也无视已有成片，报名就重做；
    /// 不带就是按常规名单催。进不来闸回 false（防重入），起跑回 true。请求线程不跟着转码。
    /// </summary>
    public bool TryStartOnce(IReadOnlyList<string>? bvids = null)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref manualBusy, 1, 0) != 0)
        {
            return false;
        }

        // 点名名单先摆下：例行轮正持着闸时，等它收尾后接手的那一轮把这批吃掉。
        var picked = bvids?.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToArray();
        Interlocked.Exchange(ref forcedBvids, picked is { Length: > 0 } ? picked : null);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunRoundAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 分站打烊，本轮作废
                }
                catch (Exception ex)
                {
                    NoteRoundFault(ex, "站长催的这轮预览没跑成");
                }
                finally
                {
                    Interlocked.Exchange(ref manualBusy, 0);
                }
            });
        return true;
    }

    /// <summary>整轮抱篗：连错两轮就把牌子翻成 fault（站长室看得到“停摆”），单轮偶发异常只记日志不下结论。</summary>
    private void NoteRoundFault(Exception ex, string message)
    {
        logger.LogWarning(ex, "{Message}", message);
        if (Interlocked.Increment(ref roundFaults) >= 2)
        {
            Volatile.Write(ref state, "fault");
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            Volatile.Write(ref state, "disabled");
            return;
        }

        await roundGate.WaitAsync(cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref nextRoundAtTicks, 0); // 跑轮期间没有「下一轮」可报
        try
        {
            SweepStalePartsOnce();
            Volatile.Write(ref state, "running");
            AlignEncodeGate(settings.Concurrency);

            // 站长点过名就走点名名单（不看 attempts、不管是否已有成片），没点名按常规欠账名单。
            var forced = Interlocked.Exchange(ref forcedBvids, null);
            var queue = forced is { Length: > 0 }
                ? db.ListPreviewCandidatesByBvids(forced)
                : db.ListVideosNeedingPreview(settings.Batch);
            if (forced is { Length: > 0 })
            {
                logger.LogInformation("预览补员点名补件：报名 {Asked} 条，能转的 {Found} 条（没成片的直接跳过）", forced.Length, queue.Count);
            }

            Interlocked.Exchange(ref roundTaken, queue.Count);
            Interlocked.Exchange(ref roundDone, 0);
            var filled = 0;
            var tasks = new List<Task>();
            foreach (var candidate in queue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await YieldIfDownloadingAsync(cancellationToken))
                {
                    logger.LogInformation("预览补员：站内下载一直没腾开，本轮收工（已办 {Done}/{Taken}）", Volatile.Read(ref roundDone), queue.Count);
                    break;
                }

                await encodeGate.WaitAsync(cancellationToken);
                Interlocked.Increment(ref inFlight);
                // 不把 stoppingToken 交给 Task.Run：它在起跑前就被取消的话，委托根本不会跑，
                // finally 里的闸位与在途计数就永不归还（整轮卡在下一件上）。取消靠件内各 await 自查。
                tasks.Add(Task.Run(
                    async () =>
                    {
                        try
                        {
                            if (await ProcessOneAsync(candidate, cancellationToken))
                            {
                                Interlocked.Increment(ref filled);
                            }
                        }
                        finally
                        {
                            Interlocked.Increment(ref roundDone);
                            Interlocked.Decrement(ref inFlight);
                            encodeGate.Release();
                        }
                    }));

                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }

            if (queue.Count > 0)
            {
                logger.LogInformation("预览补员：本轮转成 {Filled}/{Taken} 条", filled, queue.Count);
            }
        }
        finally
        {
            Volatile.Write(ref lastRoundSeconds, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            Interlocked.Increment(ref roundsDone);
            Interlocked.Exchange(ref roundFaults, 0); // 本轮跑完了，异常计数归零（fault 只给连轮抱篗用）
            Volatile.Write(ref state, settings.Enabled ? "idle" : "disabled");
            roundGate.Release();
        }
    }

    /// <summary>有下载压力就让路：每 60 秒回头看一次，连让 20 次还不腾开就放弃本轮（NAS 上 ffmpeg 会拖慢下载）。</summary>
    private async Task<bool> YieldIfDownloadingAsync(CancellationToken cancellationToken)
    {
        if (!settings.YieldOnDownload)
        {
            return true;
        }

        for (var n = 1; n <= YieldLimit; n++)
        {
            if (!HasDownloadPressure())
            {
                Volatile.Write(ref state, "running");
                return true;
            }

            Volatile.Write(ref state, "paused");
            // 暂停板也算压力：站长按下暂停时往往正在整架导旧档，那时更不该插队抢 IO（让路口径照方案）。
            var pressure = pauseBoard.IsPaused
                ? "下载暂停板落着"
                : $"下载面板还有 {db.CountInFlightDownloads()} 件事没办完";
            logger.LogInformation("预览补员让路：{Pressure}，歇 {Seconds} 秒再看（第 {Try} 次）", pressure, YieldPauseSeconds, n);
            await Task.Delay(TimeSpan.FromSeconds(YieldPauseSeconds), cancellationToken);
        }

        return false;
    }

    /// <summary>下载暂停板落着、或有排队/在途的行，都算下载那边有事。</summary>
    private bool HasDownloadPressure() => pauseBoard.IsPaused || db.CountInFlightDownloads() > 0;

    /// <summary>并发度只有站长能改；改完在下一轮开头换闸（此刻绝无在途转码，换闸安全）。</summary>
    private void AlignEncodeGate(int concurrency)
    {
        var want = Math.Clamp(concurrency, PreviewSettings.ConcurrencyMin, PreviewSettings.ConcurrencyMax);
        if (appliedConcurrency == want)
        {
            return;
        }

        encodeGate.Dispose();
        encodeGate = new SemaphoreSlim(want, PreviewSettings.ConcurrencyMax);
        appliedConcurrency = want;
    }

    /// <summary>开机首轮开工前扫一次 .part 尸首（站长先催也算首轮）。没有 tmp 区，也没做全局开机清扫，只此一处。</summary>
    private void SweepStalePartsOnce()
    {
        if (Interlocked.CompareExchange(ref sweptParts, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(paths.Previews))
            {
                return;
            }

            var swept = 0;
            foreach (var part in Directory.EnumerateFiles(paths.Previews, "*.part"))
            {
                TryDelete(part);
                swept++;
            }

            if (swept > 0)
            {
                logger.LogInformation("预览补员开机清扫：抹掉 {Swept} 个上次崩溃留下的 .part", swept);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "预览补员开机清扫没扫干净，下轮再说");
        }
    }

    /// <summary>单件流程：定位成片 → 转码到 .part → ffprobe 验货 → File.Move 转正 → 记 Arrived 行。全程失手只记 Failed 账。</summary>
    private async Task<bool> ProcessOneAsync(PreviewCandidate candidate, CancellationToken cancellationToken)
    {
        if (DerivePreviewRel(candidate.Bvid) is not { } name)
        {
            db.MarkPreviewFailed(candidate.VideoId, "bvid 不能当预览文件名");
            return false;
        }

        var dest = Path.Combine(paths.Previews, name);
        var part = dest + ".part";
        if (ResolveSource(candidate.LocalVideoRel) is not { } source || !File.Exists(source))
        {
            db.MarkPreviewFailed(candidate.VideoId, "成片文件不存在");
            return false;
        }

        Directory.CreateDirectory(paths.Previews);
        TryDelete(part); // 同名尸首先清掉，免得 ffmpeg 撞车
        try
        {
            // 逐件耗时只量 ffmpeg 那一跑（不含 ffprobe 验货、不等同下载让路），与产物时长各是各的。
            var encodeWatch = System.Diagnostics.Stopwatch.StartNew();
            var error = await Ffmpeg.EncodePreviewAsync(source, part, settings.Res, settings.Seconds, cancellationToken);
            encodeWatch.Stop();
            if (error is not null)
            {
                db.MarkPreviewFailed(candidate.VideoId, error);
                return false;
            }

            // 验货：时长读不出正数就是半截件，绝不许转正（转正了前端播到一半黑屏，比没有预览更糟）。
            var duration = await Ffmpeg.ProbeDurationAsync(part, cancellationToken);
            if (duration is not > 0)
            {
                db.MarkPreviewFailed(candidate.VideoId, "验货没过：预览时长读不出来（只转出半截）");
                return false;
            }

            var size = await Ffmpeg.ProbeSizeAsync(part, cancellationToken);
            var bytes = new FileInfo(part).Length;
            if (bytes <= 0)
            {
                db.MarkPreviewFailed(candidate.VideoId, "转出来是个空件");
                return false;
            }

            File.Move(part, dest, overwrite: true);
            db.RegisterPreviewArrived(
                candidate.VideoId, candidate.PageIdx, name, size?.Width, size?.Height, Math.Round(duration.Value, 2), bytes,
                Math.Round(encodeWatch.Elapsed.TotalSeconds, 2));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            db.MarkPreviewFailed(candidate.VideoId, $"落盘不利索：{ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>预览件名：只用 bvid + .mp4。bvid 天然唯一、不含中文与路径分隔符，从根上避开所有 baseName* glob。</summary>
    public static string? DerivePreviewRel(string bvid)
    {
        var name = (bvid ?? string.Empty).Trim();
        if (name.Length is 0 or > 64)
        {
            return null;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return null; // 认不出的名字一律不当文件名使：宁可不做，不许越界
            }
        }

        return name + ".mp4";
    }

    /// <summary>台账里的成片 rel 换成绝对路径；想越狱出视频库的一律当没这回事。</summary>
    private string? ResolveSource(string localVideoRel)
    {
        if (string.IsNullOrWhiteSpace(localVideoRel))
        {
            return null;
        }

        var root = Path.GetFullPath(paths.Videos).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(paths.Videos, localVideoRel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            /* 临时件删不掉就随它去：没有行，谁也当它不存在 */
        }
    }
}
