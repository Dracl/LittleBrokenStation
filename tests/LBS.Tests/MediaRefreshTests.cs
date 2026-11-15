using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;

namespace LBS.Tests;

/// <summary>
/// 「重拉封面与媒体」的后端半边。本仓没有 HTTP harness，钉的是数据与判据本身（照 ManageGate / SubmitterManageTests 那族的做法）：
/// ① <b>档案重取走窄口 UPDATE</b>——最要紧的一条：UP 名片（up_face/up_sign/up_fans）、互动数据（stat_*）、创作团队（staff_json）
///    是另几拨采集员攒出来的家底，绝不允许被 SaveSubmissionMetadata 那种 DELETE 重插抹掉；
/// ② 站内覆写三栏（site_title / site_desc / site_zone）与 videos.title 一个字节不许被 B 站那份冲掉；
/// ③ 单条缺件判定与「有缺才回炉」（判据与开机全库扫同源，预览行不算缺件、健康馆藏一个下载都不许触发）；
/// ④ 封面件同 rel 二次登记只一行（原地覆盖不产生第二行）+ 封面名推导与补员同一条规矩；
/// ⑤ 两张门的归属：MemberGate 认得 POST …/media-refresh（匿名进不来、尾斜杠穿不了门），钥匙只有投稿人本人与站长握得住；
/// ⑥ /media 封面图的 no-cache + ETag：图片才贴头，视频与音频一律不动，If-None-Match 对上了才 304。
/// 真出去问 B 站档案、真抓封面件那两段要网络与活号，不在本文件（见汇报「未验」）。
/// </summary>
public sealed class MediaRefreshTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly AdminGate _gate = new(null!, null!);

    public MediaRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-media-refresh-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        _paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    /// <summary>造一条到站馆藏：正片 + ass +（可省）封面三件落盘、一个分 P、档案七栏齐、带一枚旧封面热链。</summary>
    private long SeedArrived(string bvid, out string basePath, bool withCover = true)
    {
        var videoId = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, "B站原标题", "https://pic.example/old.jpg"),
        }).InsertedIds[0];
        var dir = Path.Combine(_videosDir, bvid);
        Directory.CreateDirectory(dir);
        basePath = Path.Combine(dir, $"1-{bvid}-1080P-AVC");
        File.WriteAllText(basePath + ".mp4", "video-bytes");
        File.WriteAllText(basePath + ".ass", "Dialogue: 0,0:00:01.00,0:00:06.00,默认,,0,0,0,,一条");
        if (withCover)
        {
            File.WriteAllText(basePath + ".Cover.jpg", "cover-bytes");
        }

        _db.MarkSubmitted(videoId, new[] { "task-" + bvid }, "B站原标题", "https://pic.example/old.jpg");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("B站原UP", 123, "音乐>翻唱", "2026-05-20 18:00:00", "B站原简介", "1万"),
            new[] { new PageDraft("task-" + bvid, 1, "正片", 456, "04:13", basePath) });
        _db.MarkArrived(videoId);
        _db.FinalizeArtifacts(videoId, _videosDir);
        return videoId;
    }

    private long SeedArrived(string bvid, bool withCover = true) => SeedArrived(bvid, out _, withCover);

    /// <summary>造一个在席席位（判身份只吃 Id 与 Role，其余无关；照 VideoSettingsTests 同一做法）。</summary>
    private long Seat(string name)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        Assert.NotNull(_db.BindMember(id, 960_000 + id, name, null));
        return id;
    }

    private static StationMember Member(long id, string role) =>
        new(id, 1000 + id, "席位" + id, null, null, MemberStatus.Active, role, DateTimeOffset.UnixEpoch.ToString("O"), null, null);

    private static HttpRequest Request(string method, string path) => new DefaultHttpContext
    {
        Request = { Method = method, Path = path },
    }.Request;

    /// <summary>把内存通道里的排队 id 抽干净（200ms 内没货就算抽完）。</summary>
    private static async Task<List<long>> 抽队列(DownloadQueue queue)
    {
        var ids = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var id in queue.ReadAllAsync(cts.Token))
            {
                ids.Add(id);
            }
        }
        catch (OperationCanceledException)
        {
            // 通道空了就到此为止
        }

        return ids;
    }

    /// <summary>绕开读层直取列：钉「这一列还在 / 这一列没被写」时不看有效值那张嘴的脸色。</summary>
    private string? 直读文本(string sql, long id)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    // ─── ① 档案重取只走窄口 ───────────────────────────────────────────────

    [Fact]
    public void 档案重取只动那六栏_名片互动与创作团队一条不许掉()
    {
        var id = SeedArrived("BVmref000001");

        // 另几拨采集员的家底：UP 名片、五维互动、创作团队（这条防的就是 SaveSubmissionMetadata 那种 DELETE 重插）
        _db.SetUpInfo(id, "/avatars/up-1.jpg", "站长测试签名", 12_345);
        _db.SetVideoStat(id, 11, 22, 33, 44, 55);
        _db.SetVideoStaff(id, """[{"mid":7,"name":"剪辑","face":"f","title":"剪辑"}]""");

        var (meta, cover) = _db.UpdateArchiveFromView(id, new ArchiveRefreshPatch(
            Zone: "鬼畜>调教",
            Description: "B站新简介",
            ViewCountText: "1.2万",
            UpName: "B站新UP",
            UpMid: 456,
            PublishText: "2026-06-01 12:00:00",
            CoverUrl: "https://pic.example/new.jpg"));

        Assert.True(meta);
        Assert.True(cover);

        // 该动的动了
        var data = _db.GetPlaybackData("BVmref000001")!;
        Assert.Equal("鬼畜>调教", data.Zone);
        Assert.Equal("B站新简介", data.Description);
        Assert.Equal("1.2万", data.ViewCountText);
        Assert.Equal("B站新UP", data.UpName);
        Assert.Equal(456, data.UpMid);
        Assert.Equal("2026-06-01 12:00:00", data.PublishText);
        // 封面链跟着新链走——问的是台账那一栏（`PlaybackData.CoverUrl` 下发的是**有效值**，
        // 这条稿有本地件，拿它比热链必然对不上，那是读侧本就该如斯，不是档案没写进去）。
        Assert.Equal("https://pic.example/new.jpg", _db.GetCoverUrl(id));
        Assert.StartsWith("/media/", data.CoverUrl); // 本地件还在，放映厅照旧优先本地

        // 不该动的一件没掉
        Assert.Equal("/avatars/up-1.jpg", data.UpFace);
        Assert.Equal("站长测试签名", data.UpSign);
        Assert.Equal(12_345, data.UpFans);
        Assert.Equal(11, data.StatLike);
        Assert.Equal(22, data.StatCoin);
        Assert.Equal(33, data.StatFavorite);
        Assert.Equal(44, data.StatShare);
        Assert.Equal(55, data.StatReply);
        Assert.Single(data.Staff!); // staff_json 还在（被抹成 NULL 就整段没了）
        Assert.Contains("剪辑", 直读文本("SELECT staff_json FROM video_meta WHERE video_id = $id;", id)!);
        Assert.Equal("B站原标题", data.Title); // 标题不重取
    }

    [Fact]
    public void 档案重取不碰站内覆写三栏_也不把标题跟着B站跑()
    {
        var id = SeedArrived("BVmref000002");
        Assert.True(_db.SetVideoTitleOverride(id, "站长自己起的标题"));
        Assert.True(_db.SetVideoDescOverride(id, "站长自己写的简介"));
        Assert.True(_db.SetVideoZoneOverride(id, "鬼畜>调教"));

        var (meta, _) = _db.UpdateArchiveFromView(id, new ArchiveRefreshPatch(
            Zone: "音乐>翻唱", Description: "B站新简介", UpName: "B站新UP", UpMid: 456));
        Assert.True(meta);

        var data = _db.GetPlaybackData("BVmref000002")!;
        // 面板念的还是站内那三份（覆写中），三枚标也都还在
        Assert.Equal("站长自己起的标题", data.Title);
        Assert.Equal("站长自己写的简介", data.Description);
        Assert.Equal("鬼畜>调教", data.Zone);
        Assert.True(data.TitleOverridden && data.DescOverridden && data.ZoneOverridden);

        // 底层三栏一字未动（光看有效值分不清「覆写还在」与「覆写被涂了」）
        Assert.Equal("站长自己起的标题", 直读文本("SELECT site_title FROM videos WHERE id = $id;", id));
        Assert.Equal("站长自己写的简介", 直读文本("SELECT site_desc FROM videos WHERE id = $id;", id));
        Assert.Equal("鬼畜>调教", 直读文本("SELECT site_zone FROM videos WHERE id = $id;", id));

        // 而 B 站那份（video_meta）确实被窄口刷新了：覆写一旦还原，念的就是它
        Assert.Equal("音乐>翻唱", 直读文本("SELECT zone FROM video_meta WHERE video_id = $id;", id));
        Assert.Equal("B站新简介", 直读文本("SELECT description FROM video_meta WHERE video_id = $id;", id));
        Assert.Equal("B站原标题", 直读文本("SELECT title FROM videos WHERE id = $id;", id));
    }

    [Fact]
    public void 档案重取_空串空白与零值都当没给_不涂旧账也不空转UPDATE()
    {
        var id = SeedArrived("BVmref000003");

        var (meta, cover) = _db.UpdateArchiveFromView(id, new ArchiveRefreshPatch(
            Zone: "   ",
            Description: "",
            ViewCountText: null,
            UpName: null,
            UpMid: 0, // B 站 owner.mid 缺档时给 0：0 不是 mid
            PublishText: null,
            CoverUrl: "  "));

        Assert.False(meta);  // 六栏全「没给」：连一趟空 UPDATE 都不该发
        Assert.False(cover);
        Assert.Equal("音乐>翻唱", 直读文本("SELECT zone FROM video_meta WHERE video_id = $id;", id));
        Assert.Equal("B站原简介", 直读文本("SELECT description FROM video_meta WHERE video_id = $id;", id));
        Assert.Equal("1万", 直读文本("SELECT view_count FROM video_meta WHERE video_id = $id;", id));
        Assert.Equal("https://pic.example/old.jpg", 直读文本("SELECT cover_url FROM videos WHERE id = $id;", id));
    }

    // ─── ③ 单条缺件判定与「有缺才回炉」 ──────────────────────────────────

    [Fact]
    public async Task 缺件判定_有产物行盘上没有才算缺_且只在真缺时回炉()
    {
        var healthy = SeedArrived("BVmref000010");
        var lost = SeedArrived("BVmref000011", out var lostBase);
        File.Delete(lostBase + ".mp4"); // 有产物行、盘上没件：这才是缺件

        Assert.False(MissingMediaReconciler.IsMediaMissing(_db, _paths, healthy, "BVmref000010"));
        Assert.True(MissingMediaReconciler.IsMediaMissing(_db, _paths, lost, "BVmref000011"));

        var queue = new DownloadQueue();
        Assert.Equal(
            MediaRefreshService.MediaIntact,
            await MediaRefreshService.RequeueIfMissingAsync(_db, _paths, queue, healthy, "BVmref000010", null, CancellationToken.None));
        Assert.Equal(
            MediaRefreshService.MediaQueued,
            await MediaRefreshService.RequeueIfMissingAsync(_db, _paths, queue, lost, "BVmref000011", null, CancellationToken.None));

        // 健康那条一个字节没动、一个下载没触发；缺件那条才回炉
        Assert.Equal(VideoStatus.Arrived, Assert.Single(_db.ListVideos(), v => v.Id == healthy).Status);
        Assert.Equal(VideoStatus.Pending, Assert.Single(_db.ListVideos(), v => v.Id == lost).Status);
        Assert.Equal(new[] { lost }, await 抽队列(queue));
    }

    [Fact]
    public async Task 预览行不算缺件_没生成预览也照样不用回炉()
    {
        var id = SeedArrived("BVmref000012");
        // 预览成片区压根没这个文件：预览行在 previews 表、不在 artifacts，缺件回炉不许拿它验盘
        _db.RegisterPreviewArrived(id, 1, "BVmref000012.mp4", 640, 360, 12.5, 2048);

        Assert.False(MissingMediaReconciler.IsMediaMissing(_db, _paths, id, "BVmref000012"));

        var queue = new DownloadQueue();
        Assert.Equal(
            MediaRefreshService.MediaIntact,
            await MediaRefreshService.RequeueIfMissingAsync(_db, _paths, queue, id, "BVmref000012", null, CancellationToken.None));
        Assert.Equal(VideoStatus.Arrived, Assert.Single(_db.ListVideos(), v => v.Id == id).Status);
        Assert.Empty(await 抽队列(queue));
    }

    [Fact]
    public void 封面件同rel二次登记只一行_原地覆盖不产生第二行()
    {
        // 故意造一条没归档封面的馆藏：补登两次也只该有一行 kind='cover'
        var id = SeedArrived("BVmref000013", withCover: false);
        Assert.DoesNotContain(_db.GetArtifactRelPaths(id), rel => rel.EndsWith(".Cover.jpg", StringComparison.OrdinalIgnoreCase));

        _db.RegisterCoverArtifact(id, "BVmref000013/1-BVmref000013-1080P-AVC.Cover.jpg");
        _db.RegisterCoverArtifact(id, "BVmref000013/1-BVmref000013-1080P-AVC.Cover.jpg");

        Assert.Equal(1, _db.GetArtifactRelPaths(id).Count(rel => rel.EndsWith(".Cover.jpg", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    // 封面名从产物行 rel 推：目录不动、去扩展与 .Cover 中缀再挂 .Cover.jpg（与 downkyi 同族，补员与重取共用这一张嘴）
    [InlineData("dir/1-正片-1080P-AVC.mp4", "dir/1-正片-1080P-AVC.Cover.jpg")]
    [InlineData("dir/1-正片-1080P-AVC.Cover.jpg", "dir/1-正片-1080P-AVC.Cover.jpg")] // 已是封面名不再叠加
    [InlineData(@"dir\1-正片.mp4", "dir/1-正片.Cover.jpg")]                          // 反斜杠体也认
    [InlineData("裸名.mkv", "裸名.Cover.jpg")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void 封面名推导与补员同一条规矩(string? baseRel, string? expected) =>
        Assert.Equal(expected, CoverFileFetcher.DeriveCoverRel(baseRel));

    [Theory]
    [InlineData("//i0.hdslb.com/c.jpg", "https://i0.hdslb.com/c.jpg")] // 协议相对链补全：HttpClient 不吃裸 //
    [InlineData("  https://i0.hdslb.com/c.jpg ", "https://i0.hdslb.com/c.jpg")]
    [InlineData("http://i0.hdslb.com/c.jpg", "http://i0.hdslb.com/c.jpg")]
    [InlineData("ftp://i0.hdslb.com/c.jpg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 封面链只认http族(string? raw, string? expected) =>
        Assert.Equal(expected, CoverFileFetcher.NormalizeUrl(raw));

    // ─── ⑤ 两张门的归属 ──────────────────────────────────────────────────

    [Theory]
    // 重拉封面与媒体：吃会员门（匿名一律 401），不吃站长门（投稿人本人得进得来，归属归端点 CanManageVideo）
    [InlineData("POST", "/api/videos/123/media-refresh", false, true)]
    [InlineData("POST", "/api/videos/123/media-refresh/", false, true)] // 门不能被一枚尾斜杠穿
    [InlineData("POST", "/api/videos/123/danmaku-refresh", false, true)]
    [InlineData("POST", "/api/videos/123/meta-refresh", false, true)]   // 重拉视频数据同族：吃会员门，归属归端点 CanManageVideo
    [InlineData("POST", "/api/videos/123/meta-refresh/", false, true)]  // 尾斜杠同样穿不了这扇门
    [InlineData("GET", "/api/videos/123/meta-refresh", false, false)]   // 也只点 POST
    [InlineData("POST", "/api/videos/123/media-refreshX", false, false)] // 只点这个名字，不顺手拓形状
    [InlineData("GET", "/api/videos/123/media-refresh", false, false)]   // 只有 POST 被点名
    [InlineData("POST", "/api/videos/123/codecs", true, false)]          // 加档仍归站长门（别被这次改动带下水）
    [InlineData("POST", "/api/videos/123", false, false)]
    public void 重拉封面与媒体的两张门归属(string method, string path, bool expectAdmin, bool expectMember)
    {
        var request = Request(method, path);
        Assert.Equal(expectAdmin, _gate.IsAdminRequest(request));
        Assert.Equal(expectMember, MemberGate.RequiresMember(request));
    }

    [Fact]
    public void 重拉封面与媒体的钥匙_只有投稿人本人与站长握得住()
    {
        var mine = Seat("投稿的人");
        var others = Seat("路过的人");
        var mineVideo = SeedArrived("BVmref000020");
        var othersVideo = SeedArrived("BVmref000021");
        var orphan = SeedArrived("BVmref000022"); // 无归属：只有站长握得住
        _db.SetVideoSubmitterMember(mineVideo, mine);
        _db.SetVideoSubmitterMember(othersVideo, others);

        var owner = Member(Seat("站长"), MemberRoles.Owner);
        var submitter = Member(mine, MemberRoles.Member);
        var passer = Member(others, MemberRoles.Member);

        Assert.True(ManageGate.CanManage(_db, mineVideo, owner));
        Assert.True(ManageGate.CanManage(_db, othersVideo, owner));
        Assert.True(ManageGate.CanManage(_db, orphan, owner));
        Assert.True(ManageGate.CanManage(_db, 424242, owner)); // 「查无此片」归端点那趟回查接住

        Assert.True(ManageGate.CanManage(_db, mineVideo, submitter));
        Assert.False(ManageGate.CanManage(_db, othersVideo, submitter));
        Assert.False(ManageGate.CanManage(_db, orphan, submitter));

        Assert.False(ManageGate.CanManage(_db, mineVideo, passer));
        Assert.False(ManageGate.CanManage(_db, mineVideo, null));
        Assert.False(ManageGate.CanManage(_db, othersVideo, null));
    }

    // ─── ⑥ /media 封面图的缓存口径 ───────────────────────────────────────

    [Fact]
    public void 封面图贴no_cache与ETag_视频与音频一律不掺和()
    {
        var stamped = Prepare("/media/BVmref000030/BVmref000030.Cover.jpg");
        Assert.Equal("no-cache", stamped.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(stamped.Response.Headers.ETag.ToString()));

        // 正片与音轨：那边有分轨 range 请求，加 revalidation 会搅坏播放 → 一个头都不许贴
        foreach (var path in new[]
                 {
                     "/media/BVmref000030/BVmref000030.mp4",
                     "/media/BVmref000030/BVmref000030-30280.m4a",
                     "/media/BVmref000030/BVmref000030.ass",
                 })
        {
            var untouched = Prepare(path);
            Assert.True(string.IsNullOrEmpty(untouched.Response.Headers.CacheControl.ToString()), $"{path} 不该被贴 Cache-Control");
            Assert.True(string.IsNullOrEmpty(untouched.Response.Headers.ETag.ToString()), $"{path} 不该被贴 ETag");
        }
    }

    [Fact]
    public void 封面图带上If_None_Match才304_对不上与视频都照常放行()
    {
        var dir = Path.Combine(_videosDir, "hit");
        Directory.CreateDirectory(dir);
        var rel = "hit/x.Cover.jpg";
        File.WriteAllBytes(Path.Combine(_videosDir, "hit", "x.Cover.jpg"), new byte[] { 1, 2, 3 });
        // 桩件拿的是固定 mtime（PrepareResponse 那一头），盘上这份也得贴同一个时戳，两边的 ETag 才算得一样。
        File.SetLastWriteTimeUtc(Path.Combine(_videosDir, "hit", "x.Cover.jpg"),
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero).UtcDateTime);

        // 先由 OnPrepareResponse 那一头算出这枚 ETag（两处必须同源，否则永远 304 不上）
        var stamped = Prepare("/media/" + rel);
        var etag = stamped.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        var fresh = new DefaultHttpContext();
        fresh.Request.Method = HttpMethods.Get;
        fresh.Request.Path = "/media/" + rel;
        Assert.False(MediaCachePolicy.TryHandleNotModified(fresh, _paths)); // 没带 If-None-Match：该把整张图端出去

        var hit = new DefaultHttpContext();
        hit.Request.Method = HttpMethods.Get;
        hit.Request.Path = "/media/" + rel;
        hit.Request.Headers.IfNoneMatch = etag;
        Assert.True(MediaCachePolicy.TryHandleNotModified(hit, _paths));
        Assert.Equal(StatusCodes.Status304NotModified, hit.Response.StatusCode);
        Assert.Equal(etag, hit.Response.Headers.ETag.ToString());

        var stale = new DefaultHttpContext();
        stale.Request.Method = HttpMethods.Get;
        stale.Request.Path = "/media/" + rel;
        stale.Request.Headers.IfNoneMatch = "\"stale-etag\"";
        Assert.False(MediaCachePolicy.TryHandleNotModified(stale, _paths));

        var movie = new DefaultHttpContext();
        movie.Request.Method = HttpMethods.Get;
        movie.Request.Path = "/media/hit/x.mp4";
        movie.Request.Headers.IfNoneMatch = etag;
        Assert.False(MediaCachePolicy.TryHandleNotModified(movie, _paths)); // 视频不掺和 revalidation

        var ghost = new DefaultHttpContext();
        ghost.Request.Method = HttpMethods.Get;
        ghost.Request.Path = "/media/hit/没这东西.jpg";
        ghost.Request.Headers.IfNoneMatch = "*";
        Assert.False(MediaCachePolicy.TryHandleNotModified(ghost, _paths)); // 件不在：交给静态区回它的 404
    }

    /// <summary>拿一枚桩件走一遍 OnPrepareResponse 那一头（桩件的 mtime/长度与盘上实物对上，ETag 才可比）。</summary>
    private static DefaultHttpContext Prepare(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        MediaCachePolicy.PrepareResponse(context, new StubFile(Path.GetFileName(path), 3, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)));
        return context;
    }

    private sealed class StubFile(string name, long length, DateTimeOffset lastModified) : IFileInfo
    {
        public string Name { get; } = name;

        public long Length { get; } = length;

        public DateTimeOffset LastModified { get; } = lastModified;

        public bool IsDirectory => false;

        // IFileInfo 还要求这两件（本测试只递 mtime 与长度给 ETag 算式，盘上路径与存在性用不上）。
        public string? PhysicalPath { get; } = null;

        public bool Exists => true;

        public Stream CreateReadStream() => Stream.Null;
    }
}
