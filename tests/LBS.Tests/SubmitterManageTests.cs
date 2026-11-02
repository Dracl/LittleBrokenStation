using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;

namespace LBS.Tests;

/// <summary>
/// 投稿人自管三件套（刷评论快照 / 重拉 B 站弹幕 / 删自己的稿）的门闸归属与归属查询。
/// 这三条从站长室摘出来交给 MemberGate + 端点内自核，判据一旦漂走就是「投稿人进不来」或「匿名敞着」，
/// 所以把两张门的答案钉成一张表。
/// </summary>
public sealed class SubmitterManageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;
    private readonly AdminGate _gate = new(null!, null!);

    public SubmitterManageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-submitter-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _db = new StationDb(new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir));
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

    private long AddVideo(string bvid) =>
        _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;

    private static HttpRequest Request(string method, string path) => new DefaultHttpContext
    {
        Request = { Method = method, Path = path },
    }.Request;

    [Theory]
    // 投稿人自管：从站长室里摘出来（false），改由会员门 + 端点内自核兜着
    [InlineData("DELETE", "/api/videos/123", false, true)]
    [InlineData("POST", "/api/videos/123/comments", false, true)]
    [InlineData("POST", "/api/videos/123/comments/subs", false, true)]
    [InlineData("POST", "/api/videos/123/danmaku-refresh", false, true)]
    // 站长专属：续下/重新排队/加档/按编码删档，一个字都不能松
    [InlineData("POST", "/api/videos/123/revive", true, false)]
    [InlineData("POST", "/api/videos/123/requeue", true, false)]
    [InlineData("POST", "/api/videos/123/codecs", true, false)]
    [InlineData("DELETE", "/api/videos/123/codecs/hevc", true, false)]
    // 站内弹幕（自带本人/主人规则）与只读配套设施：照旧
    [InlineData("DELETE", "/api/videos/danmaku/9", false, true)]   // 站内弹幕删除：不属站长室，但要会员会话（与弹幕读写同闸）
    [InlineData("POST", "/api/videos/9/danmaku/9/report", false, true)] // 站内弹幕举报：吃会员门（路径含 /danmaku），不给站长室抢去
    [InlineData("POST", "/api/videos/danmaku/9/report", false, true)]  // 同一形状压在弹幕删除那一口下：照样只吃会员门
    [InlineData("GET", "/api/videos/123/danmaku", false, true)]
    [InlineData("GET", "/api/videos/123/related", false, true)]
    [InlineData("GET", "/api/videos", false, false)]
    [InlineData("POST", "/api/videos", false, true)]
    // 举报清单与处置：整段挂在 /api/admin/ 前缀下，身份即钥匙（站长门管，会员门不管）
    [InlineData("GET", "/api/admin/danmaku-reports", true, false)]
    [InlineData("POST", "/api/admin/danmaku-reports/9/handle", true, false)]
    // 观看历史：报进度的 /progress 归放映厅配套设施（吃会员门）；
    // /api/member/history 那三条不在两张门视野里（两端都 false），端点自己查 MemberGate.Current 回 401。
    [InlineData("POST", "/api/videos/9/progress", false, true)]
    [InlineData("GET", "/api/videos/9/progress", false, true)]
    // 加档前的预估规格：表面只读，其实每一发都替分站出去打 playurl（风控敏感口、吃的是采集号）→ 访客不给。
    [InlineData("GET", "/api/videos/9/codecs/estimates", false, true)]
    // 加档实况：形状压在 /progress 那条规则上（同样吃会员门），一并钉住免得哪天改门改飘。
    [InlineData("GET", "/api/videos/9/codecs/progress", false, true)]
    [InlineData("GET", "/api/member/history", false, false)]
    [InlineData("POST", "/api/member/history/delete", false, false)]
    [InlineData("POST", "/api/member/history/clear", false, false)]
    // 分区：只读两口（顶级清单/卡阵）与首页同级公开；改派挂 /api/admin/ 前缀，身份即钥匙（站长门管，会员门不管）。
    [InlineData("GET", "/api/zones", false, false)]
    [InlineData("GET", "/api/zones/videos", false, false)]
    [InlineData("POST", "/api/admin/zones/set", true, false)]
    // 合集：名录与首页同级公开（首页番剧模式那排入口卡靠它摆封面）；但「点进某一架」是进站待遇，
    // /api/collections/{id} 已被会员门拿下（详情里是稿件清单与简介，不是张封面）。
    // 开集/销集/加减片子挂在 /api/member/collections 下，这个前缀不在两张门视野里（两端都 false），端点自己查 MemberGate.Current 回 401（与 /api/member/history 同一防法）。
    [InlineData("GET", "/api/collections", false, false)]
    [InlineData("GET", "/api/member/collections", false, false)]
    [InlineData("POST", "/api/member/collections", false, false)]
    // 访客只看主页（2026-11-02 站长定）：动态整页与合集详情吃会员门；分区两口与合集名录照旧公开，
    // 站长要留的正是首页那份封面预览 —— 可以看见，不可以点进去。
    [InlineData("GET", "/api/collections/9", false, true)]
    [InlineData("GET", "/api/collections/9/", false, true)]  // 尾斜杠不许穿门
    [InlineData("PATCH", "/api/collections/9", false, true)]  // 合集抽屉那口从「两张门外面自防」收进门里（归属仍归端点里的 ManageGate 核）
    [InlineData("GET", "/api/dynamics", false, true)]
    [InlineData("GET", "/api/dynamics/targets", false, true)]
    [InlineData("GET", "/api/dynamics/1234567890/comments", false, true)]
    [InlineData("POST", "/api/dynamics/1234567890/comments/refresh", false, true)]
    [InlineData("GET", "/api/dynamics/1234567890/comments/", false, true)]  // 同样不许拿斜杠穿
    // UP 空间（放映厅名片那颗蓝钮）：里头是一份稿件清单，与合集详情同权 —— 名录可逛、清单进站。
    // 只拿 GET：POST /api/up/import（UP 主整搬）在投稿那一段已点名，两枚不能互相顶替。
    [InlineData("GET", "/api/up/556980/videos", false, true)]
    [InlineData("POST", "/api/up/556980/videos", false, true)]  // POST /api/up/ 整段早就在投稿那一段点名（UP 主整搬）：这一格钉的是「两支各自成立，不靠对方顶」
    public void 两张门的归属矩阵(string method, string path, bool expectAdmin, bool expectMember)
    {
        var request = Request(method, path);
        Assert.Equal(expectAdmin, _gate.IsAdminRequest(request));
        Assert.Equal(expectMember, MemberGate.RequiresMember(request));
    }

    [Theory]
    // 尾斜杠穿门回归（2026-10-31 对活站实测漏过）：路由把 /x 与 /x/ 当同一扇门，两道门进门先 TrimEnd('/') 再点名。
    // 同一扇门带不带斜杠必须落在同一格：门既不能被穿，也不能被剪子误封。
    [InlineData("POST", "/api/videos/", false, true)]
    [InlineData("GET", "/api/videos/123/related/", false, true)]
    [InlineData("GET", "/api/videos/123/download/", false, true)]
    [InlineData("GET", "/api/videos/123/progress/", false, true)]
    [InlineData("GET", "/api/watch/BV1x411c7mD/", false, true)]
    [InlineData("DELETE", "/api/videos/123/", false, true)]
    [InlineData("POST", "/api/videos/123/comments/", false, true)]
    [InlineData("POST", "/api/videos/123/comments/subs/", false, true)]
    [InlineData("POST", "/api/videos/123/danmaku-refresh/", false, true)]
    [InlineData("GET", "/api/stats/collect/", true, false)]
    [InlineData("GET", "/api/station/throttle/", true, false)]
    [InlineData("GET", "/api/stats/preview/", true, false)]
    [InlineData("GET", "/api/admin/stats/", true, false)]
    [InlineData("GET", "/api/admin", true, false)]
    [InlineData("GET", "/api/downloads", true, false)]
    [InlineData("GET", "/api/downloads/", true, false)]
    [InlineData("GET", "/api/settings/", true, false)]
    // 公开门面不被剪子误封：带斜杠照样放行访客。
    [InlineData("GET", "/api/videos/", false, false)]
    [InlineData("GET", "/api/collections/", false, false)]
    // 动态已从公开门面收进门里（2026-11-02 站长定：访客只看主页），这一格钉的仍是同一件事：
    // 带不带斜杠都得落在门里，不能一斜杠就漏到公开那侧。
    [InlineData("GET", "/api/dynamics/targets/", false, true)]
    [InlineData("GET", "/api/collections/9/", false, true)]
    public void 尾斜杠穿门回归(string method, string path, bool expectAdmin, bool expectMember)
    {
        var request = Request(method, path);
        Assert.Equal(expectAdmin, _gate.IsAdminRequest(request));
        Assert.Equal(expectMember, MemberGate.RequiresMember(request));
    }

    [Fact]
    public void 投稿归属_写入后查得到()
    {
        var mine = AddVideo("BV1mine00001");
        var others = AddVideo("BV1other00002");
        var crawler = AddVideo("BV1crawl00003");

        _db.SetVideoSubmitterMember(mine, 7);
        _db.SetVideoSubmitterMember(others, 8);

        Assert.Equal(7L, _db.GetVideoSubmitterMember(mine));
        Assert.Equal(8L, _db.GetVideoSubmitterMember(others));
        Assert.Null(_db.GetVideoSubmitterMember(crawler)); // 爬工代投：无归属，只有站长能自管

        var map = _db.GetVideoSubmitters();
        Assert.Equal(7L, map[mine]);
        Assert.Equal(8L, map[others]);
        Assert.Null(map[crawler]);
        Assert.False(map.TryGetValue(999999, out _));
    }

    [Fact]
    public void 弹幕重拉targets_带上cid与基路径()
    {
        var videoId = AddVideo("BV1dm0000001");
        var first = Path.Combine(_videosDir, "测试标题", "1-正片");
        var second = Path.Combine(_videosDir, "测试标题", "2-幕后");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[]
            {
                new PageDraft("t1", 1, "正片", 456, "03:30", first),
                new PageDraft("t2", 2, "幕后", null, "01:00", second),
            });

        var targets = _db.GetDanmakuPageTargets(videoId);

        Assert.Equal(2, targets.Count);
        Assert.Equal(1, targets[0].Idx);
        Assert.Equal(456L, targets[0].Cid);
        Assert.Equal(first, targets[0].BasePath);
        Assert.Equal(2, targets[1].Idx);
        Assert.Null(targets[1].Cid); // 缺 cid 的分 P 由服务侧点名跳过
        Assert.Equal(second, targets[1].BasePath);
        Assert.Empty(_db.GetDanmakuPageTargets(424242));
    }

    [Fact]
    public void 同P的ass与xml并存时_优先给ass()
    {
        var videoId = AddVideo("BV1dm0000002");
        var dir = Path.Combine(_videosDir, "两行台账");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片-1080P-AVC");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + ".xml", "danmaku");
        File.WriteAllText(@base + ".ass", "danmaku");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("t1", 1, "正片", 1, "03:30", @base) });
        _db.FinalizeArtifacts(videoId, _videosDir);

        // 前端只会解 ass：台账里 xml 那行抢了先，重拉完看着就像「没生效」。
        Assert.Equal("/media/两行台账/1-正片-1080P-AVC.ass", _db.GetWatchData("BV1dm0000002")!.Pages[0].DanmakuUrl);
    }

    [Fact]
    public void 只有xml时_不硬造ass指针()
    {
        var videoId = AddVideo("BV1dm0000003");
        var dir = Path.Combine(_videosDir, "只有xml");
        Directory.CreateDirectory(dir);
        var @base = Path.Combine(dir, "1-正片");
        File.WriteAllText(@base + ".mp4", "video");
        File.WriteAllText(@base + ".xml", "danmaku");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("t1", 1, "正片", 1, "03:30", @base) });
        _db.FinalizeArtifacts(videoId, _videosDir);

        Assert.Equal("/media/只有xml/1-正片.xml", _db.GetWatchData("BV1dm0000003")!.Pages[0].DanmakuUrl);
    }

    [Theory]
    [InlineData("[Script Info]\nTitle: x\n[Events]\nFormat: a\nDialogue: 0,0:00:01,cat\n", true)]
    [InlineData("[Script Info]\n[Events]\n", true)]           // 真没弹幕的合法空壳，照样能落盘
    [InlineData("[Script Info]\nTitle: x\n", false)]           // 断在半路，没有事件段
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ass验货(string text, bool usable) => Assert.Equal(usable, DanmakuArchiveService.IsUsableAss(text));

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<i><d p=\"1,1,25,16777215\">你好</d></i>", true)]
    [InlineData("<i></i>", false)]
    [InlineData("<i><p>1</p></i>", false)]
    [InlineData("", false)]
    public void xml验货(string text, bool usable) => Assert.Equal(usable, DanmakuArchiveService.IsUsableXml(text));

    [Fact]
    public void 排版算法对齐引擎字符串()
    {
        Assert.Equal("none", DanmakuArchiveService.LayoutAlgorithmValue(DownKyi.Application.Settings.DanmakuLayoutAlgorithm.None));
        Assert.Equal("async", DanmakuArchiveService.LayoutAlgorithmValue(DownKyi.Application.Settings.DanmakuLayoutAlgorithm.Async));
        Assert.Equal("sync", DanmakuArchiveService.LayoutAlgorithmValue(DownKyi.Application.Settings.DanmakuLayoutAlgorithm.Sync));
    }

    [Fact]
    public void 旧档弹幕行数数得清_文件不在当没得比()
    {
        var dir = Path.Combine(_tempDir, "count-ass");
        Directory.CreateDirectory(dir);
        var ass = Path.Combine(dir, "a.ass");
        File.WriteAllText(ass, "[Script Info]\n[Events]\nDialogue: 0,0:00:01,0:00:06,Default,,0,0,0,,你好\nDialogue: 0,0:00:02,0:00:07,Default,,0,0,0,,在吗\n");

        Assert.Equal(2, DanmakuArchiveService.CountDialogue(ass));
        Assert.Equal(-1, DanmakuArchiveService.CountDialogue(Path.Combine(dir, "没这个文件.ass")));
    }

    [Fact]
    public void WatchData与VideoEntry默认不给自管权()
    {
        var videoId = AddVideo("BV1flag00001");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "区", "时间", "简介", "播放"),
            new[] { new PageDraft("t1", 1, "正片", 1, "03:30", Path.Combine(_videosDir, "flag")) });

        // 权限布尔归端点算（它才知道当前会话是谁），数据层给的必须是假，免得哪里漏赋值就白送。
        Assert.False(_db.GetWatchData("BV1flag00001")!.CanManage);
        Assert.All(_db.ListVideos(), v => Assert.False(v.CanManage));
    }
}
