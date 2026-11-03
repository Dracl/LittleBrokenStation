using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 封面缩略切片器（<see cref="CoverThumbs"/>）：档位齐不齐、越界不越界、换封面清不清旧件、名单点得准不准。
///
/// 钉的是四件最容易漂的事：①「不放大」这条硬规则（把 200 宽硬拉成 1280 是假清晰，也是磁盘黑洞）；
/// ②版本戳一换旧件必须扫干净（否则 <c>data/thumbs</c> 只涨不消，旧 URL 还能被命中）；
/// ③原图缺失/读不出尺寸只回原因不抛（同步链顺手调它，为一枚缩略掀翻一条稿的入库不划算）；
/// ④候选名单的口径（有封面行、零档 arrived、没失败到封顶）。
///
/// ffmpeg/ffprobe 是容器里的外部工具，开发机与 CI 未必有——没装时整类放过，不把缺工具算成代码的错
/// （与 <see cref="MediaSample"/> 同一套待客之道）。
/// </summary>
public sealed class CoverThumbTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public CoverThumbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-coverthumb-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    /// <summary>造一条到站馆藏并落一枚指定尺寸的封面原图，返回 videoId。withCover=false 时只留台账没有文件。</summary>
    private long SeedWithCover(string bvid, int width = 1600, int height = 1000, bool withCover = true)
    {
        var videoId = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        var dir = Path.Combine(_videosDir, bvid);
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, $"1-{bvid}-1080P-AVC");
        File.WriteAllText(basePath + ".mp4", "video-bytes");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP主小明", 123, "音乐", "2023-05-20 18:00:00", "简介文本", "1234万"),
            [new PageDraft("task-" + bvid, 1, "正片", 456, "04:13", basePath)]);
        _db.FinalizeArtifacts(videoId, _videosDir);
        _db.MarkArrived(videoId);

        if (!withCover)
        {
            return videoId;
        }

        var rel = $"{bvid}/{bvid}-1-Cover.jpg";
        MakeImage(Path.Combine(_videosDir, rel.Replace('/', Path.DirectorySeparatorChar)), width, height);
        _db.RegisterCoverArtifact(videoId, rel); // 与 CoverFileFetcher 同一张嘴：先文件后行
        return videoId;
    }

    /// <summary>用 lavfi 现造一张纯色图当封面（尺寸可控，验「不放大」与「居中裁」都得有真尺寸可问）。</summary>
    private static void MakeImage(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", $"color=c=0x3a7bd5:s={width}x{height}",
                     "-frames:v", "1", path,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
    }

    private async Task<CoverThumbs.BuildOutcome> Build(long videoId)
        => await CoverThumbs.TryBuildAsync(_db, _paths, videoId, CancellationToken.None);

    private string[] ThumbFiles(string bvid)
        => Directory.Exists(_paths.Thumbs)
            ? Directory.GetFiles(_paths.Thumbs, $"{bvid}*").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];

    [Fact]
    public async Task 宽图五档齐切_台账五行落盘_临时件不留尸首()
    {
        if (!MediaSample.HasTools())
        {
            return; // 缺 ffmpeg/ffprobe，这例没东西可验
        }

        var videoId = SeedWithCover("BVWide000001", 1600, 1000);

        var outcome = await Build(videoId);

        Assert.True(outcome.Built);
        Assert.Equal(CoverThumbTiers.All.Length, outcome.Arrived);
        Assert.Null(outcome.Error);

        var manifest = _db.GetCoverThumbManifest(videoId);
        Assert.NotNull(manifest);
        Assert.Equal(CoverThumbTiers.All.Length, manifest!.Split(',').Length);
        foreach (var name in manifest.Split(','))
        {
            Assert.True(File.Exists(Path.Combine(_paths.Thumbs, name)), $"{name} 台账说落了、盘上却没有");
            // 文件名三要素齐全：bvid 独立成段（私有反查靠它）、档位、版本戳（长缓存 immutable 靠它）
            Assert.StartsWith("BVWide000001_", name);
            Assert.Contains(CoverThumbTiers.All, t => name.Contains($"_{t.Token}-", StringComparison.Ordinal));
        }

        // 临时件都在 .staging 里生、转正即搬走；留下尸首就是 File.Move 没走干净
        Assert.True(!Directory.Exists(_paths.ThumbsStaging) || Directory.GetFiles(_paths.ThumbsStaging).Length == 0);
    }

    [Fact]
    public async Task 不放大_原图比档位窄就只切得下的小档()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        // 200 宽的窄封面：只有 160x100 一档吃得下，352 那档起就该收手（放大出来的清晰是假的）
        var videoId = SeedWithCover("BVNarrow00001", 200, 160);

        var outcome = await Build(videoId);

        Assert.True(outcome.Built);
        Assert.Equal(1, outcome.Arrived);
        var manifest = _db.GetCoverThumbManifest(videoId);
        Assert.NotNull(manifest);
        Assert.Contains("160x100-", manifest!);
        Assert.DoesNotContain("352x220-", manifest);
        Assert.DoesNotContain("1280x800-", manifest);

        // 方图也一样按宽卡：1200 见方比 1280 窄，最高档就只到 768（纵的方向本来就靠裁，不构成放大的理由）
        var square = SeedWithCover("BVMidSquare1", 1200, 1200);
        Assert.True((await Build(square)).Built);
        var squareManifest = _db.GetCoverThumbManifest(square);
        Assert.Contains("768x480-", squareManifest);
        Assert.DoesNotContain("1280x800-", squareManifest);
    }

    [Fact]
    public async Task 方图居中裁成整档_产出尺寸与档位一字不差()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        // 1:1 的原图裁成 16:10 的档：既要填满整框（不许留黑边），又不许拉伸变形（只能裁）。
        // 取 1600 见方：比最高档还宽，五档才算全部入围（1200 见方切不到 1280 档，那是「不放大」规矩在管事）。
        var videoId = SeedWithCover("BVSquare0001", 1600, 1600);

        var outcome = await Build(videoId);

        Assert.True(outcome.Built);
        foreach (var tier in CoverThumbTiers.All)
        {
            var name = ThumbFiles("BVSquare0001").Single(f => Path.GetFileName(f).Contains($"_{tier.Token}-", StringComparison.Ordinal));
            var dims = await Ffmpeg.ProbeSizeAsync(name, CancellationToken.None);
            Assert.Equal((tier.Width, tier.Height), dims);
        }
    }

    [Fact]
    public async Task 原图不在盘上_只回原因不掀桌()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVNoFile0001", withCover: false);
        _db.RegisterCoverArtifact(videoId, "BVNoFile0001/BVNoFile0001-1-Cover.jpg"); // 台账有行、盘上没件

        var outcome = await Build(videoId);

        Assert.False(outcome.Built);
        Assert.Equal(0, outcome.Arrived);
        Assert.Contains("封面原图不在盘上", outcome.Error);
        Assert.Empty(ThumbFiles("BVNoFile0001"));

        // 压根没封面行的也不该抛
        var bare = SeedWithCover("BVNoCover001", withCover: false);
        var bareOutcome = await Build(bare);
        Assert.False(bareOutcome.Built);
        Assert.Contains("封面", bareOutcome.Error);
    }

    [Fact]
    public async Task 重拉封面换了版本戳_旧档扫干净不留旧URL()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVRerun0001", 1600, 1000);
        Assert.True((await Build(videoId)).Built);
        var firstNames = ThumbFiles("BVRerun0001").Select(Path.GetFileName).ToArray();
        Assert.NotEmpty(firstNames);

        // 模拟「重新拉封面」：同名原地换主（mtime 变了 → 版本戳变了）
        var source = Path.Combine(_videosDir, "BVRerun0001", "BVRerun0001-1-Cover.jpg");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(7));

        var again = await Build(videoId);

        Assert.True(again.Built);
        var secondNames = ThumbFiles("BVRerun0001").Select(Path.GetFileName).ToArray();
        Assert.Equal(CoverThumbTiers.All.Length, secondNames.Length); // 旧的没清就会翻倍
        Assert.DoesNotContain(firstNames[0]!, secondNames);

        // 同名再切一次不重复产出（补员员每小时来一遍也不该重切）
        var third = await Build(videoId);
        Assert.True(third.Built);
        Assert.Equal(CoverThumbTiers.All.Length, ThumbFiles("BVRerun0001").Length);
    }

    [Fact]
    public async Task 候选名单_只点有封面却没缩略的到站稿()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var pending = SeedWithCover("BVNeedMe0001", 800, 500); // 有封面、零档 → 该点它
        var settled = SeedWithCover("BVDone00001", 800, 500); // 已切齐 → 不该再点
        var noCover = SeedWithCover("BVNoThumb001", withCover: false); // 没封面行 → 没料可切
        Assert.True((await Build(settled)).Built);

        var list = _db.ListVideosNeedingCoverThumbs();

        Assert.Contains(list, c => c.VideoId == pending);
        Assert.DoesNotContain(list, c => c.VideoId == settled);
        Assert.DoesNotContain(list, c => c.VideoId == noCover);

        // 切完就该从名单上消失（补员员靠这个收敛，否则连轴转个没完）
        Assert.True((await Build(pending)).Built);
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == pending);
    }

    [Fact]
    public async Task 切砸了记一笔_同档不放大到封顶就不再点名()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVFault00001", 800, 500);
        _db.MarkCoverThumbFailed(videoId, "352x220", "ffmpeg 没答应");

        // 单档失败不影响其余档位落账，也不把这条稿从名单上抹掉（还有四档等着）
        var outcome = await Build(videoId);
        Assert.True(outcome.Built);
        Assert.Contains("352x220-", _db.GetCoverThumbManifest(videoId));

        // 五档全失败到封顶 → 整体搁笔，别让一张坏封面每小时拖全站重切一遍
        var other = SeedWithCover("BVGiveUp001", 800, 500);
        foreach (var tier in CoverThumbTiers.All)
        {
            for (var i = 0; i < 5; i++)
            {
                _db.MarkCoverThumbFailed(other, tier.Token, "试了五次都不行");
            }
        }

        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == other);
    }

    [Fact]
    public async Task 私有稿的缩略件认得出归属_静态区那道闸不能透明放行()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        // 缩略件不在 artifacts 里，归属全靠 cover_thumbs 那条腿认。这条腿断了就是 100% fail-open：
        // 台账认不出 → Program.cs 的私有闸「台账不管这个件」放行 → 别人藏起来的封面谁都能拉。
        var videoId = SeedWithCover("BVPriv000001", 1600, 1000);
        Assert.True((await Build(videoId)).Built);
        _db.SetVideoVisibility(videoId, VideoVisibility.Private);
        var name = _db.GetCoverThumbManifest(videoId)!.Split(',')[0];

        Assert.Equal(videoId, _db.GetVideoIdByMediaRel(name));
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel(name));

        // 第二保险：台账行被清了（删档、手删 data/thumbs、补员前）还能靠文件名里那枚 BV 号认出归属
        _db.DeleteCoverThumbs(videoId);
        Assert.Equal(videoId, _db.GetVideoIdByMediaRel(name));

        // 与缩略同形的野件（头像那类）不能被盗用成「私有」：认不出归属就是 null，由调用方放行
        Assert.Null(_db.GetVideoIdByMediaRel("someone-avatar_352x220-8df38637bb8cbcf-11c4a.webp"));
        Assert.Null(_db.GetVideoVisibilityByMediaRel("face_160x100-8df38637bb8cbcf-11c4a.webp"));
    }

    [Fact]
    public async Task 扁图不许凑档放大_高不够也整排收手() // 铁律：宁愿大一点缩小，不要小一点放大
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        // 16:9 的源比 16:10 的档更扁：800×450 切 768×480 那一档时，ffmpeg 的 increase 会把高从 450 抬到 480
        // ——1.07 倍放大，眼睛难辨而铁律已破。只看宽的判据拦不住它，所以宽高一起判。
        var videoId = SeedWithCover("BVFlatNoUp11", 800, 450);

        var outcome = await Build(videoId);

        Assert.True(outcome.Built);
        var manifest = _db.GetCoverThumbManifest(videoId)!;
        Assert.Contains("512x320-", manifest); // 宽 800≥512 且高 450≥320 → 能纯裁出来，切
        Assert.DoesNotContain("768x480-", manifest); // 高 450 < 480 → 只能放大，不许
        Assert.DoesNotContain("1280x800-", manifest);
    }

    [Fact]
    public async Task 收手档位的旧账必须销掉_扁图不会永远挂在待切名单上() // 11-03 实测：一枚 16:9 扁封面被补员员白切 880 次
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        // 1280×720 的扁封面：1280x800 档被铁律收手（高不够）。a1 时代它曾是 arrived（戳尾没带算法版本），
        // 文件被 PruneStaleFiles 扫走了而台账行还赖着：欠账判定永远点名，补员员 200 毫秒一轮连轴转。
        var videoId = SeedWithCover("BVStaleRow1", 1280, 720);
        Assert.True((await Build(videoId)).Built);
        Assert.Equal(4, ThumbFiles("BVStaleRow1").Length); // 四档齐，第五档本来就不该有

        // 手工植回一枚旧时代的 arrived 行（盘上没件、戳没带算法版本），复现 a1 存量
        _db.RegisterCoverThumbArrived(videoId, "BVStaleRow1_1280x800-deadbeef-1.webp", "1280x800", "deadbeef-1", "webp");
        Assert.Contains(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 旧戳 → 该点名
        Assert.Contains("1280x800-", _db.GetCoverThumbManifest(videoId)!); // 盘上没有的地址正被递给前端

        var again = await Build(videoId); // 再切一轮：收手档位的旧账应被一并销掉

        Assert.True(again.Built);
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 补员员该收敛了
        Assert.DoesNotContain("1280x800-", _db.GetCoverThumbManifest(videoId)!); // 幽灵地址不再递前端

        // 日后原图换成大的：旧行已销，INSERT 直接落新账，不用人工清盘
        var source = Path.Combine(_videosDir, "BVStaleRow1", "BVStaleRow1-1-Cover.jpg");
        MakeImage(source, 1600, 1000);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(7)); // 换内容也换版本戳
        var upgraded = await Build(videoId);
        Assert.True(upgraded.Built);
        Assert.Equal(5, upgraded.Arrived);
        Assert.Contains("1280x800-", _db.GetCoverThumbManifest(videoId)!);
    }

    [Fact]
    public async Task 台账说落了盘上却没有_对账销行后补员自己切回来() // 欠账判定探不到磁盘，幽灵行得靠每轮对账收口
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVGhostRow1", 1600, 1000);
        Assert.True((await Build(videoId)).Built);
        var files = ThumbFiles("BVGhostRow1");
        Assert.Equal(5, files.Length);

        // 站长手删一枚档位件（或磁盘事故）：台账还写着 arrived，旧口径既看不见欠账、manifest 还递幽灵地址
        var gone = files.Single(f => Path.GetFileName(f).Contains("_512x320-", StringComparison.Ordinal));
        File.Delete(gone);
        Assert.Contains("512x320-", _db.GetCoverThumbManifest(videoId)!); // 销账前：幽灵地址还在递
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 台账自证清白，名单点不到它

        var swept = _db.ReconcileCoverThumbRowsWithDisk();

        Assert.Equal(1, swept);
        Assert.DoesNotContain("512x320-", _db.GetCoverThumbManifest(videoId)!); // 幽灵地址收回去
        Assert.Contains(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 打成 failed 才进得来名单

        Assert.True((await Build(videoId)).Built); // 下一轮真重切
        Assert.Equal(5, ThumbFiles("BVGhostRow1").Length);
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId);
        Assert.Equal(0, _db.ReconcileCoverThumbRowsWithDisk()); // 齐件时再对账：一行不误伤
    }

    [Fact]
    public async Task 单档切砸过其余档都在_点名会捞回来重试到封顶() // 旧口径只看「零档/旧戳」，这种局会无声缺到永远
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVReroll01", 1600, 1000);
        Assert.True((await Build(videoId)).Built);

        // 模拟当年某档 ffmpeg 瞬时抽风的现场：文件没了、行是 failed（attempts=2，没到封顶）
        var victim = ThumbFiles("BVReroll01").Single(f => Path.GetFileName(f).Contains("_512x320-", StringComparison.Ordinal));
        File.Delete(victim);
        using (var conn = new SqliteConnection($"Data Source={_paths.DatabaseFile}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE cover_thumbs SET status = 'failed', rel_path = '', attempts = 2 WHERE video_id = $id AND tier = '512x320';";
            cmd.Parameters.AddWithValue("$id", videoId);
            cmd.ExecuteNonQuery();
        }

        // 四档齐 + 一枚 failed：旧口径不点名，新口径必须捞回来
        Assert.Contains(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId);

        var again = await Build(videoId); // 真重切那一档

        Assert.True(again.Built);
        Assert.Equal(5, ThumbFiles("BVReroll01").Length);
        Assert.Contains("512x320-", _db.GetCoverThumbManifest(videoId)!);
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 补齐后从名单退场

        // 封顶闸门照旧：真切不动的稿照样整体搁笔，不会每小时拖一遍（上方既有测钉住另一半）。
        // 注意 MarkCoverThumbFailed 拒改 arrived 行，所以这里直接改台账模拟「这一档彻底切不动」的现场。
        using (var conn = new SqliteConnection($"Data Source={_paths.DatabaseFile}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE cover_thumbs SET status = 'failed', rel_path = '', attempts = 5 WHERE video_id = $id AND tier = '768x480';";
            cmd.Parameters.AddWithValue("$id", videoId);
            cmd.ExecuteNonQuery();
        }

        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId);
    }

    [Fact]
    public async Task 算法版本进版本戳_旧版档重新点名追平() // 改了切片判据，存量得自己追上来，不靠人工清盘
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVAlgoVer01", 1600, 1000);
        Assert.True((await Build(videoId)).Built);
        var name = _db.GetCoverThumbManifest(videoId)!.Split(',')[0];
        Assert.EndsWith($"-{CoverThumbTiers.AlgoVersion}.webp", name); // 戳尾段就是算法版本
        Assert.DoesNotContain(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId); // 已是最新 → 不点名

        // 把一行退回旧算法版本（模拟上一代判据切出来的存量）：它必须重新出现在待切名单里。
        // 少了这条口径，同名件会在 TryBuildAsync 里靠 File.Exists 直接跳过，旧档永远赖在盘上。
        using (var conn = new SqliteConnection($"Data Source={_paths.DatabaseFile}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE cover_thumbs SET src_stamp = 'deadbeef-1-a0' WHERE video_id = $id;";
            cmd.Parameters.AddWithValue("$id", videoId);
            cmd.ExecuteNonQuery();
        }

        Assert.Contains(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId);
    }

    [Fact]
    public async Task 四条列表读法都带得出档位清单() // 首页/搜索/相关推荐/UP 空间共用 ReadEntries 那一套列下标，漏一处那一页就没缩略
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var first = SeedWithCover("BVReadOne01", 900, 600);
        var second = SeedWithCover("BVReadTwo01", 900, 600);
        var bare = SeedWithCover("BVBareList01", 900, 600, withCover: false); // 有稿没封面 → 两样都是 null
        Assert.True((await Build(first)).Built);
        Assert.True((await Build(second)).Built);
        var manifest = _db.GetCoverThumbManifest(first);
        Assert.NotNull(manifest);

        Assert.Contains(_db.ListVideos(), e => e.Id == first && e.CoverThumbs == manifest);
        Assert.Contains(_db.SearchVideos("BVReadOne01", null), e => e.Id == first && e.CoverThumbs == manifest);
        Assert.Contains(_db.ListVideosByUpMid(123), e => e.Id == first && e.CoverThumbs == manifest);
        Assert.Contains(_db.GetRelatedVideos(first, take: 8), e => e.Id == second && e.CoverThumbs != null);

        // 没切过的就是 null（空串会让前端以为「有档但名字为空」而黑屏）
        var bareEntry = _db.ListVideos().Single(e => e.Id == bare);
        Assert.Null(bareEntry.CoverThumbs);
        Assert.Null(bareEntry.LocalCover);
    }

    [Fact]
    public async Task 进回收站连缩略一并销账_恢复后补员自己切回来() // 钉住 DropCoverThumbsForRecycle 那一刀（与预览同一套规矩）
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVRecycleTh1", 900, 600);
        Assert.True((await Build(videoId)).Built);
        Assert.NotEmpty(ThumbFiles("BVRecycleTh1"));

        var outcome = _db.MoveVideoToRecycle(videoId, _videosDir, Path.Combine(_tempDir, "recycle"), "站长", 0);

        Assert.True(outcome.Ok);
        Assert.Empty(ThumbFiles("BVRecycleTh1")); // 地址不再被拼出来，盘上也不留孤儿件
        Assert.Null(_db.GetCoverThumbManifest(videoId));
        // 档收回来了也不用手动重切：没 arrived 行就自动回到补员名单上
        var restored = _db.RestoreFromRecycle(outcome.BinId, _videosDir, Path.Combine(_tempDir, "recycle"));
        Assert.True(restored.Ok);
        Assert.Contains(_db.ListVideosNeedingCoverThumbs(), c => c.VideoId == videoId);
    }

    [Fact]
    public async Task 删稿清档_盘上件与台账行一起清干净()
    {
        if (!MediaSample.HasTools())
        {
            return;
        }

        var videoId = SeedWithCover("BVDelThumb1", 800, 500);
        Assert.True((await Build(videoId)).Built);
        Assert.NotEmpty(ThumbFiles("BVDelThumb1"));

        CoverThumbs.DeleteFiles(_paths, "BVDelThumb1");
        _db.DeleteCoverThumbs(videoId);

        Assert.Empty(ThumbFiles("BVDelThumb1"));
        Assert.Null(_db.GetCoverThumbManifest(videoId));
        // 删不存在的稿也不该抛（回收站与级联删除都顺手调它）
        Assert.Equal(0, CoverThumbs.DeleteFiles(_paths, "BVNeverExisted"));
        Assert.Equal(0, CoverThumbs.DeleteFiles(_paths, null));
        Assert.Equal(0, CoverThumbs.DeleteFiles(_paths, "../videos")); // 脏 bvid 休想借通配符扫到别的目录
    }
}
