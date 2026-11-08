using LBS.Domain;
using LBS.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 加档版本的回收站（11-08 站长拍板「删掉的加档版本也进回收站，方便站长回滚」）。钉的是这几件事：
/// ① 删一版不再是「销账 + File.Delete」——行连快照、件连同名附属件一起封进 codec_recycle 与 data/codec_recycle，
///    原表干净（读侧那条不带状态过滤的装配 SELECT 因此不用加判据，也不会撞 UNIQUE 让同一版永远点不动）；
/// ② <b>主件保护</b>：加档行的 rel_path 指的正是那一 P 的 local_video_path 时整笔拒删
///    （TakeCodecVersionForDelete 原本连这个都不问，前端藏钮是唯一防线，删下去就是正片打空）；
/// ③ 附属件（弹幕 / 字幕 / 封面 / nfo）从没进过 artifacts 表，删一版必须按族一并带走，不留孤儿；
///    而<b>主件的附属件一个手指都不碰</b>（它们属于正片，不属于这一版）；
/// ④ 捐回四道判据一件都不能少：组在 → 片子还在 Arrived → 三元组没被新行占 → 件真在回收目录里。
///    最后那道与整片回收站刻意不同：整片宁可恢复出缺件的片子，一版加档缺件只会在播放器菜单上摆一档 404；
/// ⑤ 一次删除 = 一个组，捐回与销毁都按组走（点单是整片点的，捐回不该让站长按 N 次钮）；
/// ⑥ 删的时候正被占用就整笔作废，不留「账进了回收站、件还在原地」的半吊子。
/// 姿势与本仓其余库层测试一致：直驱库方法，本仓无 HTTP harness（两张门的归属矩阵在 SubmitterManageTests 钉）。
/// </summary>
public sealed class CodecRecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly string _recycleDir;
    private readonly string _dbPath;
    private readonly StationDb _db;

    public CodecRecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-codecrecycle-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        _recycleDir = Path.Combine(_tempDir, "codec_recycle");
        Directory.CreateDirectory(_videosDir);
        var paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _dbPath = paths.DatabaseFile;
        _db = new StationDb(paths);
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

    // ─── 夹具 ────────────────────────────────────────────────

    private long AddArrivedVideo(string bvid)
    {
        var id = _db.AddVideo($"https://www.bilibili.com/video/{bvid}", bvid).VideoId;
        Assert.True(_db.MarkArrived(id));
        return id;
    }

    /// <summary>摆一版已到货的加档：盘上写本体（可带附属件），账上落一行 Arrived。</summary>
    private long AddArrivedCodec(long videoId, int pageIdx, string codec, string rel, params string[] sidecarExts)
    {
        Assert.True(_db.AddCodecVersion(videoId, pageIdx, codec));
        WriteFile(rel, $"{codec}-P{pageIdx}-字节");
        foreach (var ext in sidecarExts)
        {
            WriteFile(rel[..^Path.GetExtension(rel).Length] + ext, $"{ext}内容");
        }

        var row = _db.GetCodecVersions(videoId).Single(r => r.PageIdx == pageIdx && r.Codec == codec);
        _db.MarkCodecArrived(row.Id, rel);
        return row.Id;
    }

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_videosDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private bool MainExists(string rel) => File.Exists(Path.Combine(_videosDir, rel.Replace('/', Path.DirectorySeparatorChar)));

    private bool StagedExists(string groupId, string rel) =>
        File.Exists(Path.Combine(_recycleDir, groupId, "files", rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>那一 P 的正片住在哪（pages.local_video_path）：主件保护那两条要手工摆它。</summary>
    private void SetPageMain(long videoId, int idx, string? rel)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM pages WHERE video_id = $v AND idx = $i;";
        find.Parameters.AddWithValue("$v", videoId);
        find.Parameters.AddWithValue("$i", idx);
        var pageRowId = find.ExecuteScalar();
        if (pageRowId is null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO pages (video_id, task_id, idx, title, cid, duration_text, base_path, local_video_path) " +
                "VALUES ($v, $t, $i, $title, NULL, NULL, $base, $rel);";
            insert.Parameters.AddWithValue("$v", videoId);
            insert.Parameters.AddWithValue("$t", $"task-{videoId}-{idx}");
            insert.Parameters.AddWithValue("$i", idx);
            insert.Parameters.AddWithValue("$title", $"第{idx}P");
            insert.Parameters.AddWithValue("$base", $"v/main.P{idx}");
            insert.Parameters.AddWithValue("$rel", (object?)rel ?? DBNull.Value);
            insert.ExecuteNonQuery();
            return;
        }

        using var set = connection.CreateCommand();
        set.CommandText = "UPDATE pages SET local_video_path = $rel WHERE id = $id;";
        set.Parameters.AddWithValue("$rel", (object?)rel ?? DBNull.Value);
        set.Parameters.AddWithValue("$id", pageRowId);
        set.ExecuteNonQuery();
    }

    /// <summary>把片子的状态改写（本仓没有「改状态」的公开库方法，捐回的四道判据靠它摆出「片子已不在架」那种现场）。</summary>
    private void SetVideoStatus(long videoId, string status)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", videoId);
        command.ExecuteNonQuery();
    }

    private static string Rel(string bvid, int pageIdx, string codec) => $"v/{bvid}.P{pageIdx}-{codec.ToUpperInvariant()}.mp4";

    // ─── ① 删：连行带件进回收站 ──────────────────────────────

    [Fact]
    public void 删一版_行与件一起进回收站_附属件不留孤儿()
    {
        var videoId = AddArrivedVideo("BV1recycle01");
        var rel = Rel("BV1recycle01", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel, ".ass", ".srt", ".nfo", ".Cover.jpg");

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Rows);
        Assert.Equal(5, outcome.Files); // 本体 + 弹幕 + 字幕 + nfo + 封面（Cover 也在那一族里）
        Assert.NotNull(outcome.GroupId);

        // 原表销账：货架上再没有这一版（读侧那条 SELECT 不带状态过滤，行在就列档）
        Assert.DoesNotContain(_db.GetCodecVersions(videoId), r => r.Codec == "hevc");

        // 台账落全：谁动的手、席号几号、进的是哪一组、件在不在
        var entry = Assert.Single(_db.ListCodecRecycle(videoId));
        Assert.Equal(outcome.GroupId, entry.GroupId);
        Assert.Equal(videoId, entry.VideoId);
        Assert.Equal("BV1recycle01", entry.Bvid);
        Assert.Equal(1, entry.PageIdx);
        Assert.Equal("hevc", entry.Codec);
        Assert.Equal(rel, entry.RelPath);
        Assert.True(entry.FileStored);
        Assert.True(entry.Bytes > 0);
        Assert.Equal("小牛", entry.DeletedBy);
        Assert.Equal(7, entry.DeletedByMemberId);
        Assert.Equal("member", entry.Source);
        Assert.True(entry.VideoInStation);

        // 件真搬走了：原地没了，回收目录里按同一相对路径躺着（同卷搬家，不复制）
        Assert.False(MainExists(rel));
        Assert.True(StagedExists(outcome.GroupId!, rel));
        Assert.True(StagedExists(outcome.GroupId!, rel[..^4] + ".ass"));
        Assert.True(StagedExists(outcome.GroupId!, rel[..^4] + ".Cover.jpg"));
    }

    [Fact]
    public void 主件的附属件一个手指都不碰()
    {
        var videoId = AddArrivedVideo("BV1recycle02");
        var main = "v/BV1recycle02.P1.mp4";
        WriteFile(main, "正片");
        WriteFile("v/BV1recycle02.P1.ass", "正片弹幕");
        SetPageMain(videoId, 1, main);
        var codecRel = Rel("BV1recycle02", 1, "avc");
        AddArrivedCodec(videoId, 1, "avc", codecRel, ".ass");

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "avc", 1, _videosDir, _recycleDir, "站长", 1, "admin");

        Assert.True(outcome.Ok);
        // 加档那一族的弹幕走了，正片那一族的弹幕原地不动：前缀扫盘只认 "<主干>*"，不许把正片家人一起端走
        Assert.True(MainExists("v/BV1recycle02.P1.ass"));
        Assert.False(MainExists("v/BV1recycle02.P1-AVC.ass"));
        Assert.True(MainExists(main));
    }

    [Fact]
    public void 删整版_跨分P一次成组_不相干的编码一根毫毛不动()
    {
        var videoId = AddArrivedVideo("BV1recycle03");
        for (var page = 1; page <= 3; page++)
        {
            AddArrivedCodec(videoId, page, "hevc", Rel("BV1recycle03", page, "hevc"));
        }

        AddArrivedCodec(videoId, 1, "avc", Rel("BV1recycle03", 1, "avc"));

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", null, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.True(outcome.Ok);
        Assert.Equal(3, outcome.Rows);
        var entries = _db.ListCodecRecycle(videoId);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal(outcome.GroupId, entry.GroupId)); // 一次删除 = 一组
        Assert.Single(_db.GetCodecVersions(videoId), r => r.Codec == "avc"); // 别的编码没被牵进去
    }

    // ─── ② 三道安全闸 ────────────────────────────────────────

    [Fact]
    public void 主件保护_rel_正是那一P的正片时整笔拒删()
    {
        var videoId = AddArrivedVideo("BV1recycle04");
        var main = "v/BV1recycle04.P1.mp4";
        WriteFile(main, "正片字节");
        SetPageMain(videoId, 1, main);
        // 账上把加档行指到正片同一个文件：这条以前一路开火，正片被打空、菜单上还挂着两档
        Assert.True(_db.AddCodecVersion(videoId, 1, "hevc"));
        var row = _db.GetCodecVersions(videoId).Single(r => r.Codec == "hevc");
        _db.MarkCodecArrived(row.Id, main);

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.False(outcome.Ok);
        Assert.Contains("主件", outcome.Message);
        Assert.True(MainExists(main)); // 正片一个字节没动
        Assert.Contains(_db.GetCodecVersions(videoId), r => r.Codec == "hevc"); // 账也没销
        Assert.Empty(_db.ListCodecRecycle(videoId)); // 也没留下半组孤儿台账
    }

    [Fact]
    public void 下载中的行拒删_要人先取消这次加档()
    {
        var videoId = AddArrivedVideo("BV1recycle05");
        Assert.True(_db.AddCodecVersion(videoId, 1, "hevc")); // 停在 Downloading

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.False(outcome.Ok);
        Assert.Contains("还在下载中", outcome.Message);
        Assert.Contains(_db.GetCodecVersions(videoId), r => r.Status == "Downloading");
    }

    [Fact]
    public void 查无此版_回那句可分状态码的拒词()
    {
        var videoId = AddArrivedVideo("BV1recycle06");

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.False(outcome.Ok);
        // 端点靠这句分 404 与 409：不能改字面，也不能换成两句同义的话
        Assert.Equal(StationDb.CodecRecycleMissingMessage, outcome.Message);
    }

    [Fact]
    public void 没落过盘的一版也收得进来_只是不带件()
    {
        var videoId = AddArrivedVideo("BV1recycle07");
        Assert.True(_db.AddCodecVersion(videoId, 1, "hevc"));
        var row = _db.GetCodecVersions(videoId).Single(r => r.Codec == "hevc");
        _db.MarkCodecArrived(row.Id, "v/从不存在的.P1-HEVC.mp4"); // 账上有、盘上没有（引擎报到货却没落盘）

        var outcome = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "站长", 1, "admin");

        Assert.True(outcome.Ok);
        Assert.Equal(0, outcome.Files);
        var entry = Assert.Single(_db.ListCodecRecycle(videoId));
        Assert.False(entry.FileStored); // 照实记：当初盘上就没这件

        // 捐回照样放行（四道判据只管 file_stored=1 的行），否则这种行永远躺在回收站里出不去
        var restored = _db.RestoreCodecGroup(outcome.GroupId!, _videosDir, _recycleDir);
        Assert.True(restored.Ok);
        Assert.Contains(_db.GetCodecVersions(videoId), r => r.Codec == "hevc");
    }

    // ─── ③ 捐回的四道判据 ────────────────────────────────────

    [Fact]
    public void 捐回_文件回原位_行连主键一起回来_回收站清空()
    {
        var videoId = AddArrivedVideo("BV1recycle08");
        var rel = Rel("BV1recycle08", 1, "hevc");
        var rowId = AddArrivedCodec(videoId, 1, "hevc", rel, ".ass");

        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");
        Assert.True(moved.Ok);

        var restored = _db.RestoreCodecGroup(moved.GroupId!, _videosDir, _recycleDir);

        Assert.True(restored.Ok);
        Assert.Equal(1, restored.Rows);
        Assert.Equal(2, restored.Files);
        // 文件回到原位（相对路径一字不改），回收目录里那一组整个抹掉
        Assert.True(MainExists(rel));
        Assert.True(MainExists(rel[..^4] + ".ass"));
        Assert.False(Directory.Exists(Path.Combine(_recycleDir, moved.GroupId!)));
        Assert.Empty(_db.ListCodecRecycle(videoId));
        // 连主键一起回来：同一版在引擎侧的账、举报单里的引用都不该因为一次删而改号
        Assert.Equal(rowId, Assert.Single(_db.GetCodecVersions(videoId), r => r.Codec == "hevc").Id);
    }

    [Fact]
    public void 捐回被拦_片子已经不在架()
    {
        var videoId = AddArrivedVideo("BV1recycle09");
        var rel = Rel("BV1recycle09", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel);
        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        SetVideoStatus(videoId, "Archived"); // 片子先下了架（库里 status 是 TEXT，与弹幕回收站同一把尺子：只认 'Arrived'）

        var restored = _db.RestoreCodecGroup(moved.GroupId!, _videosDir, _recycleDir);

        Assert.False(restored.Ok);
        Assert.Contains("不在架", restored.Message);
        Assert.False(MainExists(rel)); // 被拦下就是真没搬，不留「文件回来了账没回来」
        Assert.NotEmpty(_db.ListCodecRecycle(videoId)); // 组还完整躺着，等稿件先被捞回来
    }

    [Fact]
    public void 捐回被拦_那一版后来又被人点回来了()
    {
        var videoId = AddArrivedVideo("BV1recycle10");
        var rel = Rel("BV1recycle10", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel);
        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.True(_db.AddCodecVersion(videoId, 1, "hevc")); // 三元组又被占了（UNIQUE 只许一行）

        var restored = _db.RestoreCodecGroup(moved.GroupId!, _videosDir, _recycleDir);

        Assert.False(restored.Ok);
        Assert.Contains("点回来", restored.Message);
        Assert.Single(_db.GetCodecVersions(videoId), r => r.Codec == "hevc"); // 别让两条账抢同一个位置
    }

    [Fact]
    public void 捐回被拦_回收目录里找不到文件()
    {
        var videoId = AddArrivedVideo("BV1recycle11");
        var rel = Rel("BV1recycle11", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel);
        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "小牛", 7, "member");

        // 有人直接清了盘（危险区一键清空、手工删目录都算）：此时捐回去只是给播放器菜单摆一档 404
        Directory.Delete(Path.Combine(_recycleDir, moved.GroupId!), true);

        var restored = _db.RestoreCodecGroup(moved.GroupId!, _videosDir, _recycleDir);

        Assert.False(restored.Ok);
        Assert.Contains("找不到", restored.Message);
        Assert.Empty(_db.GetCodecVersions(videoId)); // 账也没塞回去
    }

    [Fact]
    public void 捐回不存在的组_回没有这一组()
    {
        var restored = _db.RestoreCodecGroup("从不存在的组", _videosDir, _recycleDir);

        Assert.False(restored.Ok);
        Assert.Contains("没有这一组", restored.Message);
    }

    // ─── ④ 销毁与清单 ────────────────────────────────────────

    [Fact]
    public void 销毁_台账与目录一起没了_且不可再捐()
    {
        var videoId = AddArrivedVideo("BV1recycle12");
        var rel = Rel("BV1recycle12", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel);
        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", 1, _videosDir, _recycleDir, "站长", 1, "admin");

        Assert.True(_db.PurgeCodecGroup(moved.GroupId!, _recycleDir));

        Assert.Empty(_db.ListCodecRecycle(videoId));
        Assert.Equal(0, _db.CountCodecRecycle());
        Assert.False(Directory.Exists(Path.Combine(_recycleDir, moved.GroupId!)));
        Assert.False(MainExists(rel)); // 真删，不是藏起来
        Assert.False(_db.PurgeCodecGroup(moved.GroupId!, _recycleDir)); // 第二次就没有这一组了
    }

    [Fact]
    public void 清单_按片子筛_不在架的标记出来给前端摆灰钮()
    {
        var keep = AddArrivedVideo("BV1recycle13");
        var gone = AddArrivedVideo("BV1recycle14");
        AddArrivedCodec(keep, 1, "hevc", Rel("BV1recycle13", 1, "hevc"));
        AddArrivedCodec(gone, 1, "hevc", Rel("BV1recycle14", 1, "hevc"));
        _db.MoveCodecVersionsToRecycle(keep, "hevc", null, _videosDir, _recycleDir, "小牛", 7, "member");
        _db.MoveCodecVersionsToRecycle(gone, "hevc", null, _videosDir, _recycleDir, "小牛", 7, "member");
        SetVideoStatus(gone, "Archived");

        Assert.Equal(2, _db.CountCodecRecycle());
        var one = Assert.Single(_db.ListCodecRecycle(keep)); // 按片子筛
        Assert.Equal("BV1recycle13", one.Bvid);
        Assert.True(one.VideoInStation);
        var other = Assert.Single(_db.ListCodecRecycle(gone));
        Assert.False(other.VideoInStation); // 前端据此摆灰钮并说明原因
    }

    [Fact]
    public void 计数只认自己的表_不跟整片回收站串账()
    {
        var videoId = AddArrivedVideo("BV1recycle15");
        var rel = Rel("BV1recycle15", 1, "hevc");
        AddArrivedCodec(videoId, 1, "hevc", rel);
        Assert.Equal(0, _db.CountCodecRecycle());

        var moved = _db.MoveCodecVersionsToRecycle(videoId, "hevc", null, _videosDir, _recycleDir, "小牛", 7, "member");

        Assert.Equal(1, _db.CountCodecRecycle());
        Assert.Empty(_db.ListRecycle()); // 加档件不进整片回收站（两座各管各的账）

        // 危险区「清空回收站」把 recycle 下所有子目录扫一遍删：加档件必须住在另一座，
        // 否则那一钮会把加档回收站的件拍干而台账还一行行躺着，从此账实对不上。
        Directory.CreateDirectory(Path.Combine(_tempDir, "recycle"));
        _db.PurgeAllRecycle(Path.Combine(_tempDir, "recycle"));
        Assert.Equal(1, _db.CountCodecRecycle());
        var still = Assert.Single(_db.ListCodecRecycle(videoId));
        Assert.True(still.FileStored); // 件还在原地，捐得回来

        // 但危险区那枚「一键清空分站」得连这座一起拍掉：台账销了而封存件还在盘上，就是下一笔没人认领的孤儿账。
        Assert.True(File.Exists(Path.Combine(_recycleDir, moved.GroupId!, "files", rel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(1, _db.PurgeAllCodecRecycle(_recycleDir));
        Assert.Equal(0, _db.CountCodecRecycle());
        Assert.False(Directory.Exists(Path.Combine(_recycleDir, moved.GroupId!))); // 根留个空壳，组目录整个没了
    }
}
