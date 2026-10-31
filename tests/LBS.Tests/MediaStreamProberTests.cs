using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 分轨码率的唯一来源是写入侧：MediaStreamProber 在「主片到站归档后」与「加档扫盘定档后」各探一次
/// （起进程的活集中在这一个类，StationDb 与热路径 GetWatchData 都不起进程、也不补探）。
/// 要验「真探得到值」得有真件，没装 ffmpeg/ffprobe 的机器上那几例直接放过；
/// 防抖、点名口径与实测编码那几例不依赖外部工具，什么时候都跑。
/// </summary>
public sealed class MediaStreamProberTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly MediaStreamProber _prober;

    public MediaStreamProberTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-prober-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
        _prober = new MediaStreamProber(_db, _paths, NullLogger<MediaStreamProber>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉无所谓
        }
    }

    [Fact]
    public void 到站与定档那一刻探完_两路码率与实测编码落库并盖章()
    {
        if (!MediaSample.HasTools())
        {
            return; // 没真件可探，这些「值真进库」的断言无从谈起
        }

        var bvid = "BV1probe0001";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        var mainBase = Path.Combine(dir, $"{bvid}.P1");
        Assert.True(MediaSample.MakeVideo(mainBase + ".mp4"), "ffmpeg 没造出正片");
        // 样本件是 h264：故意点单 hevc，顺带验「挂羊头卖狗肉」那条 Warning 真会说人话
        File.Copy(mainBase + ".mp4", Path.Combine(dir, $"{bvid}.P1-HEVC.mp4"));

        var 日志 = new 收日志();
        var prober = new MediaStreamProber(_db, _paths, 日志);

        var id = 到站带页(bvid, mainBase);
        _db.FinalizeArtifacts(id, _tempDir); // local_video_path 从 null 变有值的那一刻
        prober.ProbeArrivedVideo(id);

        var page = Assert.Single(_db.GetWatchData(bvid)!.Pages);
        Assert.Equal("avc", page.Codec);
        Assert.NotNull(page.VideoKbps);
        Assert.NotNull(page.AudioKbps);
        // 形态同趟入库（清晰度菜单/Hi-Res 徽记判据）：样本件就是 72 高 @10 帧 + aac。
        Assert.Equal(72, page.Height);
        Assert.NotNull(page.Fps);
        Assert.InRange(page.Fps!.Value, 9.5, 10.5);
        Assert.Equal("aac", page.AudioCodec);
        Assert.Equal(1, 分页防抖戳(id, 1));
        Assert.Equal(1, 直读整数("SELECT shape_probed FROM pages WHERE video_id = $id AND idx = $idx;", id, 1, null));

        // 加档行同理：定档那一刻探，之后开多少遍观看页都不再扰盘
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var row = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        Assert.True(_db.FinalizeCodecVersion(row.Id, Path.Combine(dir, $"{bvid}.P1-HEVC"), _tempDir));
        prober.ProbeCodecFile(row.Id, id, 1, "hevc");

        var version = Assert.Single(Assert.Single(_db.GetWatchData(bvid)!.Pages).CodecVersions);
        Assert.Equal("Arrived", version.Status);
        Assert.NotNull(version.VideoKbps);
        Assert.NotNull(version.AudioKbps);
        Assert.Equal(72, version.Height);
        Assert.Equal("aac", version.AudioCodec);
        Assert.Equal(1, 加档行防抖戳(id, 1, "hevc"));

        // 实测编码进 measured_codec，点单值 codec 不许被改口（GetArrivedCodecRel 靠它找文件）
        Assert.Equal("avc", 加档行实测编码(id, 1, "hevc"));
        Assert.Equal("hevc", _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").Codec);
        Assert.Contains(
            日志.Lines,
            line => line.Level == LogLevel.Warning
                && line.Message.Contains("hevc", StringComparison.Ordinal)
                && line.Message.Contains("avc", StringComparison.Ordinal));
    }

    [Fact]
    public void 从没开过的片子_归档落定那一刻码率就进库()
    {
        if (!MediaSample.HasTools())
        {
            return; // 同上：要的是「值真进库」
        }

        var bvid = "BV1probe0004";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        var mainBase = Path.Combine(dir, $"{bvid}.P1");
        Assert.True(MediaSample.MakeVideo(mainBase + ".mp4"), "ffmpeg 没造出正片");

        var id = 到站带页(bvid, mainBase);
        // 全程只走归档链路（FinalizeArtifacts 写 local_video_path → 同步员那一刻探测），
        // 一次都没调 GetWatchData：开片才走的路径不该是码率的来源。
        _db.FinalizeArtifacts(id, _tempDir);
        _prober.ProbeArrivedVideo(id);

        Assert.NotNull(直读整数("SELECT video_kbps FROM pages WHERE video_id = $id AND idx = $idx;", id, 1, null));
        Assert.NotNull(直读整数("SELECT audio_kbps FROM pages WHERE video_id = $id AND idx = $idx;", id, 1, null));
        Assert.Equal(1, 分页防抖戳(id, 1));
    }

    [Fact]
    public void 探不到也盖章_盖过戳的不再被点名()
    {
        var bvid = "BV1probe0002";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        var mainBase = Path.Combine(dir, $"{bvid}.P1");
        File.WriteAllText(mainBase + ".mp4", "假文件（ffprobe 认不出，机器上没装 ffprobe 也一样探不到）");

        var id = 到站带页(bvid, mainBase);
        Assert.Empty(_db.GetPagesAwaitingStreamProbe(id)); // 正片还没归档：没文件可探，不点名

        _db.FinalizeArtifacts(id, _tempDir);
        Assert.Single(_db.GetPagesAwaitingStreamProbe(id)); // 刚落盘又没探过 → 该探

        _prober.ProbeArrivedVideo(id);
        var page = Assert.Single(_db.GetWatchData(bvid)!.Pages);
        Assert.Null(page.VideoKbps); // 探不到就 null，不给 0 冒充零码率
        Assert.Null(page.AudioKbps);
        Assert.Equal(1, 分页防抖戳(id, 1)); // 探失败也算「探过一次」：不致于每轮到它都重探

        Assert.Empty(_db.GetPagesAwaitingStreamProbe(id)); // 盖过戳的不再被点名 = 一个文件只扰一趟
        _prober.ProbeArrivedVideo(id); // 再叫一次也不该做事（不抛、不改账）
        Assert.Equal(1, 分页防抖戳(id, 1));
    }

    [Fact]
    public void 加档行探不到_实测编码与码率都留null但照样盖章()
    {
        var bvid = "BV1probe0005";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{bvid}.P1-AVC.mp4"), "假文件（探不出任何东西）");

        var id = 到站带页(bvid, Path.Combine(dir, $"{bvid}.P1"));
        Assert.True(_db.AddCodecVersion(id, 1, "avc"));
        var row = _db.GetCodecVersions(id).Single(r => r.Codec == "avc");
        Assert.True(_db.FinalizeCodecVersion(row.Id, Path.Combine(dir, $"{bvid}.P1-AVC"), _tempDir));

        _prober.ProbeCodecFile(row.Id, id, 1, "avc");

        Assert.Equal(1, 加档行防抖戳(id, 1, "avc")); // 探不到也盖章，不致于每次巡视都重探
        Assert.Null(加档行实测编码(id, 1, "avc")); // 实测编码留 null，不猜
        Assert.Null(直读整数("SELECT video_kbps FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;", id, 1, "avc"));
        Assert.Equal("avc", _db.GetCodecVersions(id).Single(r => r.Codec == "avc").Codec); // 点单值不动
    }

    [Fact]
    public void 队列快照带得出实测编码()
    {
        var bvid = "BV1probe0006";
        var id = _db.AddVideo("https://www.bilibili.com/video/" + bvid, bvid).VideoId;

        // 在途加档行：还没探过就是 null（探测发生在定档那一刻）
        Assert.True(_db.AddCodecVersion(id, 1, "av1"));
        var row = _db.GetCodecVersions(id).Single(r => r.Codec == "av1");
        var 排队中 = Assert.Single(_db.GetCodecQueueRows(), r => r.VideoId == id);
        Assert.Null(排队中.Measured);
        Assert.Equal("av1", 排队中.Codec);

        // 写入侧那一步落了账，快照就原样转达（点单值照旧不动）
        _db.SetCodecStreams(row.Id, 900, 96, probed: true, measuredCodec: "hevc");
        var 带实测 = Assert.Single(_db.GetCodecQueueRows(), r => r.VideoId == id);
        Assert.Equal("hevc", 带实测.Measured);
        Assert.Equal("av1", 带实测.Codec);
    }

    [Fact]
    public void 只点已落盘又没盖章的分P()
    {
        var bvid = "BV1probe0003";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        var p1 = Path.Combine(dir, $"{bvid}.P1");
        var p2 = Path.Combine(dir, $"{bvid}.P2");
        File.WriteAllText(p1 + ".mp4", "假文件");
        // P2 的正片压根没到（缺件那条链补回来、归档那一刻才轮到它）

        var id = 到站带页(bvid, p1, p2);
        _db.FinalizeArtifacts(id, _tempDir);

        Assert.Equal(new[] { 1 }, _db.GetPagesAwaitingStreamProbe(id).Select(p => p.PageIdx));

        _db.SetPageStreams(id, 1, null, 500, 64); // 写入侧落库（顺带盖章）
        Assert.Empty(_db.GetPagesAwaitingStreamProbe(id));
    }

    [Fact]
    public void 老帐重探一轮_形态入库后不再扰盘()
    {
        var bvid = "BV1probe0007";
        var dir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(dir);
        var mainBase = Path.Combine(dir, $"{bvid}.P1");
        File.WriteAllText(mainBase + ".mp4", "假文件（这例只验防抖戳的换位重探，探不探得到无碍）");

        var id = 到站带页(bvid, mainBase);
        _db.FinalizeArtifacts(id, _tempDir);
        _prober.ProbeArrivedVideo(id);
        Assert.Equal(1, 直读整数("SELECT shape_probed FROM pages WHERE video_id = $id AND idx = $idx;", id, 1, null));

        // 模拟升级前的老账：stream_probed 盖过但形态列全空——事件点与补员巡视都该再给它探一趟，
        // 不然永远没人为它补 width/height/fps/audio_codec（清晰度菜单的实测链就接不上）。
        直写("UPDATE pages SET shape_probed = NULL, width = NULL, height = NULL, fps = NULL, audio_codec = NULL WHERE video_id = $id AND idx = $idx;", id, 1);
        Assert.Single(_db.GetPagesAwaitingStreamProbe(id));
        Assert.Contains(_db.GetPagesAwaitingStreamProbeBackfill(50), r => r.VideoId == id);

        _prober.ProbeArrivedVideo(id);
        Assert.Equal(1, 直读整数("SELECT shape_probed FROM pages WHERE video_id = $id AND idx = $idx;", id, 1, null));
        Assert.Empty(_db.GetPagesAwaitingStreamProbe(id)); // 重探也只这一趟，不致于每小时回锅
    }

    [Fact]
    public void 引擎实发规格_盖章只补不擦()
    {
        var bvid = "BV1probe0008";
        var id = 到站带页(bvid, Path.Combine(_tempDir, "v", $"{bvid}.P1"));

        // 同步员从在途任务拄下来的实发规格：清晰度菜单直接照报，Hi-Res 徽记拿 id 当硬判据。
        _db.SetPageQuality(id, 116, "1080P 60帧", 31251);
        var page = Assert.Single(_db.GetWatchData(bvid)!.Pages);
        Assert.Equal("1080P 60帧", page.QualityName);
        Assert.Equal(31251, page.AudioQualityId);

        // COALESCE 只补不擦：空手再来一趟不许把已入账的真值擦掉。
        _db.SetPageQuality(id, null, null, null);
        page = Assert.Single(_db.GetWatchData(bvid)!.Pages);
        Assert.Equal("1080P 60帧", page.QualityName);
        Assert.Equal(31251, page.AudioQualityId);

        // 加档行同一口径（定档那一刻由同步员盖章）。
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var row = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        _db.SetCodecQuality(row.Id, 116, "1080P 60帧", 30251);
        var version = Assert.Single(Assert.Single(_db.GetWatchData(bvid)!.Pages).CodecVersions);
        Assert.Equal("1080P 60帧", version.QualityName);
        Assert.Equal(30251, version.AudioQualityId);
    }

    /// <summary>造一条已到站馆藏，按传入的基准路径摆 N 个分 P（文件摆不摆、摆几个由调用方决定）。</summary>
    private long 到站带页(string bvid, params string[] outputBasePaths)
    {
        var id = _db.AddVideo("https://www.bilibili.com/video/" + bvid, bvid).VideoId;
        var taskIds = Enumerable.Range(1, outputBasePaths.Length).Select(i => $"task-{bvid}-{i}").ToList();
        _db.MarkSubmitted(id, taskIds, bvid, null);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(null, null, null, null, null, null),
            outputBasePaths
                .Select((basePath, i) => new PageDraft($"task-{bvid}-{i + 1}", i + 1, $"{bvid}-P{i + 1}", i + 1, "04:13", basePath))
                .ToList());
        _db.MarkArrived(id);
        return id;
    }

    private int? 分页防抖戳(long videoId, int pageIdx)
    {
        return 直读整数("SELECT stream_probed FROM pages WHERE video_id = $id AND idx = $idx;", videoId, pageIdx, null);
    }

    /// <summary>直探台账改一笔：只给「模拟升级前老账」这类铺垫用，不绕业务写入口。</summary>
    private void 直写(string sql, long videoId, int pageIdx)
    {
        using var connection = 台账();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$idx", pageIdx);
        command.ExecuteNonQuery();
    }

    private int? 加档行防抖戳(long videoId, int pageIdx, string codec)
    {
        return 直读整数("SELECT stream_probed FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;", videoId, pageIdx, codec);
    }

    private string? 加档行实测编码(long videoId, int pageIdx, string codec)
    {
        using var connection = 台账();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT measured_codec FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        command.Parameters.AddWithValue("$codec", codec);
        return command.ExecuteScalar() as string;
    }

    /// <summary>绕开 Db 类直探台账取一个整数列：验「写入侧到底有没有把值写进库」，不靠 GetWatchData 转达。</summary>
    private int? 直读整数(string sql, long videoId, int pageIdx, string? codec)
    {
        using var connection = 台账();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", videoId);
        if (codec is null)
        {
            command.Parameters.AddWithValue("$idx", pageIdx);
            command.Parameters.AddWithValue("$page", pageIdx);
        }
        else
        {
            command.Parameters.AddWithValue("$page", pageIdx);
            command.Parameters.AddWithValue("$codec", codec);
        }

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private SqliteConnection 台账()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        return connection;
    }

    /// <summary>收日志用：要验「点单 X、盘上 Y」那条 Warning 真会说人话（真件是 h264 而点单 hevc）。</summary>
    private sealed class 收日志() : ILogger<MediaStreamProber>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }
}
