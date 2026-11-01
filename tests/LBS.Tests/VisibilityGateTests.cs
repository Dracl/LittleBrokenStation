using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 可见性的端点侧那两把钥匙与静态区挡门的反查：数据层负责按尺子滤行，这里盯的是「尺子递对没有、门外给的脸色对不对」。
/// ① <see cref="ViewerGate.Scope"/>：访客 PublicOnly / 席位 Of(自己) / 站长 Everything（递错就是 fail-closed，连本人都看不见）；
/// ② <see cref="ViewerGate.CanView"/>：公开人人能看、私有只认投稿人本人与站长、查无此片一律 false —— 端点据此回「查无」那张 404 脸，不许 403；
/// ③ 静态闸的反查 <c>GetVideoIdByMediaRel</c>：artifacts / 加档件 / 预览件 / BV 号兜底都认得出，台账不认的件回 null（调用方必须放行）；
/// ④ 反查缓存的时间约束：认到 60 秒（稿件转私后最多 60 秒内必须挡住）、认不出只 5 秒、满仓整体清；
/// ⑤ 抽屉设置的档位校验、「看不见的片子安利不了」那道缝，以及加档那本全站账不认尺子时
///    读侧那道 <c>CanViewVideo</c> 是唯一依凭（codecs/progress 一族的泄漏面）。
/// 站内无 HTTP harness，身份照 CollectionsTests 那套直接 Stamp 进 DefaultHttpContext。
/// </summary>
public sealed class VisibilityGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public VisibilityGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-visibility-gate-" + Guid.NewGuid().ToString("N"));
        var videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(videosDir);
        _paths = new DataPaths(_tempDir, videosDir, _tempDir, _tempDir, _tempDir);
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

    /// <summary>造一条到站的货：可见性与投稿人当场定（写侧只有 AddVideosBulk 这一支能带私有进来）。</summary>
    private long Arrive(
        string bvid,
        string? title = null,
        long? submitter = null,
        VideoVisibility visibility = VideoVisibility.Public)
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title ?? bvid, null, null, visibility),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, "音乐>翻唱", "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title ?? bvid, null);
        _db.MarkArrived(id);
        if (submitter is not null)
        {
            _db.SetVideoSubmitterMember(id, submitter.Value);
        }

        return id;
    }

    /// <summary>造一个在席席位（比的是站内席位 id，与端点那把 CanManageVideo 同一张嘴）。</summary>
    private long Seat(string name)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        Assert.NotNull(_db.BindMember(id, 950_000 + id, name, null));
        return id;
    }

    /// <summary>拿席位 id 拼个身份（端点判归属与看客钥匙只吃 Id 与 Role，其余字段无关）。</summary>
    private static StationMember Member(long id, string role) =>
        new(id, 1000 + id, "席位" + id, null, null, MemberStatus.Active, role, DateTimeOffset.UnixEpoch.ToString("O"), null, null);

    /// <summary>把这枚身份盖进请求（MemberGate.Stamp 就是端点拿人的那个口）；memberId 给 null 就是访客。</summary>
    private HttpContext AsThis(long? memberId, string role = MemberRoles.Member)
    {
        var http = new DefaultHttpContext();
        if (memberId is { } id)
        {
            new MemberGate(_db).Stamp(http, Member(id, role));
        }

        return http;
    }

    [Fact]
    public void 看客尺子三档_访客席位与站长各一把()
    {
        var me = Seat("藏货的人");

        // 访客：PublicOnly —— Of(null) 与它同一枚，没席位就是访客
        Assert.Equal(VisibilityScope.PublicOnly, ViewerGate.Scope(AsThis(null)));
        Assert.False(ViewerGate.Scope(AsThis(null)).All);

        // 席位：公开 + 自己的（比的是站内席位 id）
        var mine = ViewerGate.Scope(AsThis(me));
        Assert.False(mine.All);
        Assert.Equal(me, mine.MemberId);

        // 站长：什么都不滤
        var owner = ViewerGate.Scope(AsThis(Seat("站长"), MemberRoles.Owner));
        Assert.True(owner.All);
        Assert.Null(owner.MemberId);
    }

    [Fact]
    public void 能不能看这把钥匙_公开人人_私有只认本人与站长_查无回false()
    {
        var me = Seat("藏货的人");
        var other = Seat("别人");
        var mine = Arrive("BVgate000001", "我藏的一首", submitter: me, visibility: VideoVisibility.Private);
        var theirs = Arrive("BVgate000002", "别人藏的一首", submitter: other, visibility: VideoVisibility.Private);
        var orphan = Arrive("BVgate000003", "没主人的私货", visibility: VideoVisibility.Private);
        var open = Arrive("BVgate000004", "人人听得的一首");

        // 访客：公开的能看；私有的与查无此片同一路（端点拿 false 一律回 404，不给「有这稿但不给你看」的信号）
        Assert.True(ViewerGate.CanView(AsThis(null), _db, open));
        Assert.False(ViewerGate.CanView(AsThis(null), _db, mine));
        Assert.False(ViewerGate.CanView(AsThis(null), _db, 424242));

        // 席位：自己的私有行放行；别人的、无主的私有行与查无一律 false
        Assert.True(ViewerGate.CanView(AsThis(me), _db, mine));
        Assert.True(ViewerGate.CanView(AsThis(me), _db, open));
        Assert.False(ViewerGate.CanView(AsThis(me), _db, theirs));
        Assert.False(ViewerGate.CanView(AsThis(me), _db, orphan));
        Assert.False(ViewerGate.CanView(AsThis(me), _db, 424242));
        Assert.False(ViewerGate.CanView(AsThis(other), _db, mine));

        // 站长：短路全见（连无主的私货也握得住）。「查无回 false」那一句管的是没钥匙的人：
        // 站长短路在查档之前，与 CanManageVideo 一字不差（那把钥匙也不问有没有这一条），
        // 伪 id 到站长手里会被各端点自己那趟回查接住（download 回「没存下」、share 回「没这个片子」）。
        var ownerSeat = Seat("站长");
        Assert.True(ViewerGate.CanView(AsThis(ownerSeat, MemberRoles.Owner), _db, mine));
        Assert.True(ViewerGate.CanView(AsThis(ownerSeat, MemberRoles.Owner), _db, theirs));
        Assert.True(ViewerGate.CanView(AsThis(ownerSeat, MemberRoles.Owner), _db, orphan));
        Assert.True(ViewerGate.CanView(AsThis(ownerSeat, MemberRoles.Owner), _db, 424242));
    }

    [Fact]
    public void 静态闸反查稿件id_产物加档预览与BV号都认_台账不认回null()
    {
        var hidden = Arrive("BVgate000010", "藏起来的一首", visibility: VideoVisibility.Private);

        // ① artifacts 精确命中（静态闸就从这条拿 id 去问 CanViewVideo）
        _db.RegisterCoverArtifact(hidden, "藏/封面.jpg");
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("藏/封面.jpg"));

        // 路径形不规整也认：反斜杠、头斜杠与 ./ 前缀（闸里那条归一链的下游）
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("藏\\封面.jpg"));
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("/./藏/封面.jpg"));

        // 中文件名要先把百分号那形解回来才匹配得上台账：不解码就是白挡（闸里多做那一手）
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel(Uri.UnescapeDataString("%E8%97%8F/%E5%B0%81%E9%9D%A2.jpg")));

        // ② 悬停预览件（rel 相对 data/previews，同一字符串传进来）
        _db.RegisterPreviewArrived(hidden, 1, "BVgate000010.mp4", 320, 180, 6.5, 20480);
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("BVgate000010.mp4"));

        // ③ 加档件
        Assert.True(_db.AddCodecVersion(hidden, 1, "hevc"));
        var codecRow = Assert.Single(_db.GetCodecVersions(hidden), r => r.Codec == "hevc");
        _db.MarkCodecArrived(codecRow.Id, "藏/1-正片-1080P-HEVC.mp4");
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("藏/1-正片-1080P-HEVC.mp4"));

        // ④ 台账没行：退到文件名尾部那枚 BV 号反查（引擎落盘名常带 bvid）
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("杂项/随手拍-BVgate000010.mp4"));
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("杂项/随手拍-BVgate000010-1080P-AVC.mp4"));

        // 台账彻底不认这个件（头像/表情那类根本不在这些表里）与空串：一律 null —— 调用方必须放行，不许当私有
        Assert.Null(_db.GetVideoIdByMediaRel("avatars/950001.jpg"));
        Assert.Null(_db.GetVideoIdByMediaRel("随便/没这个件.mp4"));
        Assert.Null(_db.GetVideoIdByMediaRel("   "));
        Assert.Null(_db.GetVideoIdByMediaRel(""));

        // 那串不像 BV 号（差一位都不算）：不反查，免得把不相干的稿子按私有挡了
        Assert.Null(_db.GetVideoIdByMediaRel("杂项/随手拍-BVgate00001.mp4"));

        // 与查可见性那口共用同一认法（同一张嘴）：同一条腿两边答案一致
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/封面.jpg"));
    }

    [Fact]
    public void 反查缓存_认到留六十秒_认不出只留五秒_满仓整体清()
    {
        var clock = new ManualClock();
        var cache = new MediaOwnerCache(clock);
        var calls = 0;

        long? ReadThrough(string rel)
        {
            calls++;
            return rel.Contains("私", StringComparison.Ordinal) ? 7 : null;
        }

        // 认到稿件的件：60 秒内不再问库（首页一屏上百张封面，逐条问库才是问题）
        Assert.Equal(7L, cache.Resolve("私/封面.jpg", ReadThrough));
        cache.Resolve("私/封面.jpg", ReadThrough);
        Assert.Equal(1, calls);

        clock.Now += TimeSpan.FromSeconds(59);
        cache.Resolve("私/封面.jpg", ReadThrough);
        // 没到 60 秒还该吃缓存
        Assert.Equal(1, calls);

        // 整 60 秒必须回库：这就是「稿件转私后最多 60 秒内挡住」那条硬约束的下限
        clock.Now += TimeSpan.FromSeconds(1);
        cache.Resolve("私/封面.jpg", ReadThrough);
        // 到 60 秒就得重新问库，转私的答复不许再多留一秒
        Assert.Equal(2, calls);

        // 认不出的件只留 5 秒：新归档的件不能长时间卡在「台账不认」那侧（那是放行方向）
        Assert.Null(cache.Resolve("没这件.mp4", ReadThrough));
        Assert.Equal(3, calls);
        clock.Now += TimeSpan.FromSeconds(4);
        Assert.Null(cache.Resolve("没这件.mp4", ReadThrough));
        // 5 秒内还该吃缓存
        Assert.Equal(3, calls);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(cache.Resolve("没这件.mp4", ReadThrough));
        // 过 5 秒必须回库，新入台账的件得尽快认出来
        Assert.Equal(4, calls);

        // 满仓整体清：代价只是多问一趟库，不许悄悄养着一层永不回库的旧答复
        for (var i = 0; i < 4096; i++)
        {
            cache.Resolve($"件{i}.mp4", _ => null);
        }

        cache.Resolve("没这件.mp4", ReadThrough); // 这条过期 → 回库，顺手把整层掀了
        var afterSweep = calls;
        cache.Resolve("件0.mp4", ReadThrough); // 早先填的那批已被整体清掉 → 该回库重问
        // 整层清掉后先前那条答复该回库重问
        Assert.Equal(afterSweep + 1, calls);
    }

    [Fact]
    public void 抽屉设置的档位校验_认那三样_其余给一句人话()
    {
        Assert.True(CollectionsEndpoints.TryNormalizeVisibility(null, out var empty, out _));
        Assert.Equal(VideoVisibility.Public, empty); // 不填 = 公开（与投稿那四档同一口径）

        Assert.True(CollectionsEndpoints.TryNormalizeVisibility("   ", out var blank, out _));
        Assert.Equal(VideoVisibility.Public, blank);

        Assert.True(CollectionsEndpoints.TryNormalizeVisibility(" Private ", out var spaced, out _));
        Assert.Equal(VideoVisibility.Private, spaced);

        Assert.False(CollectionsEndpoints.TryNormalizeVisibility("secret", out _, out var message));
        Assert.Contains(VisibilityCodec.Private, message, StringComparison.Ordinal); // 拒词把人话说明白：只认那两样
    }

    [Fact]
    public void 藏着的稿子安利不出去_公开的那部照旧发得出去()
    {
        var me = Seat("安利的人");
        var peer = Seat("收信的人");
        Arrive("BVgate000020", "藏的一首", submitter: Seat("别人"), visibility: VideoVisibility.Private);
        Arrive("BVgate000021", "人人听得的一首");

        // 没过看客钥匙：404 一句人话，且拒词里不许出现「私有」（说了就是替人验那稿存在）
        var (error, _, _, _) = DmEndpoints.ValidateVideoShare(_db, me, peer, "BVgate000020", null, viewerCanView: false);
        Assert.Equal(StatusCodes.Status404NotFound, error!.Value.Status);
        Assert.DoesNotContain("私有", error.Value.Message, StringComparison.Ordinal);

        // 同一部片子，钥匙过了（东家本人 / 站长）就照旧发得出去：卡面也照样定格
        var (noError, target, _, dynJson) = DmEndpoints.ValidateVideoShare(_db, me, peer, "BVgate000020", null, viewerCanView: true);
        Assert.Null(noError);
        Assert.Equal(peer, target);
        Assert.NotNull(dynJson);

        // 查无此片那句话得留着：钥匙 false 也不许抢它的台词（「在不在馆」那道判据在前）
        var (gone, _, _, _) = DmEndpoints.ValidateVideoShare(_db, me, peer, "BVgate000099", null, viewerCanView: false);
        Assert.Equal(StatusCodes.Status404NotFound, gone!.Value.Status);
        Assert.Contains("没有这个片子", gone.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 对外形状_稿件可见性是小写字符串不是0与1()
    {
        // 卡阵/搜索直发 VideoEntry，Visibility 是枚举：不加这层转换器就变成 0/1，前端拿不到 "private"。
        // 注册点在 Program.cs 的 ConfigureHttpJsonOptions（全站一份），这里拿同一份形状验它对外的字面量。
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new VideoVisibilityJsonConverter());

        var hidden = new VideoEntry(1, "https://www.bilibili.com/video/BVgate000030", "BVgate000030", null,
            "藏的一首", VideoStatus.Arrived, DateTimeOffset.UnixEpoch, Visibility: VideoVisibility.Private);
        Assert.Contains("\"visibility\":\"private\"", JsonSerializer.Serialize(hidden, options), StringComparison.Ordinal);

        var open = hidden with { Visibility = VideoVisibility.Public };
        Assert.Contains("\"visibility\":\"public\"", JsonSerializer.Serialize(open, options), StringComparison.Ordinal);

        // 单拿枚举本身验：小写字面量，不露 0/1
        Assert.Equal("\"private\"", JsonSerializer.Serialize(VideoVisibility.Private, options), ignoreCase: false);
        Assert.Equal("\"public\"", JsonSerializer.Serialize(VideoVisibility.Public, options), ignoreCase: false);

        // 读侧也不拿枚举的数字化旧形当路人：认字符串、也认存量那两枚数字（不把一列脏值顶成 500）
        Assert.Equal(VideoVisibility.Private, JsonSerializer.Deserialize<VideoVisibility>("\"private\"", options));
        Assert.Equal(VideoVisibility.Private, JsonSerializer.Deserialize<VideoVisibility>("1", options));
        Assert.Equal(VideoVisibility.Public, JsonSerializer.Deserialize<VideoVisibility>("\"shy\"", options));
    }

    [Fact]
    public void 加档那本账不认尺子_所以读侧的看客钥匙是唯一的门()
    {
        // /api/videos/{id}/codecs/progress 拿的是 GetDownloadingCodecVersions 这本全站账，它没有尺子可递：
        // 私有稿的加档行一行不缺。也就是说那道口上挂的 CanViewVideo 是「码率清单不漏给外人」的唯一依凭，
        // 这里钉住两件事：① 全站账确实不滤私有（所以那道门拆不得）；② 钥匙与静态闸拿的是同一枚 id。
        var me = Seat("藏货的人");
        var other = Seat("第三者");
        var hidden = Arrive("BVgate000040", "藏的一首", submitter: me, visibility: VideoVisibility.Private);
        Assert.True(_db.AddCodecVersion(hidden, 1, "hevc"));

        var stationRows = _db.GetDownloadingCodecVersions();
        Assert.Contains(stationRows, r => r.VideoId == hidden);

        // 钥匙本身：别人的私有稿这本账念不出去，本人过（与 danmaku/download 同一把）
        Assert.False(ViewerGate.CanView(AsThis(other), _db, hidden));
        Assert.False(ViewerGate.CanView(AsThis(null), _db, hidden));
        Assert.True(ViewerGate.CanView(AsThis(me), _db, hidden));

        // 定档后端点不念它了，但静态闸还得认得这个件：同一枚 id 从两边都递得出来（同一张嘴）
        var row = Assert.Single(_db.GetCodecVersions(hidden), r => r.Codec == "hevc");
        _db.MarkCodecArrived(row.Id, "藏/1-正片-1080P-HEVC.mp4");
        Assert.Equal(hidden, _db.GetVideoIdByMediaRel("藏/1-正片-1080P-HEVC.mp4"));
    }

    /// <summary>手动时钟（照 ApiThrottleTests 那套，只到 GetUtcNow 这一层）：缓存的 TTL 不许靠真睡觉来验。</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
