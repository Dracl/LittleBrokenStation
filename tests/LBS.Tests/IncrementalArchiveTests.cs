using DownKyi.Core.BiliApi.DanmakuApi.Models;
using LBS.Engine;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 评论与弹幕的增量合并（只进不退）。
/// 这两条链路都吃 B 站的脸色：那边撤一个楼、删一条弹幕，本站的留档都不能跟着变薄，
/// 否则「快照」就成了「此刻的转播」，存档的意义没了。这里钉的就是这条底线。
/// </summary>
public sealed class IncrementalArchiveTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static CommentItem Item(long rpid, string content, IReadOnlyList<CommentItem>? sub = null) =>
        new(rpid, "网友" + rpid, null, content, 0, 0, null, false, sub?.Count, sub);

    private static CommentSnapshot Snap(params CommentItem[] replies) =>
        new(1, 456782499, "BV1o541187Wh", T1, replies.Length, null, replies);

    private static BiliDanmaku Danmaku(long id, int progress, string content) => new()
    {
        Id = id,
        Progress = progress,
        Mode = 1,
        Fontsize = 25,
        Color = 0xFFFFFF,
        MidHash = "mid-" + id,
        Content = content,
        Ctime = 1_700_000_000,
        Weight = 0,
        Pool = 0,
    };

    // ── 评论 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 评论增量合并_B站撤走的楼层继续养着()
    {
        var archived = Snap(Item(1, "原来的内容"), Item(2, "二楼"));
        var fresh = new CommentSnapshot(1, 456782499, "BV1o541187Wh", T2, 500, null, new[] { Item(1, "改过的内容"), Item(3, "三楼") });

        var merge = CommentSnapshotOps.MergeArchive(archived, fresh);

        Assert.Equal(3, merge.Total);
        Assert.Equal(1, merge.Added);      // 三楼
        Assert.Equal(1, merge.Refreshed);  // 一楼改了正文
        Assert.Equal(1, merge.KeptGone);   // 二楼 B 站撤了，档里还在
        Assert.Contains(merge.Merged.Replies, r => r.Rpid == 2 && r.Content == "二楼");
        Assert.Equal("改过的内容", merge.Merged.Replies[0].Content); // 两边都有取最新
        Assert.Equal(T2, merge.Merged.FetchedAtUtc);                 // 抓取时间跟着这一趟走
        Assert.Equal(500, merge.Merged.Total);                       // B 站报的总数不被本地并集压低
    }

    [Fact]
    public void 评论增量合并_楼中楼取并集不被洗掉()
    {
        // 上次按需补采到的楼中楼，这一趟 B 站只给了前几楼。
        var archived = Snap(Item(1, "主楼", new[] { Item(11, "楼中楼甲"), Item(12, "楼中楼乙") }));
        var fresh = Snap(Item(1, "主楼", new[] { Item(13, "新补的楼中楼") }));

        var merge = CommentSnapshotOps.MergeArchive(archived, fresh);

        var root = Assert.Single(merge.Merged.Replies);
        Assert.NotNull(root.Sub);
        Assert.Equal(new long[] { 11, 12, 13 }, root.Sub.Select(s => s.Rpid).ToArray());
    }

    [Fact]
    public void 评论增量合并_置顶取新的没有就留旧的()
    {
        var archived = new CommentSnapshot(1, 1, "BV1", T1, 2, Item(99, "置顶旧"), new[] { Item(1, "一楼") });
        var freshBlank = new CommentSnapshot(1, 1, "BV1", T2, 1, null, new[] { Item(1, "一楼") });

        var kept = CommentSnapshotOps.MergeArchive(archived, freshBlank);
        Assert.NotNull(kept.Merged.Top);
        Assert.Equal(99, kept.Merged.Top.Rpid);

        var freshTop = new CommentSnapshot(1, 1, "BV1", T2, 2, Item(100, "置顶新"), new[] { Item(1, "一楼") });
        var replaced = CommentSnapshotOps.MergeArchive(archived, freshTop);
        var top = replaced.Merged.Top;
        Assert.NotNull(top);
        Assert.Equal(100, top.Rpid);
    }

    [Fact]
    public void 评论增量合并_空手而归的fresh也不会清空留档()
    {
        var archived = Snap(Item(1, "一楼"), Item(2, "二楼"));

        var merge = CommentSnapshotOps.MergeArchive(archived, Snap());

        Assert.Equal(2, merge.Total);
        Assert.Equal(0, merge.Added);
        Assert.Equal(2, merge.KeptGone);
        Assert.Equal(new long[] { 1, 2 }, merge.Merged.Replies.Select(r => r.Rpid).ToArray());
    }

    // ── B 站弹幕 ──────────────────────────────────────────────────────────

    [Fact]
    public void 弹幕并集按id_重复不收撤走不丢()
    {
        var archived = new[] { Danmaku(1, 1000, "老甲"), Danmaku(2, 2000, "老乙") };
        var fetched = new[] { Danmaku(2, 2000, "老乙"), Danmaku(3, 3000, "新丙") };

        var merged = DanmakuArchiveService.MergeById(archived, fetched);

        Assert.Equal(new long[] { 1, 2, 3 }, merged.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void 弹幕并集_id不可用时退化到时间与文本()
    {
        var archived = new[] { Danmaku(0, 1000, "666") };
        var fetched = new[] { Danmaku(0, 1000, "666"), Danmaku(0, 2000, "666") };

        var merged = DanmakuArchiveService.MergeById(archived, fetched);

        // 同时间同文本算一条；换了时间的算新的一条。
        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { 1000, 2000 }, merged.Select(d => d.Progress).ToArray());
    }

    [Fact]
    public void 祖传ass只追加_老行的样式原样保留()
    {
        // 当年下载引擎写的老档：颜色 0xFF0000、字号 30、模式 5，本站没有它们的原始字段。
        var original = string.Join("\n",
            "[Script Info]",
            "Title: 老档",
            "",
            "[V4+ Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour",
            "Style: 默认,微软雅黑,30,&H000000FF",
            "",
            "[Events]",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Text",
            "Dialogue: 0,0:00:01.00,0:00:07.00,默认,,0,0,0,,老弹幕保留样式",
            "Dialogue: 0,0:00:02.00,0:00:08.00,默认,,0,0,0,,另一条老弹幕");
        var generated = string.Join("\n",
            "[Script Info]",
            "[Events]",
            "Dialogue: 0,0:00:01.00,0:00:07.00,默认,,0,0,0,,老弹幕保留样式", // 原样还在：不该重抄
            "Dialogue: 0,0:00:09.00,0:00:15.00,默认,,0,0,0,,B站新来的弹幕");

        var (text, added) = DanmakuArchiveService.ComposeAppendedAss(original, generated);

        Assert.Equal(1, added);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:07.00,默认,,0,0,0,,老弹幕保留样式", text);
        Assert.Contains("Dialogue: 0,0:00:02.00,0:00:08.00,默认,,0,0,0,,另一条老弹幕", text);
        Assert.Contains("Style: 默认,微软雅黑,30,&H000000FF", text); // 样式表没被动过
        Assert.EndsWith("Dialogue: 0,0:00:09.00,0:00:15.00,默认,,0,0,0,,B站新来的弹幕", text);

        // 再刷一次同一批：一个字都不该再加。
        var (again, secondAdded) = DanmakuArchiveService.ComposeAppendedAss(text, generated);
        Assert.Equal(0, secondAdded);
        Assert.Equal(text, again);
    }

    [Fact]
    public void 祖传ass不像ass时宁可不追加()
    {
        var (text, added) = DanmakuArchiveService.ComposeAppendedAss("这根本不是一个 ass 文件", "Dialogue: 0,0:00:01.00,0:00:07.00,x,,0,0,0,,新弹幕");

        Assert.Equal(-1, added);
        Assert.Equal("这根本不是一个 ass 文件", text);
    }

    [Fact]
    public void 弹幕条数与事件行对得上()
    {
        var temp = Path.Combine(Path.GetTempPath(), "lbs-ass-" + Guid.NewGuid().ToString("N") + ".ass");
        try
        {
            File.WriteAllText(temp, "Dialogue: 0,0:00:01.00,0:00:07.00,x,,0,0,0,,甲\r\nDialogue: 0,0:00:02.00,0:00:08.00,x,,0,0,0,,乙\r\n");
            Assert.Equal(2, DanmakuArchiveService.CountDialogue(temp));
            Assert.Equal(
                new[] { "Dialogue: 0,0:00:01.00,0:00:07.00,x,,0,0,0,,甲", "Dialogue: 0,0:00:02.00,0:00:08.00,x,,0,0,0,,乙" },
                DanmakuArchiveService.DialogueLines(File.ReadAllText(temp)).ToArray());

            // 文件不在：给 -1，调用方据此知道「没法比」，而不是当成空档。
            Assert.Equal(-1, DanmakuArchiveService.CountDialogue(temp + ".missing"));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void 弹幕清单读写往返_原始字段一个不丢()
    {
        var temp = Path.Combine(Path.GetTempPath(), "lbs-sidecar-" + Guid.NewGuid().ToString("N") + ".danmaku.json");
        try
        {
            var union = new[] { Danmaku(7, 1234, "带颜色的弹幕") };
            DanmakuArchiveService.DanmakuArchiveStore.Save(
                temp,
                new DanmakuArchiveService.DanmakuArchiveFile(1, 816, union.Select(DanmakuArchiveService.RawDanmaku.From).ToList()));

            var loaded = DanmakuArchiveService.DanmakuArchiveStore.Load(temp);

            Assert.NotNull(loaded);
            Assert.Equal(816, loaded.LegacyDialogue); // 祖传行数记牢：它决定以后只追加、不整档重生成
            var back = Assert.Single(loaded.ToDanmakus());
            Assert.Equal(7, back.Id);
            Assert.Equal(1234, back.Progress);
            Assert.Equal("带颜色的弹幕", back.Content);
            Assert.Equal("mid-7", back.MidHash);
            Assert.Equal(0xFFFFFFu, back.Color);
            Assert.Equal(1_700_000_000, back.Ctime);

            // 清单坏了当没有：宁可退回追加模式，也不把留档当空档。
            File.WriteAllText(temp, "{不是 json");
            Assert.Null(DanmakuArchiveService.DanmakuArchiveStore.Load(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void 祖传档第二次刷新仍走追加不整档重写()
    {
        // 第一次刷完就有清单了：LegacyDialogue 还记着 816，第二次刷必须继续只追加，
        // 不然那 816 行老弹幕会被“只认识清单里那些”的重生成洗掉。
        Assert.True(DanmakuArchiveService.AppendsOnly(new DanmakuArchiveService.DanmakuArchiveFile(1, 816, []), true));
        Assert.True(DanmakuArchiveService.AppendsOnly(null, true)); // 连清单都没有的祖传 ass
        Assert.False(DanmakuArchiveService.AppendsOnly(new DanmakuArchiveService.DanmakuArchiveFile(1, 0, []), true)); // 本站生成的档
        Assert.False(DanmakuArchiveService.AppendsOnly(null, false)); // 全新档
    }

    [Fact]
    public void 弹幕档验货只认有壳子的()
    {
        Assert.True(DanmakuArchiveService.IsUsableAss("[Script Info]\nx\n[Events]\n"));
        Assert.False(DanmakuArchiveService.IsUsableAss(""));
        Assert.False(DanmakuArchiveService.IsUsableAss("[Script Info]\n只有头部没有事件段"));
        Assert.True(DanmakuArchiveService.IsUsableXml("<i><d p=\"1,1,25,16777215,0,0,0,0\">甲</d></i>"));
        Assert.False(DanmakuArchiveService.IsUsableXml("<i></i>"));
    }
}
