using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// UP 空间页头的认证栏（<c>up_official</c>）在 DB 层的口径：
/// ① 与名片另四栏同走「该 mid 最新一条非空」—— 认证是另一次出网采的，不许被半截档案埋掉；
/// ② 「整位都没档案」的判据仍只看原四栏 —— 无认证的人本来就没有这一栏，不该因此把有名片的整页打回 notfound；
/// ③ 存量回填（11-05）：脸早已本地化的老 UP 走不进补采轮，靠开局一次性补一轮 ——
///    哨兵（settings 的 <c>up-official-migrated</c>）盖过就不再跑；回填写口是只动一列的窄口，
///    同一位的所有行全量刷新，而 face/sign/fans 必须原值不动（<c>SetUpInfo</c> 无这条保证），
///    且这一列只补不抹（COALESCE）——空手不许把已到位的认证写成 NULL。
/// </summary>
public sealed class UpHeadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public UpHeadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-up-official-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>造一条该 UP 的到货稿（页头只读 video_meta，但按 UpSpaceTests 的构造习惯照样走到货那一步）。</summary>
    private long Arrive(string bvid, long upMid, string upName = "测试UP")
    {
        var outcome = _db.AddVideosBulk(new[]
        {
            new NewVideo($"https://www.bilibili.com/video/{bvid}", bvid, "标题-" + bvid, null, null, VideoVisibility.Public),
        });
        var id = outcome.InsertedIds[0];
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft(upName, upMid, "音乐>翻唱", "2026-05-20 18:00", "简介", "1.2万"),
            new[] { new PageDraft("task-" + bvid, 1, "P1", 1000 + id, "12:34", "videos") });
        _db.MarkArrived(id);
        return id;
    }

    [Fact]
    public void 名片带上认证()
    {
        var id = Arrive("BVHD0000000001", 6001);
        _db.SetUpInfo(id, "/avatars/6001.jpg", "签名", 4321, "bilibili直播高能主播");

        var up = _db.GetUpProfileByMid(6001);
        Assert.NotNull(up);
        Assert.Equal("bilibili直播高能主播", up!.Official);
        // 原四栏一个不许被新栏碰坏
        Assert.Equal("/avatars/6001.jpg", up.Face);
        Assert.Equal("签名", up.Sign);
        Assert.Equal(4321, up.Fans);
    }

    [Fact]
    public void 认证取最新非空_最新行空用旧行的()
    {
        var elder = Arrive("BVHD0000000004", 6004);
        _db.SetUpInfo(elder, "/avatars/6004-old.jpg", "老签名", 111, "bilibili直播高能主播");

        // 最新这条只补采到脸：认证那栏失败销账（空串）—— 不许埋掉旧行的值。
        var youngest = Arrive("BVHD0000000005", 6004, upName: "改了名");
        _db.SetUpInfo(youngest, "/avatars/6004-new.jpg", null, null, string.Empty);

        var up = _db.GetUpProfileByMid(6004);
        Assert.NotNull(up);
        Assert.Equal("改了名", up!.Name);            // 名字仍认最新一行
        Assert.Equal("/avatars/6004-new.jpg", up.Face);
        Assert.Equal("bilibili直播高能主播", up.Official); // 认证从旧行来
    }

    /// <summary>逐行读库验「窄口只动一列」：读侧的「最新非空」会把两行的值揉到一起，分不出谁是谁的。</summary>
    private (string? Official, string? Face, string? Sign, long? Fans) ReadRowColumns(long videoId)
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT up_official, up_face, up_sign, up_fans FROM video_meta WHERE video_id = $id;";
        cmd.Parameters.AddWithValue("$id", videoId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"video_meta 里没有这一行：{videoId}");
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3));
    }

    [Fact]
    public void 老库残留头图列_开站拍掉()
    {
        // 头图功能于 11-06 整体废除：老库里那一列连同旧值一并 DROP，不能只停写不停读（留着列就是没人读的脏数据）。
        // 幂等：先手工把旧列与旧值装回去，再走一遍 Initialize；新库重跑同样不响（EnsureColumn/DropColumn 都先查 PRAGMA）。
        var id = Arrive("BVHD0000000008", 6008);
        using (var legacy = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}"))
        {
            legacy.Open();
            using var add = legacy.CreateCommand();
            add.CommandText = "ALTER TABLE video_meta ADD COLUMN up_head TEXT;";
            add.ExecuteNonQuery();
            using var fill = legacy.CreateCommand();
            fill.CommandText = "UPDATE video_meta SET up_head = '/heads/gone.jpg' WHERE video_id = $id;";
            fill.Parameters.AddWithValue("$id", id);
            fill.ExecuteNonQuery();
        }

        _db.Initialize();

        Assert.False(HasColumn("up_head"));
        Assert.True(HasColumn("up_official")); // 认证是保留资产，拍头图不许误伤它
    }

    /// <summary>直接读库的 PRAGMA table_info：验「列在不在」不能拿读侧快照当凭据（它已经不读这列了）。</summary>
    private bool HasColumn(string column)
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "station.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(video_meta);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void 存量回填_哨兵盖过就不再跑()
    {
        // 判据抽在 StatusSyncWorker.NeedsUpOfficialBackfill（settings 哨兵，照 m54-migrated 先例）：
        // 没盖 → 该跑；盖了（值是跑完那轮的时间戳）→ 永不再扰 B 站。
        Assert.True(StatusSyncWorker.NeedsUpOfficialBackfill(_db));

        _db.SetSetting(StatusSyncWorker.UpOfficialMigratedKey, DateTimeOffset.UtcNow.ToString("O"));

        Assert.False(StatusSyncWorker.NeedsUpOfficialBackfill(_db));
    }

    [Fact]
    public void 存量回填_旧哨兵搬到新哨兵_老站不重跑()
    {
        // 头图时代那块碑（up-head-official-migrated）是头图与认证同轮盖的：老站升级后不能因为改名重跑一轮扰 B 站。
        // 判缺那一步顺手把值搬到新碑并拆旧碑 —— 值本身要照搬（那是当年跑完的时间戳，不是新盖的）。
        const string legacyValue = "2026-11-05T18:00:00.0000000+00:00";
        _db.SetSetting(StatusSyncWorker.LegacyUpOfficialMigratedKey, legacyValue);

        Assert.False(StatusSyncWorker.NeedsUpOfficialBackfill(_db));
        Assert.Equal(legacyValue, _db.GetSetting(StatusSyncWorker.UpOfficialMigratedKey));
        Assert.Null(_db.GetSetting(StatusSyncWorker.LegacyUpOfficialMigratedKey));

        // 搬完再问一次：判据已经认新碑，旧碑没了也不该翻回「该跑」。
        Assert.False(StatusSyncWorker.NeedsUpOfficialBackfill(_db));
    }

    [Fact]
    public void 存量回填_窄口只动一列_名片四栏原值不动()
    {
        // 窄口代替 SetUpInfo 的理由就钉在这条：SetUpInfo 无条件写名片那几列，回填手边没 face/sign/fans 的现值，
        // 拿 null 去传就是把老 UP 的名片抹平。这里先拿 SetUpInfo 全量落旧值，再跑窄口，逐行对账。
        var elder = Arrive("BVBF0000000001", 7001);
        var youngest = Arrive("BVBF0000000002", 7001, upName: "改了名");
        _db.SetUpInfo(elder, "/avatars/7001-old.jpg", "老签名", 999, "旧认证");
        _db.SetUpInfo(youngest, "/avatars/7001-new.jpg", "新签名", 1000, null);

        var rows = _db.SetUpOfficial(7001, "bilibili直播高能主播");

        Assert.Equal(2, rows); // 同一位的所有行全量刷新，不只最新那一行
        Assert.Equal(("bilibili直播高能主播", "/avatars/7001-old.jpg", "老签名", 999L), ReadRowColumns(elder));
        Assert.Equal(("bilibili直播高能主播", "/avatars/7001-new.jpg", "新签名", 1000L), ReadRowColumns(youngest));
    }

    [Fact]
    public void 窄口空手_不许抹已到位的认证()
    {
        // 11-05 深夜交叉验证的 P0：旧窄口一根 UPDATE 直写，空手就把已到位的现值写成 NULL，且是全行刷——
        // 读侧「最新非空」兑底只救得了单行被抹救不了全行，哨兵后不可自愈。COALESCE 只补不抹后：空手的原值必须原地不动。
        Arrive("BVBF0000000007", 7007);
        _db.SetUpOfficial(7007, "bilibili直播高能主播");

        var rows = _db.SetUpOfficial(7007, null);

        Assert.Equal(1, rows); // 命中即计行（同值/空手也算），「补了 X 位」口径不变
        Assert.Equal("bilibili直播高能主播", _db.GetUpProfileByMid(7007)!.Official);
    }

    [Fact]
    public void 存量回填_欠账名单去重且只报缺认证的()
    {
        // 7101：两行都没认证 → 名单里只出现一次（逐位出网，不能按行重复扰 B 站）。
        Arrive("BVBF0000000003", 7101);
        Arrive("BVBF0000000004", 7101);
        // 7102：认证已落 → 不进名单。
        var done = Arrive("BVBF0000000005", 7102);
        _db.SetUpInfo(done, "/avatars/7102.jpg", "签名", 7, "高能主播");
        // 7103：认证空（无认证的人采到 null）→ 也算欠账，照实报（收不收敛由哨兵管）。
        var half = Arrive("BVBF0000000006", 7103);
        _db.SetUpInfo(half, "/avatars/7103.jpg", "签名", 3, null);

        // 新稿的 UP 先补（ORDER BY MAX(video_id) DESC）：7103 入站晚于 7101。
        Assert.Equal(new[] { 7103L, 7101L }, _db.ListMidsLackingOfficial());
    }

    [Fact]
    public void 只有认证没有四栏_不算有档案()
    {
        // 判据维持原四栏口径：official 不参与「整位都没档案」的判定。
        // 这行有 up_mid（所以查得到这位），但档案四栏全空 —— 页头要退化成「UP 主 {mid}」而不是整页 notfound。
        var id = Arrive("BVHD0000000006", 6006, upName: "");
        _db.SetUpInfo(id, null, null, null, "高能主播");

        Assert.Null(_db.GetUpProfileByMid(6006));
    }

    [Theory]
    [InlineData(1, true)]  // 有名片：盖——无认证没货是常态，不盖就是每次开机重扰 B 站。
    [InlineData(5, true)]  // 多几张同理，真值表另一头有个样本才不致把 > 写成 == 1。
    [InlineData(0, false)] // 整轮零张名片：card 接口不认这趟，盖了是把「没采到」焊成「干完活」。
    public void 哨兵盖戳判据_真值表(int attempted, bool expected)
    {
        Assert.Equal(expected, StatusSyncWorker.ShouldStampOfficialSentinel(attempted));
    }
}
