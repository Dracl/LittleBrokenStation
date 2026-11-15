using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>StationDb 用临时库文件测，测完即删。</summary>
public sealed class StationDbTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public StationDbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
    }

    [Fact]
    public void 初始化幂等()
    {
        _db.Initialize();
        _db.Initialize();
        Assert.Empty(_db.ListVideos());
    }

    [Fact]
    public void WAL模式已启用()
    {
        // 侧文件（-wal/-shm）只是代理证据：最后一个连接干净关闭时会被 checkpoint 删掉，
        // 早年靠连接池养着才一直灵；数据交换引入 ClearAllPools 后这份运气不可靠了——
        // 直接问 PRAGMA journal_mode，这才是要测的事（WAL 是文件头属性，持久）。
        _db.AddVideo("https://www.bilibili.com/video/BV1GJ411x7h7", "BV1GJ411x7h7");
        _db.AddVideosBulk(new[] { new NewVideo("u", "BV1xx411c7mD", "t") });
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar()?.ToString());
    }

    [Fact]
    public void 单条入库_重复拒绝()
    {
        var url = "https://www.bilibili.com/video/BV1GJ411x7h7";
        var first = _db.AddVideo(url, "BV1GJ411x7h7");
        Assert.Equal(AddVideoResult.Inserted, first.Result);
        Assert.True(first.VideoId > 0);

        var second = _db.AddVideo(url + "?p=2", "BV1GJ411x7h7");
        Assert.Equal(AddVideoResult.Duplicate, second.Result);
        Assert.Equal(0, second.VideoId);
    }

    [Fact]
    public void 批量入库_库内与批内去重()
    {
        _db.AddVideo("u0", "BV1GJ411x7h7");

        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo("u1", "BV1GJ411x7h7", "撞库内"),
            new NewVideo("u2", "BV1xx411c7mD", "新1"),
            new NewVideo("u3", "BV1xx411c7mD", "批内重复"),
            new NewVideo("u4", "BV1qt411w7BE", "新2"),
        });

        Assert.Equal(2, outcome.Added);
        Assert.Equal(2, outcome.Duplicated);
        Assert.Equal(2, outcome.InsertedIds.Count);
        Assert.All(outcome.InsertedIds, id => Assert.True(id > 0));
    }

    [Fact]
    public void 列表按新到旧_标题可空()
    {
        _db.AddVideosBulk(new[]
        {
            new NewVideo("u1", "BV1GJ411x7h7", null),
            new NewVideo("u2", "BV1xx411c7mD", "有标题"),
        });

        var list = _db.ListVideos();
        Assert.Equal(2, list.Count);
        Assert.Equal("BV1xx411c7mD", list[0].Bvid);
        Assert.Equal("有标题", list[0].Title);
        Assert.Equal(VideoStatus.Pending, list[0].Status);
        Assert.Null(list[1].Title);
    }

    /// <summary>造一条走完提交流程的已到站视频，搜索测试用。upMid 只给需要拉开 UP 主区分度的用例用（不传维持原样：有 up 就给 100）。</summary>
    private long 已到站(string bvid, string? title, string? up = null, string? zone = null, string? views = null, long? upMid = null)
    {
        var id = _db.AddVideo("https://www.bilibili.com/video/" + bvid, bvid).VideoId;
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title, null);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(up, up is null ? null : (upMid ?? 100), zone, null, null, views),
            new[] { new PageDraft("task-" + bvid, 1, title ?? bvid, 1, "04:13", Path.Combine(_tempDir, "v", bvid + ".mp4")) });
        _db.MarkArrived(id);
        return id;
    }

    [Fact]
    public void 搜索_标题UP与BV号都能命中()
    {
        已到站("BV1Ax411d7jD", "流离之境", up: "七禾いえ", zone: "动画>综合");
        已到站("BV1Z2ny63E86", "另一首歌", up: "其他人", zone: "音乐");

        Assert.Single(_db.SearchVideos("流离", null));
        Assert.Single(_db.SearchVideos("七禾", null));
        Assert.Single(_db.SearchVideos("音乐", null));
        var byBvid = _db.SearchVideos("bv1ax", null);
        Assert.Single(byBvid); // SQLite LIKE 对 ASCII 大小写不敏感，BV 号随手大小写也能搜到
        Assert.Equal("BV1Ax411d7jD", byBvid[0].Bvid);
    }

    [Fact]
    public void 搜索_只看到已到站()
    {
        已到站("BV1Ax411d7jD", "已到站的歌");
        _db.AddVideo("u", "BV1xx411c7mD"); // Pending
        var failedId = _db.AddVideo("u", "BV1qt411w7BE").VideoId;
        _db.MarkSubmitted(failedId, new[] { "t" }, "失败的歌", null);
        _db.MarkFailed(failedId, "boom");

        Assert.Single(_db.SearchVideos("的歌", null));
        Assert.Empty(_db.SearchVideos("失败", null));
        Assert.Empty(_db.SearchVideos("BV1xx", null));
    }

    [Fact]
    public void 搜索_多词之间取交集()
    {
        已到站("BV1Ax411d7jD", "流离之境", up: "七禾いえ");
        已到站("BV1Z2ny63E86", "流离之外", up: "其他人");

        Assert.Single(_db.SearchVideos("流离 七禾", null));
        Assert.Equal(2, _db.SearchVideos("流离", null).Count);
        Assert.Empty(_db.SearchVideos("流离 不存在", null));
    }

    [Fact]
    public void 搜索_通配符按字面处理()
    {
        已到站("BV1Ax411d7jD", "100%原创");
        已到站("BV1Z2ny63E86", "百分之五十");

        Assert.Single(_db.SearchVideos("100%", null));
        Assert.Empty(_db.SearchVideos("100_x", null)); // _ 也是字面量，不是任意单字符
        Assert.Single(_db.SearchVideos("%", null));    // 不会被当成全表匹配
    }

    [Fact]
    public void 搜索_按播放数降序()
    {
        已到站("BV1Ax411d7jD", "冷门", views: "900");
        已到站("BV1Z2ny63E86", "爆款", views: "10000");
        已到站("BV1qt411w7BE", "中等", views: "4582");

        // 三支共享 BV1 前缀，一词命中全集，比较两种排序的首位。
        var hot = _db.SearchVideos("BV1", "plays");
        Assert.Equal(new[] { "爆款", "中等", "冷门" }, hot.Select(v => v.Title));

        var latest = _db.SearchVideos("BV1", null);
        Assert.Equal("中等", latest[0].Title); // 最后入库的排第一
    }

    [Fact]
    public void 搜索_万播放不能输给五千()
    {
        已到站("BV1Ax411d7jD", "小热门", views: "5000");
        已到站("BV1Z2ny63E86", "大爆款", views: "4618.3万");
        已到站("BV1qt411w7BE", "顶流", views: "2.1亿");

        var hot = _db.SearchVideos("BV1", "plays");
        Assert.Equal(new[] { "顶流", "大爆款", "小热门" }, hot.Select(v => v.Title));
    }

    [Fact]
    public void 搜索_空查询与长词限制()
    {
        已到站("BV1Ax411d7jD", "随便");
        Assert.Empty(_db.SearchVideos("   ", null));
        Assert.Single(_db.SearchVideos("随便", null));
    }

    [Fact]
    public void 列表_弹幕条数数Dialogue行()
    {
        var id = _db.AddVideo("u", "BV1Ax411d7jD").VideoId;
        _db.MarkSubmitted(id, new[] { "t1", "t2" }, "多P视频", null);
        var vDir = Path.Combine(_tempDir, "v");
        Directory.CreateDirectory(vDir);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("up", 1, "分区", null, null, "100"),
            new[]
            {
                new PageDraft("t1", 1, "P1", 1, "01:00", Path.Combine(vDir, "vid.P1.mp4")),
                new PageDraft("t2", 2, "P2", 2, "01:00", Path.Combine(vDir, "vid.P2.mp4")),
            });
        File.WriteAllText(Path.Combine(vDir, "vid.P1.mp4.ass"), "[Script Info]\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,首条\n; 注释行\nDialogue: 0,0:00:02.00,0:00:06.00,Default,,0,0,0,,次条\n");
        File.WriteAllText(Path.Combine(vDir, "vid.P2.mp4.ass"), "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,第三条\n");

        var (archived, _) = _db.FinalizeArtifacts(id, _tempDir);
        Assert.True(archived >= 2); // 两份 ass 都该扫进档案

        var entry = _db.ListVideos().Single(v => v.Bvid == "BV1Ax411d7jD");
        Assert.Equal(3, entry.DanmakuCount); // 两个 P 的 Dialogue 行求和，注释行不算

        // 没弹幕档案的给 null，前端整段不显，不给假 0
        _db.AddVideo("u2", "BV1Z2ny63E86");
        Assert.Null(_db.ListVideos().Single(v => v.Bvid == "BV1Z2ny63E86").DanmakuCount);
    }

    [Fact]
    public void 相关推荐_带出投稿时间与发布文本()
    {
        // 「接下来播放」显的必须是 B 站投稿时间（与首页卡阵同一口径），入站时间只是站长台账里的概念：
        // 全站批量下载后 created_at_utc 清一色同一天。后端先把两列备齐，前端才有的选。
        // 只验「库里存什么出来就是什么」；排序（同 UP 主优先、新入站补齐）不在本例管辖。
        var 自身 = _db.AddVideo("https://www.bilibili.com/video/BV1Ax411d7jD", "BV1Ax411d7jD").VideoId;

        var 稿件 = _db.AddVideosBulk(new[]
        {
            new NewVideo("https://www.bilibili.com/video/BV1Z2ny63E86", "BV1Z2ny63E86", "有投稿时间", null, "2024-03-01T12:00:00+08:00"),
        }).InsertedIds.Single();
        _db.MarkSubmitted(稿件, new[] { "task-rel" }, "有投稿时间", null);
        _db.SaveSubmissionMetadata(
            稿件,
            new VideoMetaDraft("某UP", 9, "音乐", "2024-03-01 12:00 发布", null, "100"),
            new[] { new PageDraft("task-rel", 1, "P1", 1, "04:13", Path.Combine(_tempDir, "v", "rel.mp4")) });
        _db.MarkArrived(稿件);

        // 存量裸货：两列都没存过 → 就该给 null，不拿入站时间冒充投稿时间
        var 裸 = _db.AddVideo("u", "BV1qt411w7BE").VideoId;
        _db.MarkArrived(裸);

        var related = _db.GetRelatedVideos(自身);
        var withDate = Assert.Single(related, r => r.Bvid == "BV1Z2ny63E86");
        Assert.Equal("2024-03-01T12:00:00+08:00", withDate.PublishedAtUtc);
        Assert.Equal("2024-03-01 12:00 发布", withDate.PublishText);

        var bare = Assert.Single(related, r => r.Bvid == "BV1qt411w7BE");
        Assert.Null(bare.PublishedAtUtc);
        Assert.Null(bare.PublishText);
        // created_at_utc 依旧带在手里（可能有别的消费方在用），只是不再拿它显时间
        Assert.True(bare.CreatedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void 相关推荐_同UP主池子比取数大时_层内随机不永远同批()
    {
        // 库里同 UP 主上百支时，按 id 倒序取 8 支就永远是最早那批新货（以前就是这样，刷新十次同几张脸）。
        // 打乱后该看到不同组合；同 UP 优先这一层不能被随机冲掉（一屏里掺进不相干的 UP 不像「接下来播放」）。
        var 自身 = 已到站("BV1Ax411d7jD", "我在看这支", up: "同一UP");
        foreach (var c in "ABCDEFGHIJKL")
            已到站($"BV1Z{c}ny63E86", $"同UP第{c}支", up: "同一UP");
        var 别家bvid = "BV1Qt411w7BE";
        var 无midbvid = "BV1Nn411w7BE";
        // 同 UP 名却没存 mid（老馆藏常见）：得走 up_name 那一层兑底，不能落到第二层去
        已到站(无midbvid, "同UP但没存mid", up: "同一UP", upMid: null);
        // 别家 UP（mid 拉开）：同 UP 池子够 8 支，不该上桌
        已到站(别家bvid, "别家UP的", up: "别家UP", upMid: 999);

        var 批次数 = new HashSet<string>();
        var 无名mid上过桌 = false;
        for (var round = 0; round < 30; round++)
        {
            var got = _db.GetRelatedVideos(自身, take: 8);
            Assert.Equal(8, got.Count);
            Assert.All(got, r => Assert.Equal("同一UP", r.UpName));
            Assert.DoesNotContain(got, r => r.Bvid == 别家bvid);
            无名mid上过桌 |= got.Any(r => r.Bvid == 无midbvid);
            批次数.Add(string.Join(",", got.Select(r => r.Bvid).OrderBy(x => x)));
        }

        // 30 次都抽到同一批的概率小到可以归零：真发生了就是 RANDOM() 没生效。
        Assert.True(批次数.Count > 1, $"30 次抽取次次同批，层内随机没生效：{string.Join(" | ", 批次数)}");
        // 池子 13 支取 8，那支「同 UP 名、无 mid」的 30 轮一次都没遇上就是 up_name 兑底没生效
        Assert.True(无名mid上过桌, "同 UP 名但没存 mid 的那支从未入列，up_name 那一层兑底失效");
    }

    [Fact]
    public void 编码版本_生命周期()
    {
        var id = 已到站("BV1Ax411d7jD", "多版视频", views: "100");
        var basePath = Path.Combine(_tempDir, "v", "vid.P1");

        // 登记：大小写归一，重复拒
        Assert.True(_db.AddCodecVersion(id, 1, "AV1"));
        Assert.False(_db.AddCodecVersion(id, 1, "av1"));
        Assert.False(_db.AddCodecVersion(id, 1, "vp9"));

        var rows = _db.GetCodecVersions(id);
        Assert.Single(rows);
        Assert.Equal("av1", rows[0].Codec);
        Assert.Equal("Downloading", rows[0].Status);

        // 提交回写
        _db.MarkCodecSubmitted(rows[0].Id, new[] { "task-9" }, basePath);
        var inFlight = _db.GetDownloadingCodecVersions();
        Assert.Single(inFlight);
        Assert.Contains("task-9", inFlight[0].TaskIdsJson);

        // 扫盘定档：基准路径前缀 + 视频后缀命中
        Directory.CreateDirectory(Path.Combine(_tempDir, "v"));
        File.WriteAllText(Path.Combine(_tempDir, "v", "vid.P1-AV1.mp4"), "fake-video");
        Assert.True(_db.FinalizeCodecVersion(rows[0].Id, basePath, _tempDir));

        rows = _db.GetCodecVersions(id);
        Assert.Equal("Arrived", rows[0].Status);
        Assert.Equal("v/vid.P1-AV1.mp4", rows[0].RelPath);

        // 失败的允许重跑
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var hevc = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        _db.MarkCodecFailed(hevc.Id, "引擎拒了");
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        Assert.Equal("Downloading", _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").Status);

        // 销账交回 rel_path 供删文件
        Assert.Equal("v/vid.P1-AV1.mp4", _db.TakeCodecVersionForDelete(id, 1, "av1"));
        Assert.Null(_db.TakeCodecVersionForDelete(id, 1, "av1"));

        // 整站连坐：删视频时表行一并清
        _db.DeleteVideoCascade(id, _tempDir);
        Assert.Empty(_db.GetCodecVersions(id));
    }

    [Fact]
    public void 编码版本_判重让路只排队不判死()
    {
        // 引擎判重键不含编码，同稿并发加两个编码时后到的必被判「已存在未完成的同名任务」；
        // 这类行得挂在途等回炉，不能一票否决成 Failed。
        var id = 已到站("BV1Ax411d7jD", "多版视频", views: "100");
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var rowId = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").Id;
        Assert.Equal(0, _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").RetryCount);

        var due = DateTimeOffset.UtcNow.AddSeconds(30);
        _db.MarkCodecDeferred(rowId, due, "已存在未完成的同名任务，跳过：多版视频");

        // 让路不是失败：行还在途，只是挂了到期时间 + 记下引擎给的原因
        var deferred = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        Assert.Equal("Downloading", deferred.Status);
        Assert.Contains("未完成的同名任务", deferred.LastError);
        Assert.Equal(1, deferred.RetryCount);
        Assert.Empty(_db.GetDueCodecRetries(DateTimeOffset.UtcNow)); // 还没到期，不能提前回炉

        // 站长室那块拿同一列辨「排队中 / 进站中」（retry_at_utc 有值即排队中）
        var queued = Assert.Single(_db.GetCodecQueueRows(), r => r.VideoId == id);
        Assert.Equal("BV1Ax411d7jD", queued.Bvid);
        Assert.Equal("多版视频", queued.Title);
        Assert.Equal("hevc", queued.Codec);
        Assert.Equal(1, queued.PageIdx);
        Assert.True(queued.Waiting);

        // 到期的才取得到
        var now = due.AddSeconds(1);
        Assert.Contains(_db.GetDueCodecRetries(now), r => r.Id == rowId);

        // 抢租约：抢到之后同一条不再立即可取（防相邻两轮连投两次），下一家也就抢不到
        Assert.True(_db.TakeCodecRetryLease(rowId, now, now.AddSeconds(90)));
        Assert.Empty(_db.GetDueCodecRetries(now));
        Assert.False(_db.TakeCodecRetryLease(rowId, now, now.AddSeconds(90)));

        // 真开跑了：销排队标记（站长室那块不再显示「排队中」），让路次数留着算退避
        _db.ClearCodecRetry(id, "hevc");
        Assert.Empty(_db.GetDueCodecRetries(now.AddYears(1)));
        Assert.False(Assert.Single(_db.GetCodecQueueRows(), r => r.VideoId == id).Waiting);
        Assert.Equal(1, _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").RetryCount);

        // 再撞再让：次数累加（退避长度与「放弃重试」都只看它）
        _db.MarkCodecDeferred(rowId, DateTimeOffset.UtcNow, "又撞一次");
        Assert.Equal(2, _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").RetryCount);

        // 真拿到引擎任务 = 让路 saga 结束：标记与次数一起销账，下次重新排队不翻老账
        _db.MarkCodecSubmitted(rowId, new[] { "task-hevc" }, Path.Combine(_tempDir, "v", "多版视频.P1"));
        var submitted = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc");
        Assert.Equal(0, submitted.RetryCount);
        Assert.Empty(_db.GetDueCodecRetries(DateTimeOffset.UtcNow.AddYears(1)));

        // 挂在途的行不会被误拾：没到期 / 没标记都不算
        Assert.Empty(_db.GetDueCodecRetries(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void 编码版本_分轨码率读写往返()
    {
        var id = 已到站("BV1Ax411d7jD", "分轨码率", views: "100");
        var basePath = Path.Combine(_tempDir, "v", "kbps.P1");
        Directory.CreateDirectory(Path.Combine(_tempDir, "v"));
        File.WriteAllText(Path.Combine(_tempDir, "v", "kbps.P1-HEVC.mp4"), "假文件（ffprobe 认不出）");
        Assert.True(_db.AddCodecVersion(id, 1, "hevc"));
        var rowId = _db.GetCodecVersions(id).Single(r => r.Codec == "hevc").Id;
        Assert.True(_db.FinalizeCodecVersion(rowId, basePath, _tempDir));

        // 开观看页是热路径：读侧一概不探。没写过的两列就是 NULL（不给 0 冒充零码率）；
        // 而且读完 stream_probed 仍是 NULL —— 读侧一旦偷偷起 ffprobe，这枚戳会被盖成 1。
        var first = Assert.Single(Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).CodecVersions);
        Assert.Null(first.VideoKbps);
        Assert.Null(first.AudioKbps);
        Assert.Null(加档行防抖戳(id, 1, "hevc"));

        // 文件压根不在盘上（被手删 / rel 指向不存在的路径）也一样正常返回 null 不抛，照旧不盖戳。
        File.Delete(Path.Combine(_tempDir, "v", "kbps.P1-HEVC.mp4"));
        var 缺件 = Assert.Single(Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).CodecVersions);
        Assert.Null(缺件.VideoKbps);
        Assert.Null(缺件.Bytes);
        Assert.Null(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages[0].VideoKbps); // 主版本正片还没落盘
        Assert.Null(加档行防抖戳(id, 1, "hevc"));

        // 写入侧（同步员定档那一刻探完落库）写进来就原样吐回去（JSON 契约：videoKbps / audioKbps）
        _db.SetCodecStreams(rowId, 1200, 128, probed: true);
        var written = Assert.Single(Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).CodecVersions);
        Assert.Equal(1200, written.VideoKbps);
        Assert.Equal(128, written.AudioKbps);
        Assert.Equal(1, 加档行防抖戳(id, 1, "hevc"));

        // probed=false 只销防抖戳（留着写入侧重探），已落库的值不清空
        _db.SetCodecStreams(rowId, null, null, probed: false);
        Assert.Null(加档行防抖戳(id, 1, "hevc"));
        Assert.Equal(1200, Assert.Single(Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).CodecVersions).VideoKbps);

        // 主版本同一口径：到站那一刻一趟 ProbeStreams 把 codec + 双轨 kbps + 防抖戳一次写掉
        _db.SetPageStreams(id, 1, "avc", 2400, 192);
        var main = Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages);
        Assert.Equal("avc", main.Codec);
        Assert.Equal(2400, main.VideoKbps);
        Assert.Equal(192, main.AudioKbps);
        Assert.Equal(1, 分页防抖戳(id, 1));

        // 没实测到编码名也不拿 null 去擦已有的 codec：播放页版本区块拿它决定整块显不显
        _db.SetPageStreams(id, 1, null, null, null);
        Assert.Equal("avc", Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).Codec);
    }

    /// <summary>直读加档行的 stream_probed（NULL=还没探过，1=探过一次）：验「库被动过没有」而不靠推断。</summary>
    private int? 加档行防抖戳(long videoId, int pageIdx, string codec)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stream_probed FROM video_codecs WHERE video_id = $id AND page_idx = $page AND codec = $codec;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$page", pageIdx);
        command.Parameters.AddWithValue("$codec", codec);
        return 台账取整数(command);
    }

    /// <summary>直读主版本行的 stream_probed（同上，拿分页编号）。</summary>
    private int? 分页防抖戳(long videoId, int pageIdx)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stream_probed FROM pages WHERE video_id = $id AND idx = $idx;";
        command.Parameters.AddWithValue("$id", videoId);
        command.Parameters.AddWithValue("$idx", pageIdx);
        return 台账取整数(command);
    }

    private static int? 台账取整数(SqliteCommand command)
    {
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    [Fact]
    public void 老库缺分轨与让路列_补列后读写照旧()
    {
        // 只验 EnsureColumn 补列不拖死读路（新库一开始就带列，只有早年建的库会走到这里）。
        // 不做任何回填/自愈：老行没实测值就是 NULL，前端按设计省略码率段（那批货会整体重新下）。
        var id = 已到站("BV1Ax411d7jD", "老库货", views: "100");
        Assert.True(_db.AddCodecVersion(id, 1, "av1"));
        var rowId = _db.GetCodecVersions(id).Single(r => r.Codec == "av1").Id;

        // 装成 M 早期的库：六列全部拆掉，重开闸门（EnsureColumn）得把账补齐，而且不能拖死读路。
        var dbFile = Path.Combine(_tempDir, "station.db");
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={dbFile}"))
        {
            connection.Open();
            foreach (var sql in new[]
                     {
                         "ALTER TABLE pages DROP COLUMN video_kbps;",
                         "ALTER TABLE pages DROP COLUMN audio_kbps;",
                         "ALTER TABLE pages DROP COLUMN stream_probed;",
                         "ALTER TABLE video_codecs DROP COLUMN video_kbps;",
                         "ALTER TABLE video_codecs DROP COLUMN audio_kbps;",
                         "ALTER TABLE video_codecs DROP COLUMN stream_probed;",
                         "ALTER TABLE video_codecs DROP COLUMN retry_at_utc;",
                         "ALTER TABLE video_codecs DROP COLUMN retry_count;",
                     })
            {
                using var drop = connection.CreateCommand();
                drop.CommandText = sql;
                drop.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        _db.Initialize(); // 重开闸门：EnsureColumn 把列补回来

        var data = _db.GetPlaybackData("BV1Ax411d7jD");
        Assert.NotNull(data);
        Assert.Null(Assert.Single(Assert.Single(data!.Pages).CodecVersions).VideoKbps); // 老行没实测值 → NULL，不给 0
        Assert.Null(加档行防抖戳(id, 1, "av1")); // 也不拿老行去补探：读侧一个文件都不扰
        Assert.Empty(_db.GetDueCodecRetries(DateTimeOffset.UtcNow)); // 老行 retry_at_utc 为空，不会被误当排队中
        Assert.False(Assert.Single(_db.GetCodecQueueRows(), r => r.VideoId == id).Waiting); // 快照照读，不炸
        Assert.Equal(0, _db.GetCodecVersions(id).Single(r => r.Codec == "av1").RetryCount);

        // 补回来的列能用：让路与分轨码率都照旧
        _db.MarkCodecDeferred(rowId, DateTimeOffset.UtcNow.AddSeconds(30), "已存在未完成的同名任务");
        Assert.Equal("Downloading", _db.GetCodecVersions(id).Single(r => r.Codec == "av1").Status);
        _db.SetCodecStreams(rowId, 800, 96, probed: true);
        var row = Assert.Single(Assert.Single(_db.GetPlaybackData("BV1Ax411d7jD")!.Pages).CodecVersions);
        Assert.Equal(800, row.VideoKbps);
        Assert.Equal(96, row.AudioKbps);
        Assert.Contains(_db.GetDueCodecRetries(DateTimeOffset.UtcNow.AddMinutes(1)), r => r.Id == rowId);
    }

    /// <summary>造一条到站视频：OutputBasePath 由调用方摆到指定根（无扩展基名，供 FinalizeArtifacts 扫盘），封面链可指定。</summary>
    private long 已到站扫盘(string bvid, string? coverUrl, string outputBasePath)
    {
        var id = _db.AddVideo("https://www.bilibili.com/video/" + bvid, bvid).VideoId;
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, bvid, coverUrl);
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(null, null, null, null, null, null),
            new[] { new PageDraft("task-" + bvid, 1, bvid, 1, "04:13", outputBasePath) });
        _db.MarkArrived(id);
        return id;
    }

    [Fact]
    public void 列表行_带出本地封面路径()
    {
        // 归档出 .Cover.jpg 的行给 /media/ 前缀（与观看页同形）；没归档封面的行留 null，前端热链兑底。
        var root = Path.Combine(_tempDir, "cv");
        Directory.CreateDirectory(root);
        var withCover = 已到站扫盘("BVCover00001", null, Path.Combine(root, "BVCover00001"));
        var noCover = 已到站扫盘("BVCover00002", null, Path.Combine(root, "BVCover00002"));
        File.WriteAllText(Path.Combine(root, "BVCover00001.mp4"), "v");
        File.WriteAllText(Path.Combine(root, "BVCover00001.Cover.jpg"), "c");
        File.WriteAllText(Path.Combine(root, "BVCover00002.mp4"), "v");
        _db.FinalizeArtifacts(withCover, root);
        _db.FinalizeArtifacts(noCover, root);

        Assert.Equal("/media/BVCover00001.Cover.jpg", Assert.Single(_db.ListVideos(), v => v.Bvid == "BVCover00001").LocalCover);
        Assert.Null(Assert.Single(_db.ListVideos(), v => v.Bvid == "BVCover00002").LocalCover);
        // 搜索同源同形（ReadEntries 共用，不给搜索页留第二条腿）。
        Assert.Equal("/media/BVCover00001.Cover.jpg", Assert.Single(_db.SearchVideos("BVCover00001", null), v => v.Bvid == "BVCover00001").LocalCover);
    }

    [Fact]
    public void 封面补员名单_只点没封面的到站货()
    {
        var root = Path.Combine(_tempDir, "cv2");
        Directory.CreateDirectory(root);

        // 欠账：有正片行没封面行 → 入列，BaseRel 认正片行供推基名。
        var debt = 已到站扫盘("BVDebt00001", "https://i0.hdslb.com/c.jpg", Path.Combine(root, "BVDebt00001"));
        File.WriteAllText(Path.Combine(root, "BVDebt00001.mp4"), "v");
        _db.FinalizeArtifacts(debt, root);
        // 已齐：封面行在 → 不入列。
        var settled = 已到站扫盘("BVFull00001", "https://i0.hdslb.com/c.jpg", Path.Combine(root, "BVFull00001"));
        File.WriteAllText(Path.Combine(root, "BVFull00001.mp4"), "v");
        File.WriteAllText(Path.Combine(root, "BVFull00001.Cover.jpg"), "c");
        _db.FinalizeArtifacts(settled, root);
        // 没封面链 → 不入列；完全无产物行（没处落盘）→ 不入列。
        var noUrl = 已到站扫盘("BVNoUrl0001", null, Path.Combine(root, "BVNoUrl0001"));
        File.WriteAllText(Path.Combine(root, "BVNoUrl0001.mp4"), "v");
        _db.FinalizeArtifacts(noUrl, root);
        已到站扫盘("BVHomeless1", "https://i0.hdslb.com/c.jpg", Path.Combine(root, "BVHomeless1"));
        // 没到站的欠着也不算。
        _db.AddVideosBulk(new[] { new NewVideo("u", "BVPending001", "排队货", "https://i0.hdslb.com/c.jpg") });

        var candidate = Assert.Single(_db.ListVideosMissingCover());
        Assert.Equal(debt, candidate.VideoId);
        Assert.Equal("BVDebt00001.mp4", candidate.BaseRel);

        // 补员登记后退出名单；同名行幂等不重。
        _db.RegisterCoverArtifact(debt, "BVDebt00001.Cover.jpg");
        _db.RegisterCoverArtifact(debt, "BVDebt00001.Cover.jpg");
        Assert.Empty(_db.ListVideosMissingCover());
    }

    [Fact]
    public void 等级补员_只捞没登号的并幂等盖章()
    {
        // 存量行是 INSERT OR IGNORE 进的不会回头补：补员只捞 level 空且有 mid 的行，同 mid 一次盖齐，已有值不重盖。
        _db.UpsertDynamic("DYN001", 100, 200, "甲", "DYNAMIC_TYPE_AV", "正文", null, null, null);
        _db.UpsertComments("DYN001", new[]
        {
            new StationDb.DynamicCommentRow(1, 0, 0, 700, "甲", null, "楼一", 0, null),
            new StationDb.DynamicCommentRow(2, 0, 0, 700, "甲", null, "楼二", 0, null),
            new StationDb.DynamicCommentRow(3, 0, 0, 701, "乙", null, "楼三", 0, null, null, null, null, 4),
            new StationDb.DynamicCommentRow(4, 0, 0, null, "无名", null, "楼四", 0, null),
        });

        Assert.Equal([700L], _db.ListPendingLevelMids(10));
        Assert.Equal(2, _db.BackfillCommentLevel(700, 5));
        Assert.Equal(0, _db.BackfillCommentLevel(700, 6)); // 幂等：盖过戳的不回头
        Assert.Empty(_db.ListPendingLevelMids(10));

        var rows = _db.ListDynamicComments("DYN001");
        Assert.Equal(5, Assert.Single(rows, r => r.Rpid == 1).Level);
        Assert.Equal(5, Assert.Single(rows, r => r.Rpid == 2).Level);
        Assert.Equal(4, Assert.Single(rows, r => r.Rpid == 3).Level); // 入库自带值原样保
        Assert.Null(Assert.Single(rows, r => r.Rpid == 4).Level);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
