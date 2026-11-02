using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 发布前整改的三张回归照（大小写绕过 + 站长口令）。本仓无 HTTP harness，钉的是**门判据本身**：
/// 判据为真 = 前置中间件对匿名一律回 401（Program.cs 的 member-required / owner-login-required 那两笔）。
/// ① 两张门点名 "/api/" 前缀必须不吃大小写：Minimal API 的路由匹配默认忽略大小写，门闸按 Ordinal 认字面时
///    /API/admin/members 在路由上照样命中端点、在门闸上却被当成「不是站长事务」——活体实测匿名回 200 拿到成员名单。
///    同一把尺子也管会员门：/API/videos/9/danmaku 这类换体写法不许穿到访客那侧。
/// ② 私有稿件的媒体件从小写路径进来也必须挡（回真即静态中间件改写成 404）：静态区落在 NTFS / Docker 卷这类
///    大小写不敏感的文件系统上时，/media/bv….mp4 照样从盘上取得出文件，而台账按字面比不中就是 fail-open 放行。
/// ③ 站长口令：settings 里存量那枚无盐 SHA256 验得过、验对那一刻透明换铸 PBKDF2（现网真站点不能被加固锁死）；
///    新上的锁至少 <see cref="AdminGate.MinPasswordLength"/> 位。
/// </summary>
public sealed class GateCaseBypassTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DataPaths _paths;
    private readonly StationDb _db;
    private readonly AdminGate _gate;

    public GateCaseBypassTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-gate-case-" + Guid.NewGuid().ToString("N"));
        var videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(videosDir);
        _paths = new DataPaths(_tempDir, videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(_paths);
        _db.Initialize();
        _gate = new AdminGate(_db, NullLogger<AdminGate>.Instance);
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

    private static HttpRequest Request(string method, string path) => new DefaultHttpContext
    {
        Request = { Method = method, Path = path },
    }.Request;

    // ─── ① 换体路径的两张门归属 ────────────────────────────────────

    [Theory]
    // 站长门：整段 /api/admin/ 前缀换体大小写照样在门里（活体漏过的那一条排在最前）。
    [InlineData("GET", "/API/admin/members", true, false)]
    [InlineData("GET", "/Api/Admin/Members", true, false)]
    [InlineData("POST", "/API/admin/export", true, false)]
    [InlineData("POST", "/API/admin/zones/set", true, false)]
    [InlineData("GET", "/API/ADMIN/stats/", true, false)] // 换体 + 尾斜杠：两把穿门的钥匙叠在一起
    [InlineData("GET", "/API/settings", true, false)]
    [InlineData("GET", "/API/Stats/Collect", true, false)] // 精确点名型的站长情报：漏一个字母就漏一整口
    [InlineData("GET", "/API/stats/preview", true, false)]
    [InlineData("GET", "/API/Station/throttle", true, false)]
    [InlineData("GET", "/API/Downloads/9", true, false)]
    [InlineData("POST", "/API/videos/123/revive", true, false)] // 站长专属的 POST 也要吃换体
    [InlineData("DELETE", "/API/videos/123/codecs/hevc", true, false)]
    // 会员门：放映厅、投稿与配套设施，换体一律算会员事务（匿名据此 401）。
    [InlineData("GET", "/API/watch/BV1x411c7mD", false, true)]
    [InlineData("GET", "/API/VIDEOS/123/danmaku", false, true)]
    [InlineData("GET", "/API/videos/123/related", false, true)]
    [InlineData("GET", "/API/Videos/123/download", false, true)]
    [InlineData("POST", "/API/videos", false, true)] // 投稿那一段（含 POST 的换体形状）
    [InlineData("POST", "/API/VIDEOS/123/danmaku-refresh", false, true)]
    [InlineData("POST", "/API/videos/123/comments/subs", false, true)]
    [InlineData("DELETE", "/API/videos/123", false, true)] // 投稿人自管删稿
    [InlineData("PATCH", "/API/Videos/123", false, true)] // 稿件设置
    [InlineData("GET", "/API/Collections/9", false, true)]
    [InlineData("GET", "/API/Dynamics/targets", false, true)]
    [InlineData("GET", "/API/UP/556980/videos", false, true)]
    // 公开门面不因换体被误封：这些在两处都该是假（访客照样能逛），门不能一改大小写就把首页一起锁了。
    [InlineData("GET", "/API/zones", false, false)]
    [InlineData("GET", "/API/videos", false, false)]
    [InlineData("GET", "/API/collections", false, false)]
    [InlineData("GET", "/API/Station/profile", false, false)]
    [InlineData("GET", "/", false, false)]
    [InlineData("GET", "/MEDIA/BV1x411c7mD.mp4", false, false)]
    public void 换体路径的两张门归属(string method, string path, bool expectAdmin, bool expectMember)
    {
        var request = Request(method, path);
        Assert.Equal(expectAdmin, _gate.IsAdminRequest(request));
        Assert.Equal(expectMember, MemberGate.RequiresMember(request));
    }

    [Fact]
    public void 活体那条_匿名拿大写路径读成员名单_必须被站长门判成站长事务()
    {
        // 整改前这一格回 false（"/api/" 按 Ordinal 点名）：路由照样命中端点，于是匿名 200 拿到成员名单。
        Assert.True(_gate.IsAdminRequest(Request("GET", "/API/admin/members")));
        // 站长门自己判真，中间件那一步与是否盖章无关（Program.cs 的 admin 闸不套 /api/ 前缀判据）。
        Assert.True(_gate.IsAdminRequest(Request("POST", "/API/admin/export")));
    }

    // ─── ② 私有稿件的小写 media 路径 ──────────────────────────────

    [Fact]
    public void 私有稿的媒体件_换体路径必须挡住_台账不认的件照旧放行()
    {
        var hidden = Arrive("BVcase000001", "藏的一首", visibility: VideoVisibility.Private); // BV + 10 位 = 定长 12
        _db.RegisterCoverArtifact(hidden, "藏/封面Case.JPG"); // artifacts 腿
        _db.RegisterPreviewArrived(hidden, 1, "BVcase000001.MP4", 320, 180, 6.5, 20480); // previews 腿
        Assert.True(_db.AddCodecVersion(hidden, 1, "hevc")); // video_codecs 腿
        var codecRow = Assert.Single(_db.GetCodecVersions(hidden), r => r.Codec == "hevc");
        _db.MarkCodecArrived(codecRow.Id, "藏/1-正片-1080P-HEVC.MP4");

        // 三条腿各钉一对：正写命中（老语义没被 NOCASE 那轮带偏）+ 换体命中（NTFS/Docker 卷上真取得出文件的那一条）。
        // 断言为真 = 静态中间件把响应改写成 404（与半成品闸同一副面孔，不说「私有」二字）。
        foreach (var (prefix, rel) in new[]
        {
            ("/media/", "藏/封面Case.JPG"),
            ("/media/", "藏/封面case.jpg"),
            ("/media/", "藏\\封面CASE.jpg"), // 反斜杠与全大写也一并钉住
            ("/previews/", "BVcase000001.MP4"),
            ("/previews/", "bvcase000001.mp4"),
            ("/media/", "藏/1-正片-1080P-HEVC.MP4"),
            ("/media/", "藏/1-正片-1080p-hevc.mp4"),
        })
        {
            Assert.True(PrivateMediaBlocked(prefix + rel), $"{prefix}{rel} 该被挡（私有稿的件）");
        }

        // 台账没行、只剩文件名里那枚 BV 号可拄的那条腿：小写 bv 打头也要认得（token 判据原本是 Ordinal）。
        // 这里的 BV 号必须是定长 12（BV + 10）：差一位 BvidInFileName 就当不是号（宁缺毋假），那不是闸漏了而是测试造错了号。
        var orphan = Arrive("BVcase000002", "另一首私货", visibility: VideoVisibility.Private);
        Assert.Equal(orphan, _db.GetVideoIdByMediaRel("杂项/随手拍-bvcase000002.mp4"));
        Assert.True(PrivateMediaBlocked("/media/杂项/随手拍-BVCASE000002.mp4"));
        // 不合规的那枚（差一位）不许反查：免得把标题里的什么「BV1」当号挡了不相干的稿子
        Assert.Null(_db.GetVideoIdByMediaRel("杂项/随手拍-bvcase00002.mp4"));

        // 反向不许过封：① 台账根本不管的件（头像那类）必须放行，否则全站公共件一起变黑；
        Assert.False(PrivateMediaBlocked("/media/avatars/950001.JPG"));
        Assert.False(PrivateMediaBlocked("/media/随便/没这个件.mp4"));
        // ② 公开的稿件不挡（NOCASE 腿不得把公开件也一块儿挡了）；
        var open = Arrive("BVcase000003", "人人听得的一首");
        _db.RegisterCoverArtifact(open, "公开/封面.JPG");
        Assert.False(PrivateMediaBlocked("/media/公开/封面.JPG"));
        Assert.False(PrivateMediaBlocked("/media/公开/封面.jpg"));
        // ③ 私有稿的投稿人本人与站长照样取回自己的件（换体路径也不例外）。
        var owner = Member(Seat("站长"), MemberRoles.Owner);
        Assert.True(PrivateMediaBlocked("/media/藏/封面case.jpg", member: Member(Seat("路人"), MemberRoles.Member)));
        Assert.False(PrivateMediaBlocked("/media/藏/封面case.jpg", member: owner));
    }

    [Fact]
    public void 私有稿转私后_小写路径的可见性反查与正写同一答案()
    {
        // 静态闸与「查可见性」共用同一认法（同一张嘴）：两条腿的答案必须一致，否则一处挡一处放。
        var hidden = Arrive("BVcase000010", "藏的一首", visibility: VideoVisibility.Private);
        _db.RegisterCoverArtifact(hidden, "藏/封面.JPG");

        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/封面.JPG"));
        Assert.Equal(VideoVisibility.Private, _db.GetVideoVisibilityByMediaRel("藏/封面.jpg"));
        Assert.Equal(_db.GetVideoIdByMediaRel("藏/封面.JPG"), _db.GetVideoIdByMediaRel("藏/封面.jpg"));

        // 认不出的件仍回 null（调用方必须放行，不许拿 null 当私有）
        Assert.Null(_db.GetVideoVisibilityByMediaRel("avatars/950002.JPG"));
        Assert.Null(_db.GetVideoIdByMediaRel("   "));
    }

    // ─── ③ 站长口令：存量兼容 + 透明升级 + 长度下限 ────────────────

    [Fact]
    public void 存量无盐SHA256口令_验得过_验对那一刻换铸PBKDF2()
    {
        const string legacy = "站长当年的四位口令";
        _db.SetSetting("admin-key", AdminGate.Hash(legacy)); // 老写法留下的存形：无盐 SHA256 的 64 位 hex

        var token = _gate.TryUnlock(legacy, "10.0.0.1");
        Assert.NotNull(token);
        Assert.True(_gate.HasValidSession(RequestWithCookie(token!)));

        // 换铸：同一条 KV 从 64 位 hex 变成 pbkdf2$210000$salt$hash，且新形验得过同一口令
        var stored = _db.GetSetting("admin-key");
        Assert.StartsWith("pbkdf2$", stored, StringComparison.Ordinal);
        Assert.True(MemberPasswords.Verify(stored, legacy));
        Assert.NotEqual(AdminGate.Hash(legacy), stored);

        // 换铸后照常解锁（现网用户不会为这次加固被锁在门外）
        Assert.NotNull(_gate.TryUnlock(legacy, "10.0.0.2"));
        // 候选先 Trim 再验（与挂牌同口径）：当年落库的就是 Trim 过的串，手机上多碰一个空格的本人不该被挡在门外
        Assert.NotNull(_gate.TryUnlock($"  {legacy} ", "10.0.0.3"));
    }

    [Fact]
    public void 存量哈希写成大写hex也认_错口令不发会话也不动存形()
    {
        const string legacy = "另一条旧口令";
        _db.SetSetting("admin-key", AdminGate.Hash(legacy).ToUpperInvariant()); // 手抄/迁移留下的 uppercase hex

        Assert.NotNull(_gate.TryUnlock(legacy, "10.0.1.1"));

        _db.SetSetting("admin-key", AdminGate.Hash(legacy));
        var before = _db.GetSetting("admin-key");
        Assert.Null(_gate.TryUnlock("差一位都不给", "10.0.1.2"));
        Assert.Null(_gate.TryUnlock(null, "10.0.1.3"));
        Assert.Null(_gate.TryUnlock("", "10.0.1.4"));
        // 没验对就不许顺手改铸（否则就成了「猜一次改一次」）
        Assert.Equal(before, _db.GetSetting("admin-key"));
    }

    [Fact]
    public void 新铸口令走PBKDF2_加盐两次不同_旧算法对不上新存形()
    {
        _gate.SetPassword("站长新口令-八位起步");

        var stored = _db.GetSetting("admin-key");
        Assert.StartsWith("pbkdf2$", stored, StringComparison.Ordinal);
        // 同一口令两次铸出的盐不同（无盐 SHA256 才会一模一样）
        Assert.NotEqual(MemberPasswords.Hash("站长新口令-八位起步"), MemberPasswords.Hash("站长新口令-八位起步"));
        // 老那枚无盐哈希绝不能再当钥匙
        Assert.NotEqual(AdminGate.Hash("站长新口令-八位起步"), stored);
        Assert.NotNull(_gate.TryUnlock("站长新口令-八位起步", "10.0.2.1"));
        // 候选先 Trim 再验（与 SetPassword 同口径）：手机上多带一个空格不该把本人挡在门外
        Assert.NotNull(_gate.TryUnlock("  站长新口令-八位起步 ", "10.0.2.2"));
        // Trim 后为空一律拒：空格不算口令，也不许进比对
        Assert.Null(_gate.TryUnlock("   ", "10.0.2.3"));
    }

    [Fact]
    public void 口令长度下限八位_四位不给上锁()
    {
        Assert.Equal(8, AdminGate.MinPasswordLength);
        Assert.False(AdminGate.MeetsPasswordPolicy(null));
        Assert.False(AdminGate.MeetsPasswordPolicy(""));
        Assert.False(AdminGate.MeetsPasswordPolicy("   "));
        Assert.False(AdminGate.MeetsPasswordPolicy("1234567"));
        Assert.False(AdminGate.MeetsPasswordPolicy(" 1234567 ")); // 量的是 Trim 后的长度
        Assert.True(AdminGate.MeetsPasswordPolicy("12345678"));

        Assert.Throws<ArgumentException>(() => _gate.SetPassword("1234567"));
        Assert.False(_gate.TrySetPassword("1234567", out var error));
        Assert.Contains("8", error, StringComparison.Ordinal);
        Assert.Null(_db.GetSetting("admin-key")); // 没上成锁：不许留半把锁

        Assert.True(_gate.TrySetPassword("12345678", out error));
        Assert.Null(error);
        Assert.NotNull(_gate.TryUnlock("12345678", "10.0.3.1"));
    }

    [Fact]
    public void 没设口令时敞开_试锁一律发不出会话()
    {
        Assert.False(_gate.IsLocked);
        Assert.Null(_gate.TryUnlock("任何东西", "10.0.4.1"));
        Assert.False(_gate.HasValidSession(RequestWithCookie("随手编的令牌")));
    }

    // ─── 基建 ────────────────────────────────────────────────────

    /// <summary>
    /// 与 Program.cs 的 <c>PrivateMediaBlocked</c> 同一判据链（剥前缀 → 解百分号 → 反斜杠归一 → 认稿件 id →
    /// 认得出才问 CanView，认不出即放行）。闸回真 = 静态中间件回 404。
    /// </summary>
    private bool PrivateMediaBlocked(string path, StationMember? member = null)
    {
        string rel;
        if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            rel = path["/media/".Length..];
        }
        else if (path.StartsWith("/previews/", StringComparison.OrdinalIgnoreCase))
        {
            rel = path["/previews/".Length..];
        }
        else
        {
            return false;
        }

        rel = Uri.UnescapeDataString(rel).Replace('\\', '/');
        if (_db.GetVideoIdByMediaRel(rel) is not { } videoId)
        {
            return false; // 台账不管这个件（头像/表情那类）：一律放行，不许拿 null 当私有
        }

        return !ViewerGate.CanView(_db, videoId, member);
    }

    /// <summary>造一条到站的货：可见性与投稿人当场定（写侧只有 AddVideosBulk 这一支能带私有进来）。</summary>
    private long Arrive(string bvid, string? title = null, long? submitter = null, VideoVisibility visibility = VideoVisibility.Public)
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

    /// <summary>造一个在席席位。</summary>
    private long Seat(string name)
    {
        var (id, _) = _db.CreateMemberInvite(name + "的席位");
        Assert.NotNull(_db.BindMember(id, 950_000 + id, name, null));
        return id;
    }

    /// <summary>拿席位 id 拼个身份（看客钥匙只吃 Id 与 Role，其余字段无关）。</summary>
    private static StationMember Member(long id, string role) =>
        new(id, 1000 + id, "席位" + id, null, null, MemberStatus.Active, role, DateTimeOffset.UnixEpoch.ToString("O"), null, null);

    /// <summary>带 lbs_admin cookie 的请求（站长会话的验票口）。cookie 得装在 Cookie 头里，不是把令牌当成头名。</summary>
    private static HttpRequest RequestWithCookie(string token)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/api/admin/stats";
        http.Request.Headers["Cookie"] = $"{AdminGate.Cookie}={token}"; // HttpRequest.Cookies 只解 Cookie 头
        return http.Request;
    }
}
