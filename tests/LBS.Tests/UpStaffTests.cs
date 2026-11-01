using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// 创作团队（联合创作稿的 B 站 staff 名单）归档：与互动数据同一次 view 采回，走 video_meta.staff_json 一列，
/// 读写共用 WatchStaffCodec 这一张嘴。按约只给新入站的稿采，存量老账一律 null、不回头补。
/// </summary>
public sealed class UpStaffTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public UpStaffTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-staff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
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
            // 临时目录留给操作系统
        }
    }

    /// <summary>造一条有档案的稿件（联合创作与否由 SetVideoStaff 决定）。</summary>
    private long Seed(string bvid)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("七禾いえ", 1802654492, "动画>综合", "2026-10-22 15:00:16", "简介", "4707"),
            Array.Empty<PageDraft>());
        return id;
    }

    [Fact]
    public void 联合创作名单_归档后进观看数据()
    {
        var id = Seed("BVSTAFF00001");
        _db.SetVideoStaff(id, WatchStaffCodec.ToJson(new[]
        {
            new WatchStaff(1802654492, "七禾いえ", "UP主", "/avatars/a.jpg"),
            new WatchStaff(8399355, "猫与深巷OvO", "作词", "https://i0.hdslb.com/bfs/face/b.jpg"),
        }));

        var data = _db.GetWatchData("BVSTAFF00001");
        Assert.NotNull(data);
        Assert.Equal(2, data.Staff?.Count);
        Assert.Equal("七禾いえ", data!.Staff![0].Name);
        Assert.Equal(8399355, data.Staff[1].Mid);
        Assert.Equal("作词", data.Staff[1].Role);
        Assert.Equal("/avatars/a.jpg", data.Staff[0].Face); // 本地件原样带回，读层不改写路径
    }

    [Fact]
    public void 没采过与采过没团队_一律null_不牵连别的字段()
    {
        Seed("BVSTAFF00002"); // 压根没采过（存量老账就是这个形态）
        var probed = Seed("BVSTAFF00003");
        _db.SetVideoStaff(probed, WatchStaffCodec.ToJson(Array.Empty<WatchStaff>())); // 空列表落库即 null

        Assert.Null(_db.GetWatchData("BVSTAFF00002")?.Staff);
        Assert.Null(_db.GetWatchData("BVSTAFF00003")?.Staff);
        Assert.Equal("七禾いえ", _db.GetWatchData("BVSTAFF00003")?.UpName);
    }

    [Fact]
    public void 缺角色的条目_读回null由前端决定显什么()
    {
        var id = Seed("BVSTAFF00004");
        _db.SetVideoStaff(id, """[{"mid":1,"name":"某成员","role":null,"face":null}]""");

        var staff = _db.GetWatchData("BVSTAFF00004")?.Staff;
        Assert.NotNull(staff);
        Assert.Single(staff!);
        Assert.Equal("某成员", staff![0].Name);
        Assert.Null(staff[0].Role);
        Assert.Null(staff[0].Face);
    }

    [Fact]
    public void 脏JSON_只当没团队_不作废整页观看数据()
    {
        var id = Seed("BVSTAFF00005");
        _db.SetVideoStaff(id, "not json at all");

        var data = _db.GetWatchData("BVSTAFF00005");
        Assert.NotNull(data);
        Assert.Null(data!.Staff);
        Assert.Equal("七禾いえ", data.UpName);
    }

    [Fact]
    public void 重提交_整行重开_团队跟着清零等待回炉()
    {
        // SaveSubmissionMetadata 是 DELETE + INSERT：销档重下必须把 staff 一起清掉，
        // 否则换库/换稿后会拿着上一世的名单显给人看。
        var id = Seed("BVSTAFF00006");
        _db.SetVideoStaff(id, WatchStaffCodec.ToJson(new[] { new WatchStaff(1, "上一世的人", "UP主", null) }));
        Assert.Single(_db.GetWatchData("BVSTAFF00006")!.Staff!);

        _db.SaveSubmissionMetadata(
            id,
            new VideoMetaDraft("七禾いえ", 1802654492, "动画>综合", "2026-10-22 15:00:16", "简介", "4707"),
            Array.Empty<PageDraft>());

        Assert.Null(_db.GetWatchData("BVSTAFF00006")?.Staff);
    }
}
