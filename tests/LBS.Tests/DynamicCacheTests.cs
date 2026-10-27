using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// M6 动态缓存：解析器按调研报告字段路径喂手写 JSON（ARCHIVE/OPUS/DRAW/FORWARD/WORD 五种 +
/// 风控三形态 + 空页），DB 层测 Upsert 幂等、列表倒序/过滤/游标、target 去重与订阅台账灌种子。
/// HTTP 层不真联网——取数姿势编译期已验，风控下的实战表现属未验项。
/// </summary>
public sealed class DynamicCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public DynamicCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-dyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Feed(string items, bool hasMore = false, string? offset = null)
    {
        var offsetPart = offset is null ? "" : $",\"offset\":\"{offset}\"";
        return "{\"code\":0,\"message\":\"0\",\"ttl\":1,\"data\":{\"has_more\":" + (hasMore ? "true" : "false")
            + offsetPart + ",\"update_baseline\":\"1\",\"items\":[" + items + "]}}";
    }

    // ─── 解析器：五种 type ───

    [Fact]
    public void ParseFeed_Archive_ExtractsAllFields()
    {
        var item = """
            {"id_str":"8880001","type":"DYNAMIC_TYPE_AV","modules":{
              "module_author":{"mid":111,"name":"电影星","pub_ts":1700000000},
              "module_dynamic":{"desc":{"text":"今天开麦聊两嘴"},"major":{
                "type":"MAJOR_TYPE_ARCHIVE","archive":{"bvid":"BV1xx411c7mD","title":"聊两嘴","duration_text":"10:00"}}}}}
            """;

        var result = LbsDynamicFetcher.ParseFeed(Feed(item, hasMore: true, offset: "8880001"));
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.True(result.HasMore);
        Assert.Equal("8880001", result.Offset);
        var parsed = Assert.Single(result.Items);
        Assert.Equal("8880001", parsed.DynamicId);
        Assert.Equal("DYNAMIC_TYPE_AV", parsed.Type);
        Assert.Equal(111, parsed.AuthorMid);
        Assert.Equal("电影星", parsed.AuthorName);
        Assert.Equal("今天开麦聊两嘴", parsed.Text);
        Assert.Equal("BV1xx411c7mD", parsed.Bvid);
        Assert.Empty(parsed.Pics);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).ToString("O"), parsed.PublishedAtUtc);
    }

    [Fact]
    public void ParseFeed_Opus_UsesSummaryAndPics()
    {
        var item = """
            {"id_str":"8880002","type":"DYNAMIC_TYPE_OPUS","modules":{
              "module_author":{"mid":112,"name":"图图酱","pub_ts":1700000100},
              "module_dynamic":{"desc":{"text":""},"major":{
                "type":"MAJOR_TYPE_OPUS","opus":{"summary":{"text":"图文正文在此处"},
                "pics":[{"url":"https://i0.hdslb.com/bfs/opus/a.jpg"},{"url":"https://i0.hdslb.com/bfs/opus/b.jpg"}]}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal("图文正文在此处", parsed.Text);
        Assert.Equal(
            ["https://i0.hdslb.com/bfs/opus/a.jpg", "https://i0.hdslb.com/bfs/opus/b.jpg"],
            parsed.Pics);
        Assert.Null(parsed.Bvid);
    }

    [Fact]
    public void ParseFeed_LegacyDraw_FallsToDrawItems()
    {
        var item = """
            {"id_str":"8880003","type":"DYNAMIC_TYPE_DRAW","modules":{
              "module_author":{"mid":113,"name":"老相簿","pub_ts":1700000150},
              "module_dynamic":{"major":{
                "type":"MAJOR_TYPE_DRAW","draw":{"items":[{"src":"https://i0.hdslb.com/d/1.jpg"},{"src":"https://i0.hdslb.com/d/2.jpg"},{"src":"https://i0.hdslb.com/d/3.jpg"}]}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(3, parsed.Pics.Count);
        Assert.Equal("https://i0.hdslb.com/d/2.jpg", parsed.Pics[1]);
        Assert.Null(parsed.Text);
    }

    [Fact]
    public void ParseFeed_Article_UsesCovers()
    {
        var item = """
            {"id_str":"8880006","type":"DYNAMIC_TYPE_ARTICLE","modules":{
              "module_author":{"mid":116,"name":"专栏党","pub_ts":1700000600},
              "module_dynamic":{"major":{
                "type":"MAJOR_TYPE_ARTICLE","article":{"covers":["https://i0.hdslb.com/cov/one.jpg","https://i0.hdslb.com/cov/two.jpg"],"title":"长文"}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(["https://i0.hdslb.com/cov/one.jpg", "https://i0.hdslb.com/cov/two.jpg"], parsed.Pics);
    }

    [Fact]
    public void ParseFeed_Forward_PrefersOrigEverywhere()
    {
        var item = """
            {"id_str":"8880004","type":"DYNAMIC_TYPE_FORWARD","modules":{
              "module_author":{"mid":222,"name":"转发君","pub_ts":1700000300},
              "module_dynamic":{"desc":{"text":"我也说两句"},"major":null}},
             "orig":{"id_str":"7770001","type":"DYNAMIC_TYPE_AV","modules":{
              "module_author":{"mid":111,"name":"原作者","face":"https://i0.hdslb.com/face/orig.jpg","pub_ts":1700000200},
              "module_dynamic":{"desc":{"text":"原文在此"},"major":{
                "type":"MAJOR_TYPE_ARCHIVE","archive":{"bvid":"BVorig00001"}}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal("8880004", parsed.DynamicId); // 缓存键是转发条自己
        Assert.Equal(222, parsed.AuthorMid); // B 站同款：卡头是转发人，时间认转发时刻
        Assert.Equal("转发君", parsed.AuthorName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000300).ToString("O"), parsed.PublishedAtUtc);
        Assert.Equal("BVorig00001", parsed.Bvid); // bvid 从 orig 挖
        Assert.StartsWith("我也说两句", parsed.Text);
        Assert.Contains("转发自 原作者", parsed.Text);
        Assert.Contains("原文在此", parsed.Text);
        // 转发灰块的原作者名片也从 orig 定格（卡头归转发人，灰块归原作者）。
        Assert.Equal(111, parsed.OrigAuthorMid);
        Assert.Equal("原作者", parsed.OrigAuthorName);
        Assert.Equal("https://i0.hdslb.com/face/orig.jpg", parsed.OrigAuthorFace);
    }

    [Fact]
    public void ParseFeed_Basic_CarriesCommentCoordinates()
    {
        // B 站把评论区挂在 basic 上：图文卡→相簿（type=11+rid）、视频卡→视频评论区（type=1+aid），与动态 id 不同源。
        var item = """
            {"id_str":"8880010","type":"DYNAMIC_TYPE_DRAW",
              "basic":{"comment_type":11,"comment_id_str":"413420393"},
              "modules":{"module_author":{"mid":119,"name":"图透君","pub_ts":1700001000},
              "module_dynamic":{"desc":{"text":"看风景"},"major":null}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(11, parsed.CommentType);
        Assert.Equal("413420393", parsed.CommentOid);
    }

    [Fact]
    public void ParseFeed_Basic_MissingCoordinates_StaysNull()
    {
        // 老卡型可能不带 basic：坐标留 null，巡捕自己退回 type=17+动态 id。
        var item = """
            {"id_str":"8880011","type":"DYNAMIC_TYPE_WORD","modules":{
              "module_author":{"mid":119,"name":"纯文君","pub_ts":1700001100},
              "module_dynamic":{"desc":{"text":"裸卡"},"major":null}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Null(parsed.CommentType);
        Assert.Null(parsed.CommentOid);
    }

    [Fact]
    public void ParseFeed_StatCounts_CapturedFromTopLevel()
    {
        var item = """
            {"id_str":"8880009","type":"DYNAMIC_TYPE_WORD","modules":{
              "module_author":{"mid":119,"name":"计数君","pub_ts":1700000900},
              "module_dynamic":{"desc":{"text":"底栏有料"},"major":null,
                "stat":{"comment":{"count":12},"forward":{"count":3},"like":{"count":72}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(12, parsed.StatComment);
        Assert.Equal(3, parsed.StatForward);
        Assert.Equal(72, parsed.StatLike);
        Assert.Null(parsed.OrigAuthorMid); // 非转发条没有灰块名片
    }

    [Fact]
    public void ParseReplies_Main_ParsesTopCommentsAndSubRoots()
    {
        var json = """
            {"code":0,"data":{"page":{"count":128},"replies":[
              {"rpid":111,"ctime":1700000000,"like":42,"rcount":3,
               "content":{"message":"热评一号"},"member":{"mid":9001,"uname":"月光","face":"https://i0.hdslb.com/f/1.jpg"}},
              {"rpid":112,"content":{"message":"   "},"member":{"mid":1}},
              {"rpid":0,"content":{"message":"没楼号"},"member":{"mid":2}}]}}
            """;

        var result = LbsDynamicFetcher.ParseReplies(json, rootRpid: 0, max: 20, collectRoots: true);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Equal(128, result.TotalCount); // 底栏评论数认 page.count，不认抽了几条
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        var top = Assert.Single(result.Replies); // 空正文与没楼号的行开不了楼
        Assert.Equal(111, top.Rpid);
        Assert.Equal(0, top.RootRpid);
        Assert.Equal("月光", top.Name);
        Assert.Equal(42, top.Like);
        Assert.Equal("2023-11-14", top.CtimeUtc![..10]);
        Assert.Equal([111L], result.Roots);
    }

    [Fact]
    public void ParseReplies_Sub_AttachToParentFloor()
    {
        var json = """
            {"code":0,"data":{"replies":[
              {"rpid":211,"like":2,"content":{"message":"子回复一条"},"member":{"mid":7,"uname":"小林"}}]}}
            """;

        var result = LbsDynamicFetcher.ParseReplies(json, rootRpid: 111, max: 20, collectRoots: false);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Empty(result.Roots);
        Assert.Null(result.Reason);
        var sub = Assert.Single(result.Replies);
        Assert.Equal(111, sub.RootRpid);
        Assert.Equal(111, sub.ParentRpid);
        Assert.Equal("子回复一条", sub.Message);
    }

    [Fact]
    public void ParseReplies_RiskControlled_SignalsAbort()
    {
        var json = """
            {"code":-352,"message":"风控"}
            """;
        var result = LbsDynamicFetcher.ParseReplies(json, 0, 20, true);
        Assert.Equal(FeedParseStatus.RiskControlled, result.Status);
        Assert.Empty(result.Replies);
        Assert.Empty(result.Roots);
        Assert.Contains("-352", result.Reason);
    }

    [Fact]
    public void ParseReplies_NoCommentSection_GracefulEmpty()
    {
        // 开播提醒等卡型没开评论区，B 站回 -404：当空页交差，不留永久欠账。
        var json = """
            {"code":-404,"message":"啥都木有"}
            """;
        var result = LbsDynamicFetcher.ParseReplies(json, 0, 20, true);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Empty(result.Replies);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ParseFeed_Word_PlainTextOnly()
    {
        var item = """
            {"id_str":"8880005","type":"DYNAMIC_TYPE_WORD","modules":{
              "module_author":{"mid":115,"name":"碎碎念","pub_ts":1700000500},
              "module_dynamic":{"desc":{"text":"今天天气好"},"major":null}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal("今天天气好", parsed.Text);
        Assert.Empty(parsed.Pics);
        Assert.Null(parsed.Bvid);
    }

    // ─── 风控与坏包识别 ───

    [Fact]
    public void ParseFeed_DetectsRiskControlForms()
    {
        var signed = LbsDynamicFetcher.ParseFeed("""{"code":-352,"message":"risk control","data":{}}""");
        Assert.Equal(FeedParseStatus.RiskControlled, signed.Status);

        var voucher = LbsDynamicFetcher.ParseFeed("""{"code":0,"data":{"v_voucher":"vcb_abc123","items":[]}}""");
        Assert.Equal(FeedParseStatus.RiskControlled, voucher.Status);
    }

    [Fact]
    public void ParseFeed_DetectsPlainFailures()
    {
        Assert.Equal(FeedParseStatus.Failed, LbsDynamicFetcher.ParseFeed("""{"code":-101,"message":"账号未登录"}""").Status);
        Assert.Equal(FeedParseStatus.Failed, LbsDynamicFetcher.ParseFeed("B 站回了段 HTML").Status);
        Assert.Equal(FeedParseStatus.Failed, LbsDynamicFetcher.ParseFeed("""{"code":0,"msg":"没 data"}""").Status);
    }

    [Fact]
    public void ParseFeed_EmptyItemsIsNormalOk()
    {
        var result = LbsDynamicFetcher.ParseFeed(Feed(""));
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
    }

    // ─── DB 层 ───

    [Fact]
    public void UpsertDynamic_IsIdempotent_ByDynamicId()
    {
        _db.AddDynamicTarget(1001);
        var first = _db.UpsertDynamic("d1", 1001, 1001, "阿婆主", "DYNAMIC_TYPE_WORD", "第一版", null, null, "2026-01-01T00:00:00.0000000+00:00");
        var second = _db.UpsertDynamic("d1", 1001, 1001, "阿婆主", "DYNAMIC_TYPE_WORD", "改过的正文", null, null, "2026-01-01T00:00:00.0000000+00:00");

        Assert.True(first);
        Assert.False(second); // 抓回即定格：撞车 IGNORE，不回改
        var row = Assert.Single(_db.ListDynamics(null));
        Assert.Equal("第一版", row.Text);
    }

    [Fact]
    public void ListDynamics_DescByTargetFilter_AndCursor()
    {
        _db.AddDynamicTarget(2001);
        _db.AddDynamicTarget(2002);
        for (var i = 1; i <= 5; i++)
        {
            _db.UpsertDynamic($"a{i}", 2001, 2001, "甲", "DYNAMIC_TYPE_WORD", $"甲{i}", null, null, "2026-01-01T00:00:00+00:00");
        }

        _db.UpsertDynamic("b1", 2002, 2002, "乙", "DYNAMIC_TYPE_WORD", "乙一", null, null, "2026-01-02T00:00:00+00:00");

        var all = _db.ListDynamics(null);
        Assert.Equal(6, all.Count);
        Assert.True(all[0].Id > all[^1].Id); // 新→旧

        var onlyJia = _db.ListDynamics(2001);
        Assert.Equal(5, onlyJia.Count);
        Assert.All(onlyJia, d => Assert.Equal(2001, d.TargetMid));

        // beforeId 游标：拿第三条起翻，只剩更早的两条
        var thirdId = onlyJia[2].Id;
        var page2 = _db.ListDynamics(2001, limit: 10, beforeId: thirdId);
        Assert.Equal(2, page2.Count);
        Assert.All(page2, d => Assert.True(d.Id < thirdId));

        // limit 封顶
        Assert.Equal(2, _db.ListDynamics(2001, limit: 2).Count);
    }

    [Fact]
    public void ListDynamics_OrdersByPublishedTime_NotInsertionOrder()
    {
        // 回扫补坐标会把旧动态重插、id 翻新；列表必须按发布时间新→旧，不能让旧货窜到队头。
        // 故意把最旧的 old 最后插（id 最大），连翻页游标一起验：不能拿 id 全局截断。
        _db.AddDynamicTarget(2003);
        _db.UpsertDynamic("new", 2003, 2003, "甲", "DYNAMIC_TYPE_WORD", "新鲜", null, null, "2026-10-20T00:00:00+00:00");
        _db.UpsertDynamic("sneaky", 2003, 2003, "甲", "DYNAMIC_TYPE_WORD", "回扫重插的旧货", null, null, "2026-02-05T00:00:00+00:00");
        _db.UpsertDynamic("old", 2003, 2003, "甲", "DYNAMIC_TYPE_WORD", "旧货但插得晚", null, null, "2025-12-01T00:00:00+00:00");

        var all = _db.ListDynamics(null);
        Assert.Equal(new[] { "new", "sneaky", "old" }, all.Select(d => d.DynamicId).ToArray());

        // (时间, id) 双指针游标：从 sneaky 那条起翻，只剩更早的 old
        var page2 = _db.ListDynamics(2003, limit: 10, beforeId: all[1].Id, beforeTime: all[1].PublishedAtUtc);
        Assert.Equal("old", Assert.Single(page2).DynamicId);
    }

    [Fact]
    public void ListDynamics_MarksInStationViaVideosJoin()
    {
        _db.AddDynamicTarget(2101);
        _db.UpsertDynamic("in", 2101, 2101, "甲", "DYNAMIC_TYPE_AV", null, null, "BVinstation1", "2026-01-01T00:00:00+00:00");
        _db.UpsertDynamic("out", 2101, 2101, "甲", "DYNAMIC_TYPE_AV", null, null, "BVnotstation", "2026-01-01T00:00:00+00:00");
        _db.AddVideosBulk(new[] { new NewVideo("https://www.bilibili.com/video/BVinstation1", "BVinstation1", "馆藏有货") });

        Assert.True(Assert.Single(_db.ListDynamics(2101), d => d.DynamicId == "in").InStation);
        Assert.False(Assert.Single(_db.ListDynamics(2101), d => d.DynamicId == "out").InStation);
    }

    [Fact]
    public void AddDynamicTarget_Dedups_AndRemoveWorks()
    {
        Assert.True(_db.AddDynamicTarget(3001, "甲"));
        Assert.False(_db.AddDynamicTarget(3001)); // mid UNIQUE 去重
        Assert.True(_db.AddDynamicTarget(3002, "乙"));

        var targets = _db.ListDynamicTargets();
        Assert.Equal(2, targets.Count);
        Assert.Equal(0, Assert.Single(targets, t => t.Mid == 3001).Count);

        Assert.Equal(1, _db.RemoveDynamicTarget(3001));
        Assert.Equal(0, _db.RemoveDynamicTarget(3999)); // 查无此 target
        Assert.Single(_db.ListDynamicTargets());
    }

    [Fact]
    public void SeedTargetsFromSubscriptions_OnlyUpRows_AndIdempotent()
    {
        var ledger = JsonSerializer.Serialize(new object[]
        {
            new { Kind = "up", Target = "space.bilibili.com/9901/", AddedAtUtc = "2026-01-01T00:00:00+00:00" },
            new { Kind = "up", Target = "9902", AddedAtUtc = "2026-01-01T00:00:00+00:00" },
            new { Kind = "up", Target = "https://space.bilibili.com/h5/9903", AddedAtUtc = "2026-01-01T00:00:00+00:00" },
            new { Kind = "fav", Target = "9904", AddedAtUtc = "2026-01-01T00:00:00+00:00" },
        });
        _db.SetSetting("subscriptions", ledger);

        Assert.Equal(3, _db.SeedTargetsFromSubscriptions());
        Assert.Equal([9901L, 9902L, 9903L], _db.ListDynamicTargets().Select(t => t.Mid).OrderBy(m => m));
        Assert.DoesNotContain(_db.ListDynamicTargets(), t => t.Mid == 9904); // 收藏夹 id 不混进盯梢名单

        // 重复灌入无变化
        Assert.Equal(0, _db.SeedTargetsFromSubscriptions());
        Assert.Equal(3, _db.ListDynamicTargets().Count);

        // 台账坏了不炸
        _db.SetSetting("subscriptions", "这不是 JSON");
        Assert.Equal(0, _db.SeedTargetsFromSubscriptions());
    }

    [Fact]
    public void TargetOffset_RoundTrips()
    {
        _db.AddDynamicTarget(4001);
        _db.SetTargetOffset(4001, "offset-77", "2026-06-01T08:00:00+00:00");
        var row = Assert.Single(_db.ListDynamicTargets());
        Assert.Equal("offset-77", row.LastOffset);
        Assert.Equal("2026-06-01T08:00:00+00:00", row.LastFetchAtUtc);

        _db.SetTargetOffset(4001, null, "2026-06-01T09:00:00+00:00"); // 见底归零游标
        row = Assert.Single(_db.ListDynamicTargets());
        Assert.Null(row.LastOffset);
    }

    [Fact]
    public void UpsertDynamic_BackfillsBlankTargetName_Once()
    {
        _db.AddDynamicTarget(5001); // 没带名片种子
        _db.UpsertDynamic("d", 5001, 5001, "阿婆主", "DYNAMIC_TYPE_WORD", "t", null, null, "2026-01-01T00:00:00+00:00");
        Assert.Equal("阿婆主", Assert.Single(_db.ListDynamicTargets()).Nickname);

        // 已有名片不覆盖
        _db.UpsertDynamic("d2", 5001, 5001, "改名了", "DYNAMIC_TYPE_WORD", "t2", null, null, "2026-01-01T00:00:00+00:00");
        Assert.Equal("阿婆主", Assert.Single(_db.ListDynamicTargets()).Nickname);
    }

    [Fact]
    public void CommentCatchup_PendingCarriesCoordinates_AndSealWritesBackCount()
    {
        // 图文卡的评论区挂在相簿（type=11+rid）：坐标随入库落库，欠账名单带着它走。
        _db.UpsertDynamic("cd1", 7001, 7001, "图文君", "DYNAMIC_TYPE_DRAW", "图", null, null, "2026-01-01T00:00:00+00:00",
            commentType: 11, commentOid: "413420393");

        var pending = Assert.Single(_db.ListPendingCommentDynamics());
        Assert.Equal("cd1", pending.DynamicId);
        Assert.Equal(11, pending.CommentType);
        Assert.Equal("413420393", pending.CommentOid);

        Assert.Equal(1, _db.UpsertComments("cd1",
            [new StationDb.DynamicCommentRow(501, 0, 0, 9, "楼", "https://i0.hdslb.com/f/9.jpg", "一楼", 3, null)]));
        _db.MarkCommentsFetched("cd1", 57);

        // 盖章后退出欠账名单；底栏评论数从 page.count 回写（动态流不带 stat，巡捕回执是唯一来源）。
        Assert.Empty(_db.ListPendingCommentDynamics());
        var row = Assert.Single(_db.ListDynamics(7001));
        Assert.Equal(57, row.StatComment);
        Assert.NotNull(row.CommentsFetchedAtUtc);
    }
}
