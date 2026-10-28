using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// 站内合集台账：集是会员自己攒的片单，item 只存 (集, 片) 这一对，片名封面读时现 JOIN。
/// 三把尺子盯紧：计数/封面/片单只认已到站（Arrived）的货；封面取最新入集那条（本地件优先）；
/// 同片在同集只占一坑（幂等）；销集连坐 item，片本身一根手指不碰。
/// </summary>
public sealed class CollectionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public CollectionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-collections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    /// <summary>造一条到货：bvid + B 站档案 + 标题/封面，直达 Arrived。arrived=false 就让它留在队列里。</summary>
    private long Arrive(string bvid, string? title = null, string? coverUrl = null, bool arrived = true)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, "音乐>翻唱", "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title ?? bvid, coverUrl);
        if (arrived)
        {
            _db.MarkArrived(id);
        }

        return id;
    }

    /// <summary>造一个在席席位（集挂席位 id，与私信/举报同一套身份）；bound=false 留个没昵称的空席。</summary>
    private long Seat(string name, bool bound = true)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        if (bound)
        {
            Assert.NotNull(_db.BindMember(id, 950_000 + id, name, null));
        }

        return id;
    }

    [Fact]
    public void 建集合_计数从0起_东家名片现查席位()
    {
        var mine = Seat("攒集的人");
        var other = Seat("别人");
        var anonymous = Seat("素净", bound: false); // 没绑 B 站号的空席：昵称无从谈起

        var first = _db.CreateCollection("睡前听", "夜里循环的那几首", mine);
        var second = _db.CreateCollection("通勤", null, mine);
        _db.CreateCollection("别人的集", null, other);

        var all = _db.ListCollections();
        Assert.Equal(3, all.Count);
        Assert.Equal("别人的集", all[0].Name); // 最新开的排前头（同刻就按 id 倒序兑底）
        Assert.Equal("通勤", all[1].Name);
        Assert.Equal(first, all[2].Id);
        Assert.Equal("睡前听", all[2].Name);
        Assert.Equal("夜里循环的那几首", all[2].Intro);
        Assert.Equal(mine, all[2].OwnerMemberId);
        Assert.Equal("攒集的人", all[2].OwnerName);
        Assert.Equal(0, all[2].ItemCount);
        Assert.Null(all[2].Cover);
        Assert.Null(all[1].Intro);

        // 只报自己的：个人空间那口吃这个
        Assert.Equal(new[] { second, first }, _db.ListCollections(mine).Select(c => c.Id));
        Assert.Empty(_db.ListCollections(Seat("第三者")));

        // 空席当东家：席位在但没昵称 → null，集不跟着消失
        var bare = _db.CreateCollection("匿名集", null, anonymous);
        Assert.Null(Assert.Single(_db.ListCollections(anonymous), c => c.Id == bare).OwnerName);
    }

    [Fact]
    public void 加片幂等_同片同集只占一坑()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("循环单", null, owner);
        var video = Arrive("BVcoll000001");

        Assert.True(_db.AddItem(id, video));
        Assert.True(_db.AddItem(id, video)); // 再来一次：撞主键就当没这回事，照样放行

        Assert.Single(_db.GetCollectionVideoIds(id));
        Assert.Equal(1, Assert.Single(_db.ListCollections(owner)).ItemCount);

        // 集不存在：不放行（404 归端点报）
        Assert.False(_db.AddItem(424242, video));
        Assert.Empty(_db.GetCollectionVideoIds(424242));
    }

    [Fact]
    public void 详情只装已到站的_排队里的不占坑()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("在听的", null, owner);
        var here = Arrive("BVcoll000002", "已入站的");
        var queued = Arrive("BVcoll000003", "还在路上的", arrived: false);

        Assert.True(_db.AddItem(id, here));
        Assert.True(_db.AddItem(id, queued)); // 排队稿允许落行（档案都不齐，读的时候自然看不见）

        var detail = _db.GetCollection(id);
        Assert.NotNull(detail);
        Assert.Single(detail.Items);
        Assert.Equal("BVcoll000002", detail.Items[0].Bvid);
        Assert.Equal("已入站的", detail.Items[0].Title);
        Assert.Equal("测试UP", detail.Items[0].UpName);
        Assert.Equal("音乐>翻唱", detail.Items[0].Zone);
        Assert.Equal(1, detail.ItemCount); // 计数与片单同一把尺子
        Assert.Equal(2, _db.GetCollectionVideoIds(id).Count); // 判定料不筛到货：在不在集里另说
    }

    [Fact]
    public void 封面取最新入集那条_本地件优先热链兑底()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("看图", null, owner);
        var hot = Arrive("BVcoll000004", coverUrl: "https://pic.example/hot.jpg");
        var local = Arrive("BVcoll000005", coverUrl: "https://pic.example/stale.jpg");
        _db.RegisterCoverArtifact(local, "收藏/本地封面.jpg");

        Assert.True(_db.AddItem(id, hot));
        Assert.True(_db.AddItem(id, local));
        Assert.Equal("/media/收藏/本地封面.jpg", _db.GetCollection(id)!.Cover); // 最新那条：本地件压热链

        // 最新那条挪出去，封面回落到剩下那条的热链
        Assert.True(_db.RemoveItem(id, local));
        Assert.Equal("https://pic.example/hot.jpg", _db.GetCollection(id)!.Cover);

        // 一条不剩：没到货就没封面
        Assert.True(_db.RemoveItem(id, hot));
        Assert.Null(_db.GetCollection(id)!.Cover);
    }

    [Fact]
    public void 片单倒序_最新入集的排前头()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("按新到旧", null, owner);
        var a = Arrive("BVcoll000006");
        var b = Arrive("BVcoll000007");
        var c = Arrive("BVcoll000008");

        _db.AddItem(id, a);
        _db.AddItem(id, b);
        _db.AddItem(id, c);

        Assert.Equal(new[] { c, b, a }, _db.GetCollection(id)!.Items.Select(i => i.Id));

        // 挪出中间那条：剩下的照旧倒序，片本身一根手指不碰
        Assert.True(_db.RemoveItem(id, b));
        Assert.Equal(new[] { c, a }, _db.GetCollection(id)!.Items.Select(i => i.Id));
        Assert.False(_db.RemoveItem(id, b)); // 本来就没这部（或早挪走了）
        Assert.NotNull(_db.GetVideoBrief(b));
    }

    [Fact]
    public void 删集合连坐item_馆藏片照旧在架()
    {
        var owner = Seat("集主");
        var keep = _db.CreateCollection("留着", null, owner);
        var doomed = _db.CreateCollection("要销的", null, owner);
        var one = Arrive("BVcoll000009");
        var two = Arrive("BVcoll000010");
        _db.AddItem(doomed, one);
        _db.AddItem(doomed, two);
        _db.AddItem(keep, one);

        Assert.True(_db.DeleteCollection(doomed));
        Assert.False(_db.CollectionExists(doomed));
        Assert.Empty(_db.GetCollectionVideoIds(doomed)); // item 行跟着没了，不留无主行
        Assert.Equal(new[] { keep }, _db.ListCollections(owner).Select(c => c.Id));
        Assert.Equal(1, Assert.Single(_db.GetCollectionVideoIds(keep))); // 别的集不受连坐

        // 片是馆藏，不归集管：删集不动它一根毫毛
        Assert.Equal(VideoStatus.Arrived, _db.GetVideoBrief(one)!.Value.Status);

        // 再销一次：没这个集了（端点据此回 404）
        Assert.False(_db.DeleteCollection(doomed));
    }

    [Fact]
    public void 查无的合集一律null或false()
    {
        Assert.Null(_db.GetCollection(424242));
        Assert.False(_db.CollectionExists(424242));
        Assert.Null(_db.GetCollectionOwner(424242));
        Assert.False(_db.RemoveItem(424242, 1));
        Assert.Empty(_db.ListCollections(424242));

        var owner = Seat("集主");
        var id = _db.CreateCollection("有个集", null, owner);
        Assert.True(_db.CollectionExists(id));
        Assert.Equal(owner, _db.GetCollectionOwner(id));
        Assert.Null(_db.GetVideoIdByBvid("BV不存在0000"));
        Assert.Equal(Arrive("BVcoll000011"), _db.GetVideoIdByBvid("BVcoll000011"));
    }

    [Fact]
    public void 集名校验_空与超长都不放行()
    {
        Assert.True(CollectionsEndpoints.TryNormalizeName("  睡前听  ", out var name, out _));
        Assert.Equal("睡前听", name); // 两侧空白掰掉再数长度

        Assert.False(CollectionsEndpoints.TryNormalizeName(null, out _, out var empty));
        Assert.Contains("名", empty);
        Assert.False(CollectionsEndpoints.TryNormalizeName("   ", out _, out _));

        Assert.False(CollectionsEndpoints.TryNormalizeName(new string('长', StationDb.MaxCollectionNameLength + 1), out _, out var tooLong));
        Assert.Contains(StationDb.MaxCollectionNameLength.ToString(), tooLong);
        Assert.True(CollectionsEndpoints.TryNormalizeName(new string('长', StationDb.MaxCollectionNameLength), out _, out _));
    }

    [Fact]
    public void 归属核验_席位缺失与别人家集各回各码()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("别人的别看", null, owner);

        Assert.Equal(401, CollectionsEndpoints.CheckOwnership(_db, null, id)!.Value.Status);
        Assert.Equal(404, CollectionsEndpoints.CheckOwnership(_db, Member(owner, MemberRoles.Member), 424242)!.Value.Status);
        Assert.Equal(403, CollectionsEndpoints.CheckOwnership(_db, Member(Seat("路过的"), MemberRoles.Member), id)!.Value.Status);
        Assert.Null(CollectionsEndpoints.CheckOwnership(_db, Member(owner, MemberRoles.Member), id)); // 本人放行
        Assert.Null(CollectionsEndpoints.CheckOwnership(_db, Member(Seat("站长"), MemberRoles.Owner), id)); // 站长兜底代管
    }

    /// <summary>拿席位 id 拼个身份（端点判归属只吃 Id 与 Role，其余字段无关）。</summary>
    private static StationMember Member(long id, string role) =>
        new(id, 1000 + id, "席位" + id, null, null, MemberStatus.Active, role, DateTimeOffset.UnixEpoch.ToString("O"), null, null);
}
