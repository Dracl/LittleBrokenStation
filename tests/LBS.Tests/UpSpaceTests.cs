using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// UP 空间的读侧（<c>GET /api/up/{mid}/videos</c> 的两条腿）：
/// ① 只给已到货的行 —— 分站没缓存的不进橱窗（那是站长的工程进度，不是观众看到的货架）；
/// ② 尺子在取数那一刻量 —— 别人的私有稿在这页等于不存在，站长全见、本人见自己的；
/// ③ B 站投稿时间倒序，没抽到投稿时间的垫到最后（SQLite 里 NULL 在 DESC 反而打头，得手动按下去）；
/// ④ 列形状与 <see cref="StationDb.ListVideos"/> 一字不差 —— 两者共用 ReadEntries、列下标写死，
///    错一列就是整页串栏（把 UP 名显示在时长那一栏那种事），所以逐栏对号入座地钉；
/// ⑤ 页头快照取「该 mid 最新一条非空」—— 半截档案不许埋掉已经采到的脸；站内没这位回 null。
/// </summary>
public sealed class UpSpaceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public UpSpaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-up-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
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

    /// <summary>造一条该 UP 的稿：到不到站、可见性、两种投稿时间、投稿人当场定。</summary>
    private long Arrive(
        string bvid,
        long upMid,
        string upName = "测试UP",
        VideoVisibility visibility = VideoVisibility.Public,
        string? publishedAtUtc = null,
        bool arrived = true,
        long? submitter = null,
        string? publishText = "2026-05-20 18:00")
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, "标题-" + bvid, null, publishedAtUtc, visibility),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(upName, upMid, "音乐>翻唱", publishText, "简介", "1.2万"),
            new[] { new PageDraft("task-" + bvid, 1, "P1", 1000 + id, "12:34", "videos") });
        if (submitter is not null)
        {
            _db.SetVideoSubmitterMember(id, submitter.Value);
        }

        if (arrived)
        {
            _db.MarkArrived(id);
        }

        return id;
    }

    private long Seat(long tail)
    {
        var (id, _) = _db.CreateMemberInvite("席位" + tail);
        Assert.NotNull(_db.BindMember(id, 960_000 + tail, "朋友" + tail, null));
        return id;
    }

    private static string[] Bvids(IReadOnlyList<VideoEntry> rows) => rows.Select(r => r.Bvid).ToArray();

    [Fact]
    public void 只给已到货的_分站没缓存的不出现()
    {
        Arrive("BVUP0000000001", 777);
        Arrive("BVUP0000000002", 777, arrived: false); // 还没收进来（Pending / 进站中 / 失败都不该露脸）
        Arrive("BVUP0000000003", 999);                 // 别人的 UP

        Assert.Equal(new[] { "BVUP0000000001" }, Bvids(_db.ListVideosByUpMid(777)));
    }

    [Fact]
    public void 别人的私有稿在这页等于不存在()
    {
        var owner = Seat(1);
        var other = Seat(2);
        Arrive("BVUP0000000004", 777);
        Arrive("BVUP0000000005", 777, visibility: VideoVisibility.Private, submitter: owner);

        Assert.Equal(new[] { "BVUP0000000004" }, Bvids(_db.ListVideosByUpMid(777)));                    // 访客尺子
        Assert.Equal(new[] { "BVUP0000000004" }, Bvids(_db.ListVideosByUpMid(777, VisibilityScope.Of(other)))); // 别的席位
        Assert.Equal(new[] { "BVUP0000000004", "BVUP0000000005" },
            Bvids(_db.ListVideosByUpMid(777, VisibilityScope.Of(owner))).OrderBy(x => x).ToArray());    // 本人
        Assert.Equal(2, _db.ListVideosByUpMid(777, VisibilityScope.Everything).Count);                   // 站长全见
    }

    [Fact]
    public void 投稿时间倒序_没抽到时间的垫到最后()
    {
        Arrive("BVOLDEST", 777, publishedAtUtc: "2026-01-01T00:00:00.0000000+00:00", publishText: "2026-01-01 08:00:00");
        Arrive("BVNEWEST", 777, publishedAtUtc: "2026-03-01T00:00:00.0000000+00:00", publishText: "2026-03-01 08:00:00");
        Arrive("BVMIDDLE", 777, publishedAtUtc: "2026-02-01T00:00:00.0000000+00:00", publishText: "2026-02-01 08:00:00");
        Arrive("BVNOPUB", 777, publishText: null); // 两种投稿时间都没有：不能冒充新作打头

        Assert.Equal(new[] { "BVNEWEST", "BVMIDDLE", "BVOLDEST", "BVNOPUB" }, Bvids(_db.ListVideosByUpMid(777)));
    }

    [Fact]
    public void 投稿时间只住在publish_text里时也按它排_与ISO那一栏同一把尺()
    {
        // 库里 156 条到货稿只有 5 条带 published_at_utc：光拿那一列排就是入站序（站长看出的正是这个）。
        // publish_text 是 B 站本地时间（比 UTC 快 8 小时），得减掉 8 小时才能与 ISO 那一栏同台比。
        Arrive("BVTEXTNEW", 777, publishText: "2026-03-01 12:00:00");        // epoch 2026-03-01 04:00Z
        Arrive("BVTEXTOLD", 777, publishText: "2024-01-01 12:00:00");        // epoch 2023-12-31 20:00Z
        Arrive("BVISO", 777, publishedAtUtc: "2025-06-01T04:00:00.0000000+00:00", publishText: null);
        Arrive("BVISOBEATS", 777, publishedAtUtc: "2026-01-01T00:00:00.0000000+00:00", publishText: "2099-01-01 00:00:00");
        Arrive("BVTAIL", 777, publishText: "");                              // 空串也算没有

        Assert.Equal(new[] { "BVTEXTNEW", "BVISOBEATS", "BVISO", "BVTEXTOLD", "BVTAIL" }, Bvids(_db.ListVideosByUpMid(777)));
    }

    [Fact]
    public void 列形状对号入座_与ListVideos共用那张读法()
    {
        // ReadEntries 的列下标是写死的：这条钉的是「UP 空间那条 SELECT 少写/错写一列时，
        // 别把 UP 名显示到时长栏、别把弹幕数当体积」。造真数据不造 artifacts/previews，
        // 于是 LocalCover 与 PreviewUrl 必须是 null —— 串栏时它们会读到别的列。
        Arrive("BVUP0000000009", 777, upName: "阿婆主", publishedAtUtc: "2026-04-01T08:00:00.0000000+00:00");

        var row = Assert.Single(_db.ListVideosByUpMid(777));
        Assert.Equal("标题-BVUP0000000009", row.Title);
        Assert.Equal("阿婆主", row.UpName);
        Assert.Equal(777, row.UpMid); // 卡上的 UP 名要点进 UP 空间，就靠这一栏
        Assert.Equal("1.2万", row.ViewCountText);
        Assert.Equal("12:34", row.DurationText);
        Assert.Equal("2026-05-20 18:00", row.PublishText);
        Assert.Equal("2026-04-01T08:00:00.0000000+00:00", row.PublishedAtUtc);
        Assert.Equal(VideoVisibility.Public, row.Visibility);
        Assert.Null(row.LocalCover);
        Assert.Null(row.PreviewUrl);
        Assert.Null(row.DanmakuCount);
        Assert.Null(row.LastError);
    }

    [Fact]
    public void 名片快照取最新非空_半截档案不许埋掉已有的脸()
    {
        var elder = Arrive("BVUP0000000010", 888);
        _db.SetUpInfo(elder, "/avatars/888.jpg", "老签名", 12345);
        Arrive("BVUP0000000011", 888, upName: "改了名"); // 最新这条只落了名字，档案半截

        var up = _db.GetUpProfileByMid(888);
        Assert.NotNull(up);
        Assert.Equal(888, up!.Mid);
        Assert.Equal("改了名", up.Name);            // 名字认最新那条
        Assert.Equal("/avatars/888.jpg", up.Face);  // 脸不能被半截档案埋掉
        Assert.Equal("老签名", up.Sign);
        Assert.Equal(12345, up.Fans);
    }

    [Fact]
    public void 站内没收过这位_清单空表而快照null()
    {
        Arrive("BVUP0000000012", 777);

        Assert.Empty(_db.ListVideosByUpMid(424242));   // 空手而归是常态，不该挨一脚 404
        Assert.Null(_db.GetUpProfileByMid(424242));    // 页头据此知道「分站从没收过这位」
    }
}
