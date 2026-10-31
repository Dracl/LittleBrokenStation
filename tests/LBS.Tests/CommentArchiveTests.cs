using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>评论归档：快照映射、观看页 JSON 契约、账本登记。不联网，抓取器只测纯函数部分。</summary>
public sealed class CommentArchiveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _videosDir;
    private readonly StationDb _db;

    public CommentArchiveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-comments-" + Guid.NewGuid().ToString("N"));
        _videosDir = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(_videosDir);
        var paths = new DataPaths(_tempDir, _videosDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private static ReplyRaw Reply(long rpid, string name, string message, long like = 0, long rcount = 0, string? ip = null) =>
        new()
        {
            Rpid = rpid,
            Like = like,
            Ctime = 1700000000,
            Rcount = rcount,
            IpLocation = ip,
            Content = new ReplyContent { Message = message },
            Member = new ReplyMember { Uname = name, Avatar = $"https://i0.hdslb.com/face/{rpid}.jpg" },
        };

    [Fact]
    public void 快照映射_置顶抽出_热评去重_楼中楼随行()
    {
        var firstPage = new ReplyPage
        {
            Upper = new ReplyUpper
            {
                Top = Reply(1, "置顶君", "置顶公告"),
                Hot = [Reply(2, "热评甲", "前排"), Reply(3, "热评乙", "沙发")],
            },
            Replies = [Reply(3, "热评乙", "沙发"), Reply(4, "潜水员", "顶")],
            Page = new ReplyPageInfo { Count = 999 },
        };

        // 模拟 FetchAsync 的 Absorb：热评在前、rpid 去重。
        var collected = new List<ReplyRaw> { Reply(2, "热评甲", "前排"), Reply(3, "热评乙", "沙发", rcount: 3), Reply(4, "潜水员", "顶") };
        collected[1].LbsSub = [Reply(31, "楼主", "统一回复")];
        collected[2].IpLocation = "上海";

        var snapshot = LbsCommentArchiver.MapSnapshot(12345, "BV1xx411c7mD", firstPage, collected);

        Assert.Equal(1, snapshot.Schema);
        Assert.Equal(12345, snapshot.Aid);
        Assert.NotNull(snapshot.Top);
        Assert.Equal(1, snapshot.Top!.Rpid);
        Assert.True(snapshot.Top.IsTop);
        Assert.Equal([2L, 3L, 4L], snapshot.Replies.Select(r => r.Rpid).ToArray());
        Assert.False(snapshot.Replies.Any(r => r.IsTop));
        Assert.Single(snapshot.Replies[1].Sub!);
        Assert.Equal(31, snapshot.Replies[1].Sub![0].Rpid);
        Assert.Equal(3L, snapshot.Replies[1].ReplyCount);
        Assert.Equal("上海", snapshot.Replies[2].IpLocation);
        Assert.Equal(999, snapshot.Total); // B 站报的总数优先
    }

    [Fact]
    public void 快照JSON_字段名对齐观看页契约_空字段省略()
    {
        var collected = new List<ReplyRaw> { Reply(7, "楼八", "内容", like: 42) };
        var snapshot = LbsCommentArchiver.MapSnapshot(1, "BV1", firstPage: null, collected);
        var json = LbsCommentArchiver.Serialize(snapshot);

        foreach (var key in new[]
        {
            "\"schema\": 1",
            "\"aid\": 1",
            "\"fetchedAtUtc\"",
            "\"total\"",
            "\"replies\"",
            "\"rpid\": 7",
            "\"like\": 42",
            "\"content\"",
            "\"name\"",
        })
        {
            if (!string.IsNullOrEmpty(key))
            {
                Assert.Contains(key, json);
            }
        }

        // 没有的字段（头像缺失/无楼中楼/无置顶）必须整个消失，前端按缺省渲染。
        var bare = LbsCommentArchiver.MapSnapshot(1, "BV1", null,
        [
            new ReplyRaw { Rpid = 8, Content = new ReplyContent { Message = "无头像楼" }, Member = new ReplyMember { Uname = "路人" } },
        ]);
        var bareJson = LbsCommentArchiver.Serialize(bare);
        Assert.DoesNotContain("\"face\"", bareJson);
        Assert.DoesNotContain("\"sub\"", bareJson);
        Assert.DoesNotContain("\"top\"", bareJson);
    }

    [Fact]
    public void 快照回读与楼中楼并楼_只留最新不重复()
    {
        var items = new List<ReplyRaw> { Reply(1, "甲", "主楼", rcount: 5), Reply(2, "乙", "副楼", like: 9) };
        var snapshot = LbsCommentArchiver.MapSnapshot(7, "BVx", null, items);
        var file = Path.Combine(_videosDir, "rt", "base.comments.json");

        CommentSnapshotOps.Save(file, snapshot);
        var loaded = CommentSnapshotOps.Load(file);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.Aid);
        Assert.Equal(2, loaded.Replies.Count);

        var sub = new CommentItem(11, "楼", null, "回复内容", 0, 1, null, false, null, null, "42", 6, null, null, "甲", "/avatars/x.jpg");
        var (merged, found) = CommentSnapshotOps.MergeSubs(loaded, 1, [sub], 1);
        Assert.True(found);
        CommentSnapshotOps.Save(file, merged);

        var again = CommentSnapshotOps.Load(file)!;
        var target = again.Replies.First(r => r.Rpid == 1);
        Assert.Single(target.Sub!);
        Assert.Equal(11, target.Sub![0].Rpid);
        Assert.Equal("甲", target.Sub![0].ParentName);
        Assert.Equal("/avatars/x.jpg", target.Sub![0].FaceLocal);
        Assert.Equal(5L, target.ReplyCount); // max(原回复数, 已加载数)

        // 第二页重复并楼不产生重复楼层。
        var (deduped, _) = CommentSnapshotOps.MergeSubs(again, 1, target.Sub!, 2);
        Assert.Single(deduped.Replies.First(r => r.Rpid == 1).Sub!);

        var (_, missing) = CommentSnapshotOps.MergeSubs(again, 999, [], 1);
        Assert.False(missing);
    }

    [Fact]
    public void 表情与图片_本地映射进快照并可回读()
    {
        var raw = Reply(1, "甲", "早上好 [doge]");
        raw.Content!.Pictures = [new ReplyPicture { ImgSrc = "https://i0.hdslb.com/bfs/new_dyn/pic.jpg" }];
        raw.Content.Emote = new Dictionary<string, ReplyEmote>
        {
            ["[doge]"] = new ReplyEmote { Url = "https://i0.hdslb.com/bfs/emote/doge.png" },
        };
        var snap = LbsCommentArchiver.MapSnapshot(
            1, "BVx", null, [raw],
            pictureMap: new Dictionary<string, string> { ["https://i0.hdslb.com/bfs/new_dyn/pic.jpg"] = "/cmt-img/pic.jpg" },
            emoteMap: new Dictionary<string, string> { ["[doge]"] = "/emotes/doge.png" });

        Assert.Equal("/cmt-img/pic.jpg", snap.Replies[0].PictureLocals![0]);
        Assert.Equal("/emotes/doge.png", snap.Emotes!["[doge]"]);

        // 回读不丢字段，MergeSubs 并楼也不能丢表情映射。
        var file = Path.Combine(_videosDir, "rt2", "em.comments.json");
        CommentSnapshotOps.Save(file, snap);
        var loaded = CommentSnapshotOps.Load(file)!;
        Assert.Equal("/emotes/doge.png", loaded.Emotes!["[doge]"]);
        var (merged, _) = CommentSnapshotOps.MergeSubs(loaded, 1, [], 1);
        Assert.Equal("/emotes/doge.png", merged.Emotes!["[doge]"]);

        // 图没存下时 PictureLocals 归 null，前端拿原 URL 热链。
        var bare = LbsCommentArchiver.MapSnapshot(1, "BVx", null, [raw]);
        Assert.Null(bare.Replies[0].PictureLocals);
        Assert.Null(bare.Emotes);
    }

    [Fact]
    public void 表情元数据_没给url的那批也留档_回填时有把手()
    {
        // B 站对 UP 专属表情停发的现场形状：能给 key 的里面，有的带 url、有的 url 为空，没 key 的根本不会出现在这里。
        var raw = Reply(1, "甲", "可爱 [UP_1802654492_打call][UP_1802654492_得意]");
        raw.Content!.Emote = new Dictionary<string, ReplyEmote>
        {
            ["[UP_1802654492_打call]"] = new ReplyEmote { Url = "https://i0.hdslb.com/bfs/garb/dacall.png", Id = 113045, PackageId = 1593, Type = 10, JumpTitle = "打call" },
            ["[UP_1802654492_得意]"] = new ReplyEmote { Url = "", Id = 113051, PackageId = 1593, Type = 10, JumpTitle = "得意" },
            ["[UP_1802654492_空空]"] = new ReplyEmote(), // 四字段全空的占位条目：不收，别占坑
        };

        var snap = LbsCommentArchiver.MapSnapshot(
            1, "BVx", null, [raw],
            emoteMap: new Dictionary<string, string> { ["[UP_1802654492_打call]"] = "/emotes/dacall.png" },
            emoteMeta: LbsCommentArchiver.CollectEmoteMeta(new[] { raw }));

        // 落盘只认有 url 的，留档要连没 url 的一起收——缺图那批的包号才是日后回填的唯一把手。
        Assert.Equal("/emotes/dacall.png", snap.Emotes!["[UP_1802654492_打call]"]);
        Assert.False(snap.Emotes!.ContainsKey("[UP_1802654492_得意]"));
        Assert.Equal(2, snap.EmoteMeta!.Count);
        Assert.Equal(1593, snap.EmoteMeta["[UP_1802654492_得意]"].PackageId);
        Assert.Equal("得意", snap.EmoteMeta["[UP_1802654492_得意]"].JumpTitle);
        Assert.Equal(10, snap.EmoteMeta["[UP_1802654492_打call]"].Type);

        // 落盘 → 回读 → 并楼，元数据一路不丢（前端失效胶囊靠 jumpTitle 念对名字）。
        var file = Path.Combine(_videosDir, "meta", "em.comments.json");
        CommentSnapshotOps.Save(file, snap);
        var loaded = CommentSnapshotOps.Load(file)!;
        Assert.Equal(1593, loaded.EmoteMeta!["[UP_1802654492_得意]"].PackageId);
        var (merged, _) = CommentSnapshotOps.MergeSubs(loaded, 1, [], 1);
        Assert.Equal("得意", merged.EmoteMeta!["[UP_1802654492_得意]"].JumpTitle);
    }

    [Fact]
    public void 老快照没emoteMeta键_回读不炸_字段为空()
    {
        // 存量馆藏就是这个形状（null 字段全省略），读侧必须容错，不给老数据做迁移。
        var raw = Reply(1, "甲", "老楼 [doge]");
        var snap = LbsCommentArchiver.MapSnapshot(
            1, "BVlegacy", null, [raw],
            emoteMap: new Dictionary<string, string> { ["[doge]"] = "/emotes/doge.png" });
        var json = LbsCommentArchiver.Serialize(snap);
        Assert.DoesNotContain("emoteMeta", json);

        var file = Path.Combine(_videosDir, "legacy", "em.comments.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
        var loaded = CommentSnapshotOps.Load(file);
        Assert.NotNull(loaded);
        Assert.Equal("/emotes/doge.png", loaded!.Emotes!["[doge]"]);
        Assert.Null(loaded.EmoteMeta);
    }

    [Fact]
    public void 置顶依序寻址_楼中楼父名解析() 
    {
        var pinned = Reply(1, "置顶君", "公告");
        var page = new ReplyPage { TopReplies = [pinned] };
        var collected = new List<ReplyRaw> { Reply(1, "置顶君", "公告"), Reply(2, "乙", "楼") };
        var snap = LbsCommentArchiver.MapSnapshot(1, null, page, collected);
        Assert.Equal(1, snap.Top!.Rpid); // 旧版接口把置顶藏 top_replies
        Assert.DoesNotContain(snap.Replies, r => r.Rpid == 1);

        // main 接口不给置顶时，补采来的 topOverride 优先。
        var withOverride = LbsCommentArchiver.MapSnapshot(1, null, null, collected, null, Reply(9, "钉哥", "公告"));
        Assert.Equal(9, withOverride.Top!.Rpid);
        Assert.Contains(withOverride.Replies, r => r.Rpid == 1); // 非置顶楼照常保留

        var r1 = Reply(11, "甲", "一楼");
        r1.Parent = 5; // 直接回根楼 → 不标
        var r2 = Reply(12, "乙", "二楼");
        r2.Parent = 11; // 回本页甲 → 取昵称
        var r3 = Reply(13, "丙", "三楼");
        r3.Parent = 999;
        r3.ParentStr = "999"; // 父楼不在本页且 parent_str 是数字 → 丢弃
        var names = LbsCommentArchiver.ResolveParentNames([r1, r2, r3], 5);
        Assert.Null(names[0]);
        Assert.Equal("甲", names[1]);
        Assert.Null(names[2]);
    }

    [Fact]
    public void 头像缓存文件名_稳定且认得扩展名()
    {
        var a = LbsCommentArchiver.AvatarFileName("https://i0.hdslb.com/bfs/face/abc.jpg");
        var b = LbsCommentArchiver.AvatarFileName("https://i0.hdslb.com/bfs/face/abc.jpg");
        Assert.Equal(a, b);
        Assert.EndsWith(".jpg", a);
        Assert.EndsWith(".png", LbsCommentArchiver.AvatarFileName("https://x/y.png?_1=2"));
        Assert.EndsWith(".jpg", LbsCommentArchiver.AvatarFileName("https://x/y"));
        // 存档原图路线：gif/webp 也认（认不出才退 .jpg），名体是裸链 sha1 的前 20 位小写十六进制。
        Assert.EndsWith(".gif", LbsCommentArchiver.AvatarFileName("https://i0.hdslb.com/bfs/face/x.gif"));
        Assert.EndsWith(".webp", LbsCommentArchiver.AvatarFileName("https://i0.hdslb.com/bfs/face/x.webp"));
        Assert.Matches("^[0-9a-f]{20}\\.jpg$", a);
        // sha1 拿裸链原样进哈希：异链异名，查询串也算身份（同图异参不串件），但扩展名只认 path 尾巴。
        Assert.NotEqual(a, LbsCommentArchiver.AvatarFileName("https://i0.hdslb.com/bfs/face/abd.jpg"));
        var bare = LbsCommentArchiver.AvatarFileName("https://x/y.png");
        var queried = LbsCommentArchiver.AvatarFileName("https://x/y.png?_1=2");
        Assert.EndsWith(".png", queried);
        Assert.NotEqual(bare[..20], queried[..20]);
    }

    [Fact]
    public void 徽章等级mid图片_从原始响应进快照()
    {
        var raw = Reply(5, "戴着勋章的人", "带图评论");
        raw.Member!.Mid = "556980";
        raw.Member.LevelRank = 6000;
        raw.Member.Medals = [new ReplyMedal { Level = 4, Name = "七禾酱", Color = "e6457a" }];
        raw.Content!.Pictures =
        [
            new ReplyPicture { ImgSrc = "https://i0.hdslb.com/bfs/new_dyn/pic1.jpg" },
            new ReplyPicture { ImgSrc = "不是URL" },
        ];
        raw.UpAction = new ReplyUpAction { Like = true };
        raw.ReplyControl = new ReplyControlRaw { Location = "IP属地：上海" };

        var snapshot = LbsCommentArchiver.MapSnapshot(1, null, null, [raw]);
        var item = snapshot.Replies[0];
        Assert.Equal("556980", item.Mid);
        Assert.Equal(6, item.Level);

        // 老接口没 level_rank，只有 level_info.current_level，也要能认。
        var alt = Reply(6, "另一人", "hi");
        alt.Member!.LevelInfo = new ReplyLevelInfo { CurrentLevel = 5 };
        var altItem = LbsCommentArchiver.MapSnapshot(1, null, null, [alt]).Replies[0];
        Assert.Equal(5, altItem.Level);
        Assert.Single(item.Medals!);
        Assert.Equal(4, item.Medals![0].Level);
        Assert.Equal("七禾酱", item.Medals[0].Name);
        Assert.Single(item.Pictures!); // 非法 URL 被洗掉
        Assert.True(item.UpLiked); // UP主觉得很赞
        Assert.Equal("上海", item.IpLocation); // reply_control.location 剥前缀入库
        Assert.Null(item.ParentName);
    }

    [Fact]
    public void 账本登记_评论快照进观看数据_重登只留最新()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1Ax411d7jD", "BV1Ax411d7jD").VideoId;
        var basePath = Path.Combine(_videosDir, "万恶之源", "1-万恶之源-720P-AVC");
        _db.SaveSubmissionMetadata(
            videoId,
            new VideoMetaDraft("UP", 1, "音乐", null, null, null),
            [new PageDraft("t1", 1, "正片", 9, "03:00", basePath)]);

        Assert.Null(_db.GetWatchData("BV1Ax411d7jD")!.CommentsUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        File.WriteAllText(basePath + ".comments.json", "{}");
        _db.RegisterCommentsArtifact(videoId, "万恶之源/1-万恶之源-720P-AVC.comments.json");

        Assert.Equal("万恶之源/1-万恶之源-720P-AVC.comments.json", _db.GetArtifactRel(videoId, "comments"));
        Assert.Equal("/media/万恶之源/1-万恶之源-720P-AVC.comments.json", _db.GetWatchData("BV1Ax411d7jD")!.CommentsUrl);

        // 补采换路径重登记：只留最新一条。
        File.WriteAllText(basePath + "-v2.comments.json", "{}");
        _db.RegisterCommentsArtifact(videoId, "万恶之源/1-万恶之源-720P-AVC-v2.comments.json");
        Assert.Equal("/media/万恶之源/1-万恶之源-720P-AVC-v2.comments.json", _db.GetWatchData("BV1Ax411d7jD")!.CommentsUrl);
    }

    [Fact]
    public void 台账摘要与aid回填_评论采集的读入口()
    {
        var videoId = _db.AddVideo("https://www.bilibili.com/video/BV1Z2ny63E86", "BV1Z2ny63E86").VideoId;
        Assert.Null(_db.GetVideoBrief(videoId)!.Value.Aid);

        _db.SaveAid(videoId, 54321);
        var brief = _db.GetVideoBrief(videoId);
        Assert.Equal("BV1Z2ny63E86", brief!.Value.Bvid);
        Assert.Equal(54321, brief.Value.Aid);
        Assert.Equal(VideoStatus.Pending, brief.Value.Status);

        Assert.Null(_db.GetFirstPageBasePath(videoId));
        _db.SaveSubmissionMetadata(videoId, new VideoMetaDraft(null, null, null, null, null, null),
            [new PageDraft("t1", 2, "P2", 1, null, "/x/2"), new PageDraft("t2", 1, "P1", 2, null, "/x/1")]);
        Assert.Equal("/x/1", _db.GetFirstPageBasePath(videoId)); // 按 idx 取第一集，不是插入顺序

        Assert.Null(_db.GetVideoBrief(999999));
    }
}
