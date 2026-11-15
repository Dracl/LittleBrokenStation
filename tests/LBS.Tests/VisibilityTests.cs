using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// 稿件与合集的可见性（public/private）：读侧三把尺子——访客（PublicOnly）只看 public、
/// 席位（Of(id)）看 public + 自己的（投稿人 / 东家本人）、站长（Everything）全见；
/// 谓词只从 StationDb.VisibilityFilter 那一个出口出，所以这些用例盯的是「每个读出口都递了尺子没有」：
/// 卡阵、搜索、分区（含那个 count 与入口卡底图）、相关推荐、放映厅、观看历史、合集的清单/计数/封面/片单。
/// 写侧两件事：投稿带可见性（撞重只补档案、绝不翻回公开）、集的可见性改得动且不连累别的栏。
/// </summary>
public sealed class VisibilityTests : IDisposable
{
    // 入口卡底图按本地日（UTC+8）钉快照，取数时刻照抄 ZoneEntryCoverTests 那套
    private static readonly DateTimeOffset Day1 = new(2026, 10, 29, 6, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;

    public VisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-visibility-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>造一条到站的货：可见性与投稿人当场定（写侧只有 AddVideosBulk 这一支能带私有进来）。</summary>
    private long Arrive(
        string bvid,
        string? title = null,
        long? submitter = null,
        VideoVisibility visibility = VideoVisibility.Public,
        string zone = "音乐>翻唱")
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, title ?? bvid, null, null, visibility),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("测试UP", 123, zone, "2026-05-20 18:00:00", "简介", "1万"),
            Array.Empty<PageDraft>());
        _db.MarkSubmitted(id, new[] { "task-" + bvid }, title ?? bvid, null);
        _db.MarkArrived(id);
        if (submitter is not null)
        {
            _db.SetVideoSubmitterMember(id, submitter);
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

    /// <summary>往台账塞 n 枪观看（形状照 VideoPage.vue 的上报实况；入口卡底图按这个选冠军分区）。</summary>
    private void Watch(string bvid, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var beat = i % 2 == 1;
            _db.InsertEventsBatch(new[]
            {
                new EventDraft(
                    EventActors.Guest,
                    null,
                    beat ? "playback.heartbeat" : "playback.play",
                    "video",
                    bvid,
                    beat ? """{"t":42}""" : null,
                    "9.9.9.9",
                    Day1.ToString("O")),
            });
        }
    }

    [Fact]
    public void 访客尺子看不到私有_本人看得到_站长全见()
    {
        var me = Seat("藏货的人");
        var mine = Arrive("BVvis0000001", "我藏的一首", submitter: me, visibility: VideoVisibility.Private);
        var publicOne = Arrive("BVvis0000002", "人人听得的一首");

        // 卡阵：三把尺子三个世界
        Assert.DoesNotContain(_db.ListVideos(), v => v.Id == mine);
        Assert.Contains(_db.ListVideos(VisibilityScope.Of(me)), v => v.Id == mine);
        Assert.Contains(_db.ListVideos(VisibilityScope.Everything), v => v.Id == mine);
        Assert.Contains(_db.ListVideos(VisibilityScope.Everything), v => v.Id == publicOne);

        // 卡阵那行的可见性带出来（前端画那枚锁用）
        Assert.Equal(VideoVisibility.Private, _db.ListVideos(VisibilityScope.Everything).Single(v => v.Id == mine).Visibility);
        Assert.Equal(VideoVisibility.Public, _db.ListVideos(VisibilityScope.PublicOnly).Single(v => v.Id == publicOne).Visibility);

        // 搜索同一张嘴（搜索是为了看，私有稿不该被搜出来）
        Assert.Empty(_db.SearchVideos("我藏的一首", null));
        Assert.Single(_db.SearchVideos("我藏的一首", null, 50, VisibilityScope.Of(me)));
        Assert.Single(_db.SearchVideos("我藏的一首", null, 50, VisibilityScope.Everything));

        // 分区页面上的货
        Assert.DoesNotContain(_db.ListZoneVideos("音乐"), v => v.Id == mine);
        Assert.Contains(_db.ListZoneVideos("音乐", VisibilityScope.Of(me)), v => v.Id == mine);
        Assert.Contains(_db.ListZoneVideos("音乐", VisibilityScope.Everything), v => v.Id == mine);

        // 放映厅：别人连「这条存在」都问不出来（与查无同一张面孔），本人拿到的载荷带得出可见性
        Assert.Null(_db.GetPlaybackData("BVvis0000001"));
        Assert.Null(_db.GetPlaybackData("BVvis0000001", VisibilityScope.Of(Seat("第三者"))));
        Assert.Equal("private", _db.GetPlaybackData("BVvis0000001", VisibilityScope.Of(me))!.Visibility);
        Assert.Equal("public", _db.GetPlaybackData("BVvis0000002", VisibilityScope.Everything)!.Visibility);

        // 无归属的私有行（爬虫代投 / 老数据反查失败）：只有站长握得住，与 CanManageVideo 同一口径
        var orphan = Arrive("BVvis0000003", "没主人的", visibility: VideoVisibility.Private);
        Assert.DoesNotContain(_db.ListVideos(VisibilityScope.Of(me)), v => v.Id == orphan);
        Assert.Contains(_db.ListVideos(VisibilityScope.Everything), v => v.Id == orphan);
    }

    [Fact]
    public void 分区那个数字剔私有_与页面上摆的货同一口径()
    {
        var me = Seat("藏货的人");
        Arrive("BVvis0000010", zone: "音乐>翻唱");
        Arrive("BVvis0000011", zone: "音乐>翻唱");
        Arrive("BVvis0000012", "藏的一首", submitter: me, visibility: VideoVisibility.Private, zone: "音乐");
        Arrive("BVvis0000013", zone: "动画");

        // 访客：数字与 ListZoneVideos 摆出来的一条不差
        Assert.Equal(2, Assert.Single(_db.ListZones(), z => z.Name == "音乐").Count);
        Assert.Equal(2, _db.ListZoneVideos("音乐").Count);

        // 本人：自己那条既上数字也上页面
        Assert.Equal(3, Assert.Single(_db.ListZones(VisibilityScope.Of(me)), z => z.Name == "音乐").Count);
        Assert.Equal(3, _db.ListZoneVideos("音乐", VisibilityScope.Of(me)).Count);

        // 站长：全算
        Assert.Equal(3, Assert.Single(_db.ListZones(VisibilityScope.Everything), z => z.Name == "音乐").Count);

        // 动画区不受连坐：私有与否只动自己那区的数
        Assert.Equal(1, Assert.Single(_db.ListZones(), z => z.Name == "动画").Count);
    }

    [Fact]
    public void 入口卡底图不选私有_快照那条转私当天也不露()
    {
        var me = Seat("藏货的人");
        var publicHot = Arrive("BVvis0000020", zone: "音乐", submitter: me);
        var privateTop = Arrive("BVvis0000021", "最火的私有货", submitter: me, visibility: VideoVisibility.Private, zone: "音乐");
        _db.RegisterCoverArtifact(publicHot, "covers/public.jpg");
        _db.RegisterCoverArtifact(privateTop, "covers/private.jpg");
        Watch("BVvis0000021", 30); // 私有那条最火
        Watch("BVvis0000020", 10);

        // 本人/站长：候选里有它，底图就是它
        Assert.Equal("BVvis0000021", _db.GetZoneEntryCover(Day1, VisibilityScope.Of(me)).Bvid);
        Assert.Equal("BVvis0000021", _db.GetZoneEntryCover(Day1, VisibilityScope.Everything).Bvid);

        // 访客：同一时刻换上第二条有封面的（私有那条压根不进候选），钉进快照的也只能是这张
        Assert.Equal("BVvis0000020", _db.GetZoneEntryCover(Day1).Bvid);

        // 钉好的那条今天转私：访客这一趟判在取数那一刻，当它已下架重算（重算也挑不出私有那枚）
        Execute("UPDATE videos SET visibility = 'private' WHERE bvid = $b;", ("$b", "BVvis0000020"));
        Assert.Null(_db.GetZoneEntryCover(Day1).Bvid);

        // 本人那一侧它还在（同一枚快照对不同尺子给不同答案，不给没钥匙的人露半点）
        Assert.Equal("BVvis0000020", _db.GetZoneEntryCover(Day1, VisibilityScope.Of(me)).Bvid);
    }

    [Fact]
    public void 相关推荐补位不选私有()
    {
        var me = Seat("藏货的人");
        var self = Arrive("BVvis0000030");
        for (var i = 1; i <= 6; i++)
        {
            Arrive($"BVvis00000{i + 30}");
        }

        var mineOne = Arrive("BVvis0000041", "私有其一", submitter: me, visibility: VideoVisibility.Private);
        var mineTwo = Arrive("BVvis0000042", "私有其二", submitter: me, visibility: VideoVisibility.Private);
        var orphan = Arrive("BVvis0000043", "无主私货", visibility: VideoVisibility.Private);

        // 随机补位也只从这位看得着的库里挑（层内 RANDOM()，所以整表比对而不是点位比对）
        var guest = _db.GetRelatedVideos(self, 50);
        Assert.DoesNotContain(guest, v => v.Id == mineOne || v.Id == mineTwo || v.Id == orphan);
        Assert.Equal(6, guest.Count);

        var owner = _db.GetRelatedVideos(self, 50, VisibilityScope.Of(me));
        Assert.Contains(owner, v => v.Id == mineOne);
        Assert.Contains(owner, v => v.Id == mineTwo);
        Assert.DoesNotContain(owner, v => v.Id == orphan); // 没归属的私有行连别人也别想撞见

        Assert.Contains(_db.GetRelatedVideos(self, 50, VisibilityScope.Everything), v => v.Id == orphan);
    }

    [Fact]
    public void 私有集只对东家与站长露_计数封面片单三处同步剔()
    {
        var owner = Seat("集主");
        var other = Seat("别人");
        var open = _db.CreateCollection("公开的集", null, owner);
        var hidden = _db.CreateCollection("藏起来的集", null, owner);
        Assert.True(_db.SetCollectionVisibility(hidden, VideoVisibility.Private));

        // 清单：私有集只对东家与站长露面
        Assert.Contains(_db.ListCollections(), c => c.Id == open);
        Assert.DoesNotContain(_db.ListCollections(), c => c.Id == hidden);
        Assert.DoesNotContain(_db.ListCollections(null, VisibilityScope.Of(other)), c => c.Id == hidden);
        Assert.Contains(_db.ListCollections(null, VisibilityScope.Of(owner)), c => c.Id == hidden);
        Assert.Contains(_db.ListCollections(null, VisibilityScope.Everything), c => c.Id == hidden);

        // 详情：没钥匙的人问不出来（与查无同一张面孔）
        Assert.NotNull(_db.GetCollection(open));
        Assert.Null(_db.GetCollection(hidden));
        Assert.Null(_db.GetCollection(hidden, VisibilityScope.Of(other)));
        Assert.NotNull(_db.GetCollection(hidden, VisibilityScope.Of(owner)));

        // 集里的三条：公开的、东家自己的私有、别人的私有（封面各给一张本地件，方便看剔了谁）
        var col = _db.CreateCollection("看数", null, owner);
        var a = Arrive("BVvis0000050");
        var b = Arrive("BVvis0000051", "东家自己的私货", submitter: owner, visibility: VideoVisibility.Private);
        var c = Arrive("BVvis0000052", "别人的私货", submitter: other, visibility: VideoVisibility.Private);
        _db.RegisterCoverArtifact(a, "covers/a.jpg");
        _db.RegisterCoverArtifact(b, "covers/b.jpg");
        _db.RegisterCoverArtifact(c, "covers/c.jpg");
        Assert.True(_db.AddItem(col, a));
        Assert.True(_db.AddItem(col, b));
        Assert.True(_db.AddItem(col, c));

        // 访客：计数、封面、片单、判定料四处同一把尺子（最新入集那条是 c，剔完就该回落到 a）
        var guestBrief = Assert.Single(_db.ListCollections(), x => x.Id == col);
        Assert.Equal(1, guestBrief.ItemCount);
        Assert.Equal("/media/covers/a.jpg", guestBrief.Cover);
        Assert.Equal(new[] { a }, _db.GetCollection(col)!.Items.Select(i => i.Id));
        Assert.Equal(new[] { a }, _db.GetCollectionVideoIds(col));

        // 东家：自己的私货算自己的数，别人那条照样四处都不许露
        var ownBrief = Assert.Single(_db.ListCollections(null, VisibilityScope.Of(owner)), x => x.Id == col);
        Assert.Equal(2, ownBrief.ItemCount);
        Assert.Equal("/media/covers/b.jpg", ownBrief.Cover); // 封面子查询跟着剔：不许「数字说两条、图还是那条私货」
        Assert.Equal(new[] { b, a }, _db.GetCollection(col, VisibilityScope.Of(owner))!.Items.Select(i => i.Id));
        Assert.Equal(new[] { a, b }, _db.GetCollectionVideoIds(col, VisibilityScope.Of(owner)).OrderBy(x => x));

        // 站长：三条全在（计数 = 片单 = 判定料）
        Assert.Equal(3, Assert.Single(_db.ListCollections(null, VisibilityScope.Everything), x => x.Id == col).ItemCount);
        Assert.Equal(3, _db.GetCollection(col, VisibilityScope.Everything)!.Items.Count);
        Assert.Equal(3, _db.GetCollectionVideoIds(col, VisibilityScope.Everything).Count);
        Assert.Equal("/media/covers/c.jpg", Assert.Single(_db.ListCollections(null, VisibilityScope.Everything), x => x.Id == col).Cover);

        // 私有集里躺着的私有稿：东家拿得到集也拿得到片，访客两头都摸不着
        var privateCol = _db.CreateCollection("双私", null, owner);
        _db.SetCollectionVisibility(privateCol, VideoVisibility.Private);
        _db.AddItem(privateCol, c);
        Assert.Null(_db.GetCollection(privateCol, VisibilityScope.Of(other)));
        Assert.Empty(_db.GetCollectionVideoIds(privateCol, VisibilityScope.Of(other))); // 视频侧那把尺子先把它剔了
    }

    [Fact]
    public void 改集_只改名字不动可见性_只改可见性不动名字()
    {
        var owner = Seat("集主");
        var id = _db.CreateCollection("原名", "原简介", owner);

        // 只改名字：可见性一根手指不碰（还是那条私有集，访客照样看不见）
        Assert.True(_db.SetCollectionVisibility(id, VideoVisibility.Private));
        Assert.True(_db.UpdateCollection(id, "新名", null, null));
        Assert.DoesNotContain(_db.ListCollections(), c => c.Id == id);
        var mine = Assert.Single(_db.ListCollections(null, VisibilityScope.Of(owner)), c => c.Id == id);
        Assert.Equal("新名", mine.Name);
        Assert.Equal("原简介", mine.Intro); // 没给的那栏不动

        // 只改可见性：名字与简介照旧
        Assert.True(_db.UpdateCollection(id, null, null, VideoVisibility.Public));
        var back = Assert.Single(_db.ListCollections(), c => c.Id == id);
        Assert.Equal("新名", back.Name);
        Assert.Equal("原简介", back.Intro);

        // 只改简介：空白存 null（与建集同一口径），名字与可见性照旧
        Assert.True(_db.UpdateCollection(id, null, "   ", null));
        Assert.Null(Assert.Single(_db.ListCollections(), c => c.Id == id).Intro);

        // SetCollectionVisibility 往返
        Assert.True(_db.SetCollectionVisibility(id, VideoVisibility.Private));
        Assert.DoesNotContain(_db.ListCollections(), c => c.Id == id);
        Assert.True(_db.SetCollectionVisibility(id, VideoVisibility.Public));
        Assert.Contains(_db.ListCollections(), c => c.Id == id);
        Assert.True(_db.CollectionExists(id)); // 转私转公都只是改一栏，集还是那个集

        // 三栏都没给值：没话改，不空转一圈
        Assert.False(_db.UpdateCollection(id, null, null, null));

        // 查无的集一律 false（404 归端点报）
        Assert.False(_db.UpdateCollection(424242, "改名", null, null));
        Assert.False(_db.SetCollectionVisibility(424242, VideoVisibility.Private));
    }

    [Fact]
    public void 静态区问档_artifacts与previews与加档件都命中_BV号兜底_台账不认时null()
    {
        var me = Seat("藏货的人");
        var hidden = Arrive("BVvis0000060", "藏起来的一首", submitter: me, visibility: VideoVisibility.Private);
        var open = Arrive("BVvis0000061", "人人听得的一首");

        // 稿件自身的可见性：查无回 null（「没这条」与「藏着」是两件事）
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibility(hidden));
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibility(open));
        Assert.Null(_db.GetVideoVisibility(424242));

        // ① artifacts 精确命中
        _db.RegisterCoverArtifact(hidden, "藏/封面.jpg");
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/封面.jpg"));

        // ② previews 命中（rel 相对 data/previews，同一字符串传进来）
        _db.RegisterPreviewArrived(hidden, 1, "BVvis0000060.mp4", 320, 180, 6.5, 20480);
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("BVvis0000060.mp4"));

        // ③ video_codecs 命中（加档件）
        Assert.True(_db.AddCodecVersion(hidden, 1, "hevc"));
        var codecRow = Assert.Single(_db.GetCodecVersions(hidden), r => r.Codec == "hevc");
        _db.MarkCodecArrived(codecRow.Id, "藏/1-正片-1080P-HEVC.mp4");
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/1-正片-1080P-HEVC.mp4"));

        // 公开的件照样答 public（不许一律当私有，否则全站静态区一起变黑）
        _db.RegisterCoverArtifact(open, "公/封面.jpg");
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibilityByMediaRel("公/封面.jpg"));

        // ④ 台账没行：退到文件名尾部的 BV 号反查（引擎落盘名常带 bvid）
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("杂项/随手拍-BVvis0000060.mp4"));
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/1-BVvis0000060-1080P-AVC.mp4"));

        // 路径形不规整也认（反斜杠、头斜杠、./ 前缀）
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏\\封面.jpg"));
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("/./藏/封面.jpg"));

        // 台账彻底不认这个件（头像/表情那类根本不在这些表里）→ null 交调用方放行，不许当私有
        Assert.Null(_db.GetVideoVisibilityByMediaRel("avatars/950001.jpg"));
        Assert.Null(_db.GetVideoVisibilityByMediaRel("随便/没这个件.mp4"));
        Assert.Null(_db.GetVideoVisibilityByMediaRel("   "));
        Assert.Null(_db.GetVideoVisibilityByMediaRel(""));

        // 文件名里那串不像 BV 号（差一位都不算）：不反查，免得把不相干的稿子按私有挡了
        Assert.Null(_db.GetVideoVisibilityByMediaRel("杂项/随手拍-BVvis000006.mp4"));
    }

    [Fact]
    public void 撞重导入不把私有改回公开_只补档案那克制留着()
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo("https://www.bilibili.com/video/BVvis0000070", "BVvis0000070", "先交的一版", null, null, VideoVisibility.Private),
        });
        Assert.Single(outcome.InsertedIds);
        var id = outcome.InsertedIds[0];
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibility(id));

        // 导入源（UP 投稿列表 / 收藏夹）再撞一次：带封面与投稿时间，没带可见性（默认 public）
        var again = _db.AddVideosBulk(new[]
        {
            new NewVideo("https://www.bilibili.com/video/BVvis0000070", "BVvis0000070", null, "https://i0.hdslb.com/c.jpg", "2024-03-01T12:00:00+08:00"),
        });
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Duplicated);

        // 撞重分支只补 cover/时间：visibility 一根手指不碰
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibility(id));
        Assert.DoesNotContain(_db.ListVideos(), v => v.Id == id); // 访客尺子下它还是藏着（没被翻成公开）
        var row = Assert.Single(_db.ListVideos(VisibilityScope.Everything), v => v.Id == id);
        Assert.Equal("https://i0.hdslb.com/c.jpg", row.CoverUrl); // 补档案那件照旧干活，别把克制做过头
    }

    [Fact]
    public void 别人的历史里不再挂私有稿_本人的与站长的照旧()
    {
        var me = Seat("藏货的人");
        var other = Seat("看过的人");
        var hidden = Arrive("BVvis0000080", "藏的一首", submitter: me, visibility: VideoVisibility.Private);
        var open = Arrive("BVvis0000081", "人人听的");
        _db.UpsertVideoHistory(other, hidden, 1, 10, 100);
        _db.UpsertVideoHistory(other, open, 1, 10, 100);
        _db.UpsertVideoHistory(me, hidden, 1, 20, 100);

        // 别人历史里不该再挂私有稿的标题封面（行不删，只是看不见，与「片被删了行就自然看不见」同一套路）
        Assert.Equal(new[] { open }, _db.GetVideoHistory(other).Select(r => r.VideoId));
        Assert.Equal(new[] { open }, _db.GetVideoHistory(other, 100, VisibilityScope.Of(other)).Select(r => r.VideoId));

        // 本人查自己的：自己藏的那条照旧在
        Assert.Equal(new[] { hidden }, _db.GetVideoHistory(me, 100, VisibilityScope.Of(me)).Select(r => r.VideoId));

        // 站长全见；行还在（尺子一换就回来，没说删）
        Assert.Equal(2, _db.GetVideoHistory(other, 100, VisibilityScope.Everything).Count);
    }

    [Fact]
    public void 老库补列_存量行靠DEFAULT落公开_不回填不立碑()
    {
        var me = Seat("藏货的人");
        var hidden = Arrive("BVvis0000090", "藏的", submitter: me, visibility: VideoVisibility.Private);
        var open = Arrive("BVvis0000091", "公开的");

        // 装作早先建的库没这两列，再走一遍开局补列
        Execute("ALTER TABLE videos DROP COLUMN visibility;");
        Execute("ALTER TABLE collections DROP COLUMN visibility;");
        _db.Initialize();

        // 存量行靠 DEFAULT 天然落 public：没回填脚本、settings 里也没立碑（那列本来就被 DROP 带走了）
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibility(open));
        Assert.Equal(VideoVisibility.Public, _db.GetVideoVisibility(hidden));
        Assert.Equal(2, _db.ListVideos(VisibilityScope.Everything).Count);
        Assert.Equal(2, _db.ListVideos().Count); // 默认尺子下都看得见（都成公开了）

        // 补列后新开的集照样能读能写
        var col = _db.CreateCollection("补列后开的集", null, me);
        Assert.Contains(_db.ListCollections(), c => c.Id == col);
        Assert.True(_db.SetCollectionVisibility(col, VideoVisibility.Private));
        Assert.DoesNotContain(_db.ListCollections(), c => c.Id == col);
    }

    [Fact]
    public void 认不出的值一律当公开_TryParse只认那三样()
    {
        // 读侧宁缺毋假：脏值不抛，一律当公开
        Assert.Equal(VideoVisibility.Public, VisibilityCodec.FromColumn(null));
        Assert.Equal(VideoVisibility.Public, VisibilityCodec.FromColumn(""));
        Assert.Equal(VideoVisibility.Public, VisibilityCodec.FromColumn("shy"));
        Assert.Equal(VideoVisibility.Private, VisibilityCodec.FromColumn("private"));
        Assert.Equal(VideoVisibility.Private, VisibilityCodec.FromColumn("Private")); // 大小写不认生：宁可多挡不误放

        // 写侧只可能出现那两枚小写字面量
        Assert.Equal("public", VisibilityCodec.ToColumn(VideoVisibility.Public));
        Assert.Equal("private", VisibilityCodec.ToColumn(VideoVisibility.Private));

        // 端点收参数：public / private / 空（= public）认，其余回 false（据此回 400）
        Assert.True(VisibilityCodec.TryParse(null, out var empty));
        Assert.Equal(VideoVisibility.Public, empty);
        Assert.True(VisibilityCodec.TryParse("   ", out var blank));
        Assert.Equal(VideoVisibility.Public, blank);
        Assert.True(VisibilityCodec.TryParse(" Private ", out var spaced));
        Assert.Equal(VideoVisibility.Private, spaced);
        Assert.False(VisibilityCodec.TryParse("secret", out _));

        // 尺子：Of(null) 就是访客尺子
        Assert.Equal(VisibilityScope.PublicOnly, VisibilityScope.Of(null));
        Assert.False(VisibilityScope.Of(3).All);
        Assert.Equal(3, VisibilityScope.Of(3).MemberId);
        Assert.True(VisibilityScope.Everything.All);
        Assert.False(VisibilityScope.PublicOnly.All);
        Assert.Null(VisibilityScope.PublicOnly.MemberId);
    }
}
