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
              "module_author":{"mid":111,"name":"原作者","pub_ts":1700000200},
              "module_dynamic":{"desc":{"text":"原文在此"},"major":{
                "type":"MAJOR_TYPE_ARCHIVE","archive":{"bvid":"BVorig00001"}}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal("8880004", parsed.DynamicId); // 缓存键是转发条自己
        Assert.Equal(111, parsed.AuthorMid); // 名片与时间认原作者
        Assert.Equal("原作者", parsed.AuthorName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000200).ToString("O"), parsed.PublishedAtUtc);
        Assert.Equal("BVorig00001", parsed.Bvid); // bvid 从 orig 挖
        Assert.StartsWith("我也说两句", parsed.Text);
        Assert.Contains("转发自 原作者", parsed.Text);
        Assert.Contains("原文在此", parsed.Text);
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
}
