using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 稿件设置的后端半边：标题 / 简介 / 分区三栏的本地覆写与还原，外加补上的 SetVideoVisibility。
/// 盯的是两件事：
/// ① 覆写与还原的往返——<b>NULL、空串、纯空白都算还原</b>（数据层同一克制），认不了的值一律写不进去；
/// ② 有效值只有一张嘴——改了标题，卡阵、搜索、分区页、相关推荐、放映厅、合集片单、观看历史七处得同时换，
///    不许出现「只有放映厅变、列表还是旧的」那种两处真相；简介只有放映厅与搜索念它（卡阵不显），两处也同一个口径。
/// 外加 revive / requeue 那两口收进归属门后的钥匙判定（本仓没有 HTTP harness，照 SubmitterManageTests 那族钉门判据）。
/// </summary>
public sealed class VideoSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly AdminGate _gate = new(null!, null!);

    public VideoSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-video-settings-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>造一条到站的货：标题 / B 站热链封面 / 本地归档封面 / 分区 / 投稿人当场定。</summary>
    private long Arrive(
        string bvid,
        string? title = null,
        string? coverUrl = null,
        string? localCover = null,
        string zone = "音乐>翻唱",
        long? submitter = null)
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title ?? bvid, coverUrl),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, zone, "2026-05-20 18:00:00", "B站原简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title ?? bvid, coverUrl);
        _db.MarkArrived(id);
        if (localCover is not null)
        {
            _db.RegisterCoverArtifact(id, localCover);
        }

        if (submitter is not null)
        {
            _db.SetVideoSubmitterMember(id, submitter);
        }

        return id;
    }

    /// <summary>造一个在席席位（只拿席位 id，角色另说：判身份的场合一律用 <see cref="Member"/> 明着递）。</summary>
    private long Seat(string name)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        Assert.NotNull(_db.BindMember(id, 960_000 + id, name, null));
        return id;
    }

    /// <summary>拿席位 id 拼个身份（写侧那把钥匙只吃 Id 与 Role，其余字段无关；照 CollectionsTests 同一做法）。</summary>
    private static StationMember Member(long id, string role) =>
        new(id, 1000 + id, "席位" + id, null, null, MemberStatus.Active, role, DateTimeOffset.UnixEpoch.ToString("O"), null, null);

    /// <summary>绕开写口直接改台账（存量库 / 手搟数据那一类）：验读侧对脏值的克制。</summary>
    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static HttpRequest Request(string method, string path) => new DefaultHttpContext
    {
        Request = { Method = method, Path = path },
    }.Request;

    // ─── ① 覆写与还原的往返 ─────────────────────────────────────────────

    [Theory]
    // 覆写 = 写非空串；null / 空串 / 纯空白 = 还原（数据层 NULLIF 归一，库里不留「空串覆写」那种第三种状态）
    [InlineData("站内改的标题", "站内改的标题")]
    [InlineData("  去首尾空白  ", "去首尾空白")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void 标题覆写与还原的往返_空串与空白都算还原(string? input, string? expected)
    {
        var id = Arrive("BVset0000001", "B站原标题");

        Assert.True(_db.SetVideoTitleOverride(id, input));
        Assert.Equal(expected ?? "B站原标题", _db.GetWatchData("BVset0000001")!.Title);
        Assert.Equal(expected is not null, _db.GetWatchData("BVset0000001")!.TitleOverridden);

        // 再来一次：还原回 B 站那份，覆写标跟着落回假
        Assert.True(_db.SetVideoTitleOverride(id, "另一句站内标题"));
        Assert.Equal("另一句站内标题", _db.GetWatchData("BVset0000001")!.Title);
        Assert.True(_db.SetVideoTitleOverride(id, null));
        Assert.Equal("B站原标题", _db.GetWatchData("BVset0000001")!.Title);
        Assert.False(_db.GetWatchData("BVset0000001")!.TitleOverridden);
    }

    [Fact]
    public void 简介覆写与还原_放映厅念它_搜索也跟着有换嘴()
    {
        var id = Arrive("BVset0000002");

        Assert.True(_db.SetVideoDescOverride(id, "  站内自己写的简介  "));
        var watched = _db.GetWatchData("BVset0000002")!;
        Assert.Equal("站内自己写的简介", watched.Description); // 去首尾空白，段内空格留着
        Assert.True(watched.DescOverridden);

        // 搜索跟着有效值换嘴（父代理本轮定的口径）：放映厅念新句、搜索却只按 B 站原句命中，
        // 就是「改了却搜不到自己改的那句」那种两处真相，所以这里钉反向：原句不再命中、新句命中。
        // （卡阵本来就不念简介，不在此列。）
        Assert.Empty(_db.SearchVideos("B站原简介", null));
        Assert.Single(_db.SearchVideos("站内自己写的简介", null));

        // 还原：空串也算（与标题、分区同一克制）
        Assert.True(_db.SetVideoDescOverride(id, ""));
        Assert.Equal("B站原简介", _db.GetWatchData("BVset0000002")!.Description);
        Assert.False(_db.GetWatchData("BVset0000002")!.DescOverridden);
        Assert.Single(_db.SearchVideos("B站原简介", null));
    }

    [Fact]
    public void 分区改派在放映厅也照得住_还原后回B站户口()
    {
        var id = Arrive("BVset0000010", zone: "音乐>翻唱");
        Assert.Equal("音乐>翻唱", _db.GetWatchData("BVset0000010")!.Zone);
        Assert.False(_db.GetWatchData("BVset0000010")!.ZoneOverridden);

        // 改派：放映厅与分区页同一张嘴（EffZoneExpr 本来就是那一个出口，这里钉的是放映厅别再只吃 vm.zone）
        Assert.True(_db.SetVideoZoneOverride(id, "鬼畜>调教"));
        Assert.Equal("鬼畜>调教", _db.GetWatchData("BVset0000010")!.Zone);
        Assert.True(_db.GetWatchData("BVset0000010")!.ZoneOverridden);
        Assert.Equal("鬼畜>调教", Assert.Single(_db.ListZoneVideos("鬼畜"), v => v.Id == id).Zone);

        // 认不了的值：回 false 且一个字节都不动（投稿人本人也吃 TryNormalizeZone 这一张嘴）
        Assert.False(_db.SetVideoZoneOverride(id, "音乐>翻唱>再来一层"));
        Assert.False(_db.SetVideoZoneOverride(id, new string('区', StationDb.MaxZoneNameLength + 1)));
        Assert.False(_db.SetVideoZoneOverride(id, "   >  "));
        Assert.Equal("鬼畜>调教", _db.GetWatchData("BVset0000010")!.Zone);

        // 还原：空串 = 清空改派，户口回跟 B 站档案走
        Assert.True(_db.SetVideoZoneOverride(id, ""));
        Assert.Equal("音乐>翻唱", _db.GetWatchData("BVset0000010")!.Zone);
        Assert.False(_db.GetWatchData("BVset0000010")!.ZoneOverridden);
    }

    [Fact]
    public void 可见性写口补上了_往返一趟读侧尺子跟着变()
    {
        var me = Seat("投稿的人");
        var id = Arrive("BVset0000020", "公开的一首", submitter: me);
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibility(id));

        // 转私：访客七处都问不着，本人与站长照旧
        Assert.True(_db.SetVideoVisibility(id, VideoVisibility.Private));
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibility(id));
        Assert.DoesNotContain(_db.ListVideos(), v => v.Id == id);
        Assert.Contains(_db.ListVideos(VisibilityScope.Of(me)), v => v.Id == id);
        Assert.Contains(_db.ListVideos(VisibilityScope.Everything), v => v.Id == id);
        Assert.Null(_db.GetWatchData("BVset0000020"));
        Assert.Equal("private", _db.GetWatchData("BVset0000020", VisibilityScope.Of(me))!.Visibility);
        Assert.Empty(_db.SearchVideos("公开的一首", null));

        // 转回公开：同一张嘴回去，锁没了
        Assert.True(_db.SetVideoVisibility(id, VideoVisibility.Public));
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibility(id));
        Assert.Contains(_db.ListVideos(), v => v.Id == id);
        Assert.Equal("public", _db.GetWatchData("BVset0000020")!.Visibility);
        Assert.Single(_db.SearchVideos("公开的一首", null));
    }

    [Fact]
    public void 四个写口_查无此片一律回false()
    {
        Assert.False(_db.SetVideoTitleOverride(424242, "没人听的标题"));
        Assert.False(_db.SetVideoDescOverride(424242, "没人看的简介"));
        Assert.False(_db.SetVideoZoneOverride(424242, "音乐"));
        Assert.False(_db.SetVideoVisibility(424242, VideoVisibility.Private));
    }

    [Theory]
    // 标题 80 字 / 简介 2000 字封顶（依据写在 StationDb.VideoSettings.cs）：超一字就回 false + 人话
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void 标题限长(int length, bool ok) =>
        Assert.Equal(ok, StationDb.TryNormalizeTitle(new string('长', length), out _, out _));

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void 简介限长(int length, bool ok) =>
        Assert.Equal(ok, StationDb.TryNormalizeDesc(new string('长', length), out _, out _));

    [Fact]
    public void 校验那几张嘴_认不了回false_空值放行回还原()
    {
        // 分区（与站长室同一张嘴）：一层 '>'、非空、两侧有字
        Assert.True(StationDb.TryNormalizeZone(" 鬼畜 > 调教 ", out var zone, out _) && zone == "鬼畜>调教");
        Assert.True(StationDb.TryNormalizeZone(null, out var cleared, out _) && cleared is null);
        Assert.False(StationDb.TryNormalizeZone("音乐>翻唱>子区", out _, out _));
        Assert.False(StationDb.TryNormalizeZone(">", out _, out _));
        Assert.False(StationDb.TryNormalizeZone(new string('区', StationDb.MaxZoneNameLength + 1), out _, out _));

        // 标题 / 简介：空白 = 还原（值出 null 且放行），超长 false
        Assert.True(StationDb.TryNormalizeTitle("   ", out var titleCleared, out _) && titleCleared is null);
        Assert.True(StationDb.TryNormalizeDesc(null, out var descCleared, out _) && descCleared is null);
        Assert.False(StationDb.TryNormalizeTitle(new string('长', StationDb.MaxSiteTitleLength + 1), out _, out var titleMessage));
        Assert.Contains(StationDb.MaxSiteTitleLength.ToString(), titleMessage);
    }

    // ─── ② 有效值只有一张嘴：改了标题七处同时换 ───────────────────────────

    [Fact]
    public void 标题覆写后_卡阵_搜索_分区_相关推荐_放映厅_合集_历史同一条标题()
    {
        var me = Seat("看过的人");
        var id = Arrive("BVset0000100", "B站长标题", zone: "音乐>翻唱", submitter: me);
        var sibling = Arrive("BVset0000101", "同区的另一首", zone: "音乐>翻唱");
        var collection = _db.CreateCollection("集", null, me);
        Assert.True(_db.AddItem(collection, id));
        _db.UpsertWatchHistory(me, id, 1, 30, 100);

        Assert.True(_db.SetVideoTitleOverride(id, "站内改的标题"));

        // ① 卡阵
        Assert.Equal("站内改的标题", Assert.Single(_db.ListVideos(), v => v.Id == id).Title);
        // ② 搜索：新标题搜得着，原标题不再命中（拿原值搜等于「改了标题还搜不到自己改的那句」）
        Assert.Single(_db.SearchVideos("站内改的标题", null), v => v.Id == id);
        Assert.Empty(_db.SearchVideos("B站长标题", null));
        // ③ 分区页
        Assert.Equal("站内改的标题", Assert.Single(_db.ListZoneVideos("音乐"), v => v.Id == id).Title);
        // ④ 相关推荐
        Assert.Equal("站内改的标题", Assert.Single(_db.GetRelatedVideos(sibling, 50), v => v.Id == id).Title);
        // ⑤ 放映厅
        Assert.Equal("站内改的标题", _db.GetWatchData("BVset0000100")!.Title);
        // ⑥ 合集简报封面之外，片单那条也得上新标题
        Assert.Equal("站内改的标题", Assert.Single(_db.GetCollection(collection)!.Items, i => i.Id == id).Title);
        // ⑦ 观看历史
        Assert.Equal("站内改的标题", Assert.Single(_db.GetWatchHistory(me), r => r.VideoId == id).Title);

        // 还原后七处一起回旧口径（同一条 SQL 表达式，不会有一处漏改）
        Assert.True(_db.SetVideoTitleOverride(id, null));
        Assert.Equal("B站长标题", Assert.Single(_db.ListVideos(), v => v.Id == id).Title);
        Assert.Equal("B站长标题", _db.GetWatchData("BVset0000100")!.Title);
        Assert.Equal("B站长标题", Assert.Single(_db.GetWatchHistory(me), r => r.VideoId == id).Title);
    }

    [Fact]
    public void 分区改派与简介覆写后_搜索也吃有效值_旧户口与原句不再命中()
    {
        var id = Arrive("BVset0000300", "一首旧歌", zone: "音乐>翻唱");

        // 分区：搜索拿 vm.zone 匹配而分区页拿 EffZoneExpr 摆行，就是「摆在鬼畜那一堆里、按鬼畜却搜不着」那种两处真相。
        Assert.Single(_db.SearchVideos("音乐", null), v => v.Id == id);
        Assert.True(_db.SetVideoZoneOverride(id, "鬼畜>调教"));
        Assert.Single(_db.SearchVideos("鬼畜", null), v => v.Id == id);
        Assert.Empty(_db.SearchVideos("音乐", null));

        // 简介：放映厅念的是覆写那句，还按 B 站原句搜就是骗人（新句搜不着、原句反倒命中）。
        Assert.Single(_db.SearchVideos("B站原简介", null), v => v.Id == id);
        Assert.True(_db.SetVideoDescOverride(id, "站内自己写的简介"));
        Assert.Single(_db.SearchVideos("站内自己写的简介", null), v => v.Id == id);
        Assert.Empty(_db.SearchVideos("B站原简介", null));

        // 两处都还原：搜索跟着回旧口径（与标题那七处同一张嘴，不靠单独补一个分支）
        Assert.True(_db.SetVideoZoneOverride(id, null));
        Assert.True(_db.SetVideoDescOverride(id, ""));
        Assert.Single(_db.SearchVideos("音乐", null), v => v.Id == id);
        Assert.Single(_db.SearchVideos("B站原简介", null), v => v.Id == id);
    }

    [Fact]
    public void 覆写标随载荷下发_数据层不送自管权()
    {
        var id = Arrive("BVset0000200", "原标题", coverUrl: "https://pic.example/hot.jpg");
        var fresh = _db.GetWatchData("BVset0000200")!;

        // 没覆写过的三栏都是假
        Assert.False(fresh.TitleOverridden);
        Assert.False(fresh.DescOverridden);
        Assert.False(fresh.ZoneOverridden);

        // 权限布尔归端点算（它才知道当前会话是谁），数据层给的必须是假，免得哪里漏赋值就白送
        Assert.False(fresh.CanManage);
        Assert.False(fresh.IsStationOwner);
        Assert.All(_db.ListVideos(), v => Assert.False(v.CanManage));

        Assert.True(_db.SetVideoTitleOverride(id, "新标题"));
        Assert.True(_db.SetVideoDescOverride(id, "新简介"));
        Assert.True(_db.SetVideoZoneOverride(id, "鬼畜"));
        var edited = _db.GetWatchData("BVset0000200")!;
        Assert.True(edited.TitleOverridden);
        Assert.True(edited.DescOverridden);
        Assert.True(edited.ZoneOverridden);
        // 面板上那三栏念的就是有效值（覆写中 = 站内那份）
        Assert.Equal("新标题", edited.Title);
        Assert.Equal("新简介", edited.Description);
        Assert.Equal("鬼畜", edited.Zone);
        // 简介只此一个名字（读写两头与放映厅正文共用 description）——不再开 desc 那张第二脸，
        // 「一个出处两个名字」早晚错开（前端面板已改读 data.description）。
    }

    // ─── ③ 老库补列那一遭：存量行没覆写、补完照样能覆写与还原 ────────────────

    [Fact]
    public void 老库补列_存量行没覆写_补完照样能覆写与还原()
    {
        var id = Arrive("BVset0000700", "B站原标题", coverUrl: "https://pic.example/hot.jpg", localCover: "covers/hot.jpg", zone: "音乐>翻唱");

        // 装作早先建的库根本没这两个槽，再走一遍开局补列（ALTER TABLE ADD COLUMN 那一条路）
        Execute("ALTER TABLE videos DROP COLUMN site_title;");
        Execute("ALTER TABLE videos DROP COLUMN site_desc;");
        _db.Initialize();

        // 存量行没覆写：三栏照吃 B 站归档那份（封面走本地件优先）
        var before = _db.GetWatchData("BVset0000700")!;
        Assert.Equal("B站原标题", before.Title);
        Assert.Equal("B站原简介", before.Description);
        Assert.Equal("音乐>翻唱", before.Zone);
        Assert.False(before.TitleOverridden || before.DescOverridden || before.ZoneOverridden);
        Assert.Equal("/media/covers/hot.jpg", Assert.Single(_db.ListVideos(), v => v.Id == id).LocalCover);

        // 补完的列写得了也还得回去
        Assert.True(_db.SetVideoTitleOverride(id, "站内新标题"));
        Assert.Equal("站内新标题", _db.GetWatchData("BVset0000700")!.Title);
        Assert.True(_db.SetVideoTitleOverride(id, null));
        Assert.Equal("B站原标题", _db.GetWatchData("BVset0000700")!.Title);
    }

    // ─── ④ revive / requeue 的钥匙判定（本仓无 HTTP harness，钉门判据本身）───

    [Theory]
    // 续下 / 重新排队 / 加档点单：整段在站长门里（AdminGate 点名 POST），会员门不管它们
    [InlineData("POST", "/api/videos/123/revive", true, false)]
    [InlineData("POST", "/api/videos/123/requeue", true, false)]
    [InlineData("POST", "/api/videos/123/codecs", true, false)]
    // 稿件设置这口 PATCH：吃会员门（与同形状的删稿 DELETE /api/videos/{id} 一字不差：匿名一律 401），
    // 不吃站长门（投稿人本人得进得来）；归属归端点里的 CanManageVideo（下面那条用例钉它）。
    // 尾斜杠与带尾巴的形状各钉一枚：门不能被一枚斜杠穿，也不能把不该封的口径顺带封了。
    [InlineData("PATCH", "/api/videos/123", false, true)]
    [InlineData("PATCH", "/api/videos/123/", false, true)]
    [InlineData("PATCH", "/api/videos/123/revive", false, false)]
    [InlineData("PATCH", "/api/videos", false, false)]
    public void 稿件写口的两张门归属(string method, string path, bool expectAdmin, bool expectMember)
    {
        var request = Request(method, path);
        Assert.Equal(expectAdmin, _gate.IsAdminRequest(request));
        Assert.Equal(expectMember, MemberGate.RequiresMember(request));
    }

    [Fact]
    public void 续下与重排的钥匙_只有投稿人本人与站长握得住()
    {
        var mine = Seat("投稿的人");
        var others = Seat("路过的人");
        var mineVideo = Arrive("BVset0000500", "我投的", submitter: mine);
        var othersVideo = Arrive("BVset0000501", "别人投的", submitter: others);
        var orphan = Arrive("BVset0000502", "爬工代投的"); // 无归属：只有站长握得住，与 MemberMail 同一口径

        var owner = Member(Seat("站长"), MemberRoles.Owner);
        var submitter = Member(mine, MemberRoles.Member);
        var passer = Member(others, MemberRoles.Member);

        // 站长短路：自己的 / 别人的 / 无主的 / 压根没这条的，钥匙都在他手里（「查无此片」归端点那趟回查接住）
        Assert.True(ManageGate.CanManage(_db, mineVideo, owner));
        Assert.True(ManageGate.CanManage(_db, othersVideo, owner));
        Assert.True(ManageGate.CanManage(_db, orphan, owner));
        Assert.True(ManageGate.CanManage(_db, 424242, owner));

        // 投稿人本人：只握得住自己那一条
        Assert.True(ManageGate.CanManage(_db, mineVideo, submitter));
        Assert.False(ManageGate.CanManage(_db, othersVideo, submitter));
        Assert.False(ManageGate.CanManage(_db, orphan, submitter));

        // 第三者与匿名（没席位）：一律假——revive / requeue / 稿件设置那几口都靠这一条收口
        Assert.False(ManageGate.CanManage(_db, mineVideo, passer));
        Assert.False(ManageGate.CanManage(_db, mineVideo, null));
        Assert.False(ManageGate.CanManage(_db, othersVideo, null));
    }

    [Fact]
    public void 站长在站长室点续下与重排_不受新门影响()
    {
        // 这条钉的是「站长室自己的调用不受影响」怎么得出的：
        // ① AdminGate 把 POST …/revive 与 …/requeue 点名成站长事务（IsAdminRequest = true），
        //    前置中间件据此要求 MemberGate.IsOwner —— 今天本来就只有站长进得来；
        // ② 站长在 ManageGate.CanManage 里 IsOwner 短路为真，钥匙永远握着。
        // 两头都真 ⇒ 端点里补的那把 CanManageVideo 只会多挡别人，挡不到站长室自己。
        var id = Arrive("BVset0000600", "站长要续下的那首");
        var ownerSeat = Seat("站长");
        var owner = Member(ownerSeat, MemberRoles.Owner);

        Assert.True(_gate.IsAdminRequest(Request("POST", "/api/videos/600/revive")));
        Assert.True(_gate.IsAdminRequest(Request("POST", "/api/videos/600/requeue")));
        Assert.True(MemberGate.IsOwner(owner));
        Assert.True(ManageGate.CanManage(_db, id, owner));
    }
}
