using System.Text.Json;
using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;

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
                "pics":[{"url":"https://i0.hdslb.com/bfs/opus/a.jpg","width":1000,"height":1500},{"url":"https://i0.hdslb.com/bfs/opus/b.jpg"}]}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal("图文正文在此处", parsed.Text);
        Assert.Equal(
            ["https://i0.hdslb.com/bfs/opus/a.jpg", "https://i0.hdslb.com/bfs/opus/b.jpg"],
            parsed.Pics.Select(p => p.Url));
        // 宽高随图定格（前端拿去判「长图」角标）；没给的留 null。
        Assert.Equal((1000, 1500), (parsed.Pics[0].Width, parsed.Pics[0].Height));
        Assert.Null(parsed.Pics[1].Width);
        Assert.Null(parsed.Bvid);
    }

    [Fact]
    public void ParseFeed_LegacyDraw_FallsToDrawItems()
    {
        var item = """
            {"id_str":"8880003","type":"DYNAMIC_TYPE_DRAW","modules":{
              "module_author":{"mid":113,"name":"老相簿","pub_ts":1700000150},
              "module_dynamic":{"major":{
                "type":"MAJOR_TYPE_DRAW","draw":{
                  "items":[{"src":"https://i0.hdslb.com/d/1.jpg","width":800,"height":2000},{"src":"https://i0.hdslb.com/d/2.jpg"},{"src":"https://i0.hdslb.com/d/3.jpg"}],
                  "images":[{"width":800,"height":2000},{"width":400,"height":500},{"width":600,"height":600}]}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(3, parsed.Pics.Count);
        Assert.Equal("https://i0.hdslb.com/d/2.jpg", parsed.Pics[1].Url);
        // 尺寸两处都认：items 自带的优先，没带的按同一 index 吃平行 images 数组兑尺寸。
        Assert.Equal((800, 2000), (parsed.Pics[0].Width, parsed.Pics[0].Height));
        Assert.Equal((400, 500), (parsed.Pics[1].Width, parsed.Pics[1].Height));
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
        Assert.Equal(["https://i0.hdslb.com/cov/one.jpg", "https://i0.hdslb.com/cov/two.jpg"], parsed.Pics.Select(p => p.Url));
        Assert.All(parsed.Pics, p => Assert.Null(p.Width)); // 专栏封面是裸字符串，宽高无从定格
    }

    [Fact]
    public void ParseFeed_DescEmote_CarriesBodyEmotes_ForwardMergesOrig()
    {
        // 正文 [表情] 兼容形态：desc.emote 字典（转义符→{url}），与评论区 content.emote 同款剥法（真 feed 主货架在
        // rich_text_nodes，另测锁形）；转发条顶层评语与原文都带表情时两边合并、按转义符去重。
        var withEmote = """
            {"id_str":"8880012","type":"DYNAMIC_TYPE_WORD","modules":{
              "module_author":{"mid":120,"name":"表情君","pub_ts":1700001200},
              "module_dynamic":{"desc":{"text":"今天也要元气满满[打call][脱单doge]",
                "emote":{"[打call]":{"url":"https://i0.hdslb.com/emote/call.png"},
                  "[脱单doge]":{"url":"//i0.hdslb.com/emote/doge.png"},"[坏掉]":{}}},"major":null}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(withEmote)).Items);
        // 只认带 url 的：空对象 [坏掉] 丢弃；协议相对链原样携带（落盘时缓存层只吃 http 前缀，存不下的热链兑底）。
        Assert.Equal(
            [("[打call]", "https://i0.hdslb.com/emote/call.png"), ("[脱单doge]", "//i0.hdslb.com/emote/doge.png")],
            parsed.Emotes);

        // 无表情的普通条：Emotes 留 null 不占座。
        var plain = """
            {"id_str":"8880014","type":"DYNAMIC_TYPE_WORD","modules":{
              "module_author":{"mid":120,"name":"素面君","pub_ts":1700001400},
              "module_dynamic":{"desc":{"text":"裸正文"},"major":null}}}
            """;
        Assert.Null(Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(plain)).Items).Emotes);

        // 转发条：顶层评语与原文的 emote 两边都剥，重复转义符只留首见的那份。
        var forward = """
            {"id_str":"8880013","type":"DYNAMIC_TYPE_FORWARD","modules":{
              "module_author":{"mid":223,"name":"转发君","pub_ts":1700001300},
              "module_dynamic":{"desc":{"text":"赞[打call]",
                "emote":{"[打call]":{"url":"https://i0.hdslb.com/emote/call.png"},
                  "[笑哭]":{"url":"https://i0.hdslb.com/emote/cry.png"}}},"major":null}},
             "orig":{"modules":{
              "module_author":{"mid":121,"name":"原作者","pub_ts":1700001250},
              "module_dynamic":{"desc":{"text":"原文[脱单doge]",
                "emote":{"[脱单doge]":{"url":"https://i0.hdslb.com/emote/doge.png"},
                  "[打call]":{"url":"https://i0.hdslb.com/emote/call2.png"}}},"major":null}}}}
            """;

        var fwd = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(forward)).Items);
        Assert.Equal(
            [("[打call]", "https://i0.hdslb.com/emote/call.png"), ("[笑哭]", "https://i0.hdslb.com/emote/cry.png"), ("[脱单doge]", "https://i0.hdslb.com/emote/doge.png")],
            fwd.Emotes);
    }

    [Fact]
    public void ParseFeed_RichTextNodes_EmojiNodesAreTheRealShelf()
    {
        // 真 feed 的正文表情货架（照 bilibili-API-collect space.md 响应示例锁形）：desc 里没有 emote 字典，
        // 表情挂在 desc.rich_text_nodes[] 的 RICH_TEXT_NODE_TYPE_EMOJI 节点（emoji.text 转义符 + emoji.icon_url 图链）——
        // 首轮只认字典形态时 body_emotes_json 全空（Bug2 回归锚）；顶层评语与原文（orig）两边都剥。
        var item = """
            {"id_str":"8880015","type":"DYNAMIC_TYPE_FORWARD","modules":{
              "module_author":{"mid":122,"name":"转发君","pub_ts":1700001500},
              "module_dynamic":{"desc":{"text":"哈哈[笑哭]",
                "rich_text_nodes":[
                  {"orig_text":"哈哈","text":"哈哈","type":"RICH_TEXT_NODE_TYPE_TEXT"},
                  {"emoji":{"icon_url":"https://i0.hdslb.com/bfs/emote/cry.png","size":1,"text":"[笑哭]","type":1},
                   "orig_text":"[笑哭]","text":"[笑哭]","type":"RICH_TEXT_NODE_TYPE_EMOJI"}]},"major":null}},
             "orig":{"modules":{
              "module_author":{"mid":123,"name":"原作者","pub_ts":1700001450},
              "module_dynamic":{"desc":{"text":"[坠落·空_自闭][坠落·空_哭泣]",
                "rich_text_nodes":[
                  {"emoji":{"icon_url":"https://i0.hdslb.com/bfs/emote/a.png","size":2,"text":"[坠落·空_自闭]","type":3},
                   "orig_text":"[坠落·空_自闭]","text":"[坠落·空_自闭]","type":"RICH_TEXT_NODE_TYPE_EMOJI"},
                  {"emoji":{"icon_url":"https://i0.hdslb.com/bfs/emote/b.png","size":2,"text":"[坠落·空_哭泣]","type":3},
                   "orig_text":"[坠落·空_哭泣]","text":"[坠落·空_哭泣]","type":"RICH_TEXT_NODE_TYPE_EMOJI"}]},"major":null}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        // 断言「转义符→url 清单非空」：这是实跑里全灭的那条链，锁死。
        Assert.NotNull(parsed.Emotes);
        Assert.NotEmpty(parsed.Emotes!);
        Assert.Equal(
            [("[笑哭]", "https://i0.hdslb.com/bfs/emote/cry.png"),
             ("[坠落·空_自闭]", "https://i0.hdslb.com/bfs/emote/a.png"),
             ("[坠落·空_哭泣]", "https://i0.hdslb.com/bfs/emote/b.png")],
            parsed.Emotes);
    }

    [Fact]
    public void ParseFeed_OpusSummary_RichTextNodesCarryEmojis()
    {
        // 图文卡正文表情挂在 major.opus.summary.rich_text_nodes（OPUS 卡型的实测货架）。
        var item = """
            {"id_str":"8880016","type":"DYNAMIC_TYPE_OPUS","modules":{
              "module_author":{"mid":124,"name":"图文君","pub_ts":1700001600},
              "module_dynamic":{"desc":null,"major":{"type":"MAJOR_TYPE_OPUS","opus":{
                "summary":{"text":"图文[打call]",
                  "rich_text_nodes":[
                    {"emoji":{"icon_url":"https://i0.hdslb.com/bfs/emote/call.png","text":"[打call]","type":1},
                     "orig_text":"图文[打call]","text":"图文[打call]","type":"RICH_TEXT_NODE_TYPE_EMOJI"}]},
                "pics":[{"url":"https://i0.hdslb.com/bfs/opus/a.jpg","width":800,"height":2000}]}}}}}
            """;

        var parsed = Assert.Single(LbsDynamicFetcher.ParseFeed(Feed(item)).Items);
        Assert.Equal(("[打call]", "https://i0.hdslb.com/bfs/emote/call.png"), Assert.Single(parsed.Emotes!));
        Assert.Equal((800, 2000), (Assert.Single(parsed.Pics).Width, Assert.Single(parsed.Pics).Height));
    }

    [Fact]
    public void ParsedPic_Serializes_CamelCaseEvenWithDefaultOptions()
    {
        // Bug1 回归锚：首轮 persist 闭包拿默认 options 裸奔，pics_json 落库成了 PascalCase（"Url"/"Width"/"Height"）。
        // [JsonPropertyName] 锁死后，即便序列化方忘了传 camelCase options，落库形态也必须是 camelCase。
        var json = System.Text.Json.JsonSerializer.Serialize(new List<ParsedPic> { new("https://i0.hdslb.com/x.jpg", 1576, 886) });
        Assert.Equal("""[{"url":"https://i0.hdslb.com/x.jpg","width":1576,"height":886}]""", json);
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
               "content":{"message":"热评一号[爱心]","emote":{"[爱心]":{"url":"https://i0.hdslb.com/emote/love.png"}}},
               "member":{"mid":9001,"uname":"月光","avatar":"https://i0.hdslb.com/f/1.jpg"}},
              {"rpid":112,"content":{"message":"   "},"member":{"mid":1}},
              {"rpid":0,"content":{"message":"没楼号"},"member":{"mid":2}}]}}
            """;

        var result = LbsDynamicFetcher.ParseReplies(json, rootRpid: 0, max: 20, collectRoots: true);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Equal(128, result.TotalCount); // 底栏评论数认 page.count，不认抽了几条
        var top = Assert.Single(result.Replies); // 空正文与没楼号的行开不了楼
        Assert.Equal(111, top.Rpid);
        Assert.Equal(0, top.RootRpid);
        Assert.Equal("月光", top.Name);
        // 老口 /x/v2/reply 的 member 只给 avatar 不给 face：两 key 都得认，不然是全场剃光头。
        Assert.Equal("https://i0.hdslb.com/f/1.jpg", top.Face);
        Assert.Equal(42, top.Like);
        Assert.Equal("2023-11-14", top.CtimeUtc![..10]);
        Assert.Equal([111L], result.Roots);
        // 表情清单：转义符→图 url 随正文自带，交给评论资产缓存落盘。
        Assert.Equal(("[爱心]", "https://i0.hdslb.com/emote/love.png"), Assert.Single(result.Emotes));
    }

    [Fact]
    public void ParseReplies_Level_NewBagFirst_OldKeyFallback()
    {
        // 等级取数口径：member.level_info.current_level 优先，老口没这个袋退 member.level，两个都没有 null（前端不渲染徽记）。
        var json = """
            {"code":0,"data":{"page":{"count":3},"replies":[
              {"rpid":401,"content":{"message":"新袋流"},"member":{"mid":1,"uname":"甲","level":2,"level_info":{"current_level":5}}},
              {"rpid":402,"content":{"message":"老口流"},"member":{"mid":2,"uname":"乙","level":3}},
              {"rpid":403,"content":{"message":"没等级流"},"member":{"mid":3,"uname":"丙"}}]}}
            """;

        var result = LbsDynamicFetcher.ParseReplies(json, rootRpid: 0, max: 20, collectRoots: false);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        Assert.Equal(5, Assert.Single(result.Replies, r => r.Rpid == 401).Level);
        Assert.Equal(3, Assert.Single(result.Replies, r => r.Rpid == 402).Level);
        Assert.Null(Assert.Single(result.Replies, r => r.Rpid == 403).Level);
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
    public void ParseReplies_Pictures_ParsedAlignedAndFiltered()
    {
        // 评论配图：content.pictures[].img_src 随手剥下（与视频侧归档器 ReplyPicture 同款模型）；
        // 只认绝对 http 链，非 http 与空 src 丢弃，单条封顶 9 张。
        var json = """
            {"code":0,"data":{"replies":[
              {"rpid":301,"content":{"message":"看图","pictures":[
                {"img_src":"https://i0.hdslb.com/new/1.jpg"},
                {"img_src":"//i0.hdslb.com/proto/2.jpg"},
                {"img_src":""},
                {"img_src":"https://i0.hdslb.com/new/3.jpg"},
                {"img_src":"https://i0.hdslb.com/new/4.jpg"},
                {"img_src":"https://i0.hdslb.com/new/5.jpg"},
                {"img_src":"https://i0.hdslb.com/new/6.jpg"},
                {"img_src":"https://i0.hdslb.com/new/7.jpg"},
                {"img_src":"https://i0.hdslb.com/new/8.jpg"},
                {"img_src":"https://i0.hdslb.com/new/9.jpg"},
                {"img_src":"https://i0.hdslb.com/new/10.jpg"},
                {"img_src":"https://i0.hdslb.com/new/11.jpg"},
                {"img_src":"https://i0.hdslb.com/new/12.jpg"}]},
               "member":{"mid":1,"uname":"甲"}},
              {"rpid":302,"content":{"message":"纯文"},"member":{"mid":2,"uname":"乙"}}]}}
            """;

        var result = LbsDynamicFetcher.ParseReplies(json, rootRpid: 0, max: 20, collectRoots: false);
        Assert.Equal(FeedParseStatus.Ok, result.Status);
        var withPics = Assert.Single(result.Replies, r => r.Rpid == 301);
        // 协议相对链不算绝对 http 链、空 src 丢；11 张合法图只留前 9 张（11/12 被封顶掉了）。
        Assert.Equal(
            ["https://i0.hdslb.com/new/1.jpg", "https://i0.hdslb.com/new/3.jpg", "https://i0.hdslb.com/new/4.jpg",
             "https://i0.hdslb.com/new/5.jpg", "https://i0.hdslb.com/new/6.jpg", "https://i0.hdslb.com/new/7.jpg",
             "https://i0.hdslb.com/new/8.jpg", "https://i0.hdslb.com/new/9.jpg", "https://i0.hdslb.com/new/10.jpg"],
            withPics.Pictures);
        Assert.Null(Assert.Single(result.Replies, r => r.Rpid == 302).Pictures);
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

        Assert.Equal(2, _db.UpsertComments("cd1",
            [new StationDb.DynamicCommentRow(501, 0, 0, 9, "楼", "https://i0.hdslb.com/f/9.jpg", "一楼", 3, null, "/avatars/abc.jpg",
                """["https://i0.hdslb.com/p/1.jpg","https://i0.hdslb.com/p/2.jpg"]""",
                """["/cmt-img/aaa.jpg",null]"""),
             new StationDb.DynamicCommentRow(502, 501, 501, 10, "客", null, "二楼", 0, "2026-01-02T00:00:00+00:00")]));
        _db.MarkCommentsFetched("cd1", 57, """{"[爱心]":"/emotes/love.png"}""");

        // 读回验列位：message/like/ctime/face_local 各归各位，表情映射随盖章定格。
        var tree = _db.ListDynamicComments("cd1");
        var topRow = Assert.Single(tree, r => r.Rpid == 501);
        Assert.Equal("一楼", topRow.Message);
        Assert.Equal(3, topRow.LikeCount);
        Assert.Equal("/avatars/abc.jpg", topRow.FaceLocal);
        // 配图两数组同序读回（单图失败存 null 元素）；无图楼两列都空。
        Assert.Equal("""["https://i0.hdslb.com/p/1.jpg","https://i0.hdslb.com/p/2.jpg"]""", topRow.PicturesJson);
        Assert.Equal("""["/cmt-img/aaa.jpg",null]""", topRow.PicturesLocalJson);
        var subRow = Assert.Single(tree, r => r.Rpid == 502);
        Assert.Equal("二楼", subRow.Message);
        Assert.Equal(501, subRow.RootRpid);
        Assert.Equal("2026-01-02T00:00:00+00:00", subRow.CtimeUtc);
        Assert.Null(subRow.PicturesJson);
        Assert.Equal("""{"[爱心]":"/emotes/love.png"}""", _db.GetDynamicEmotesJson("cd1"));

        // 图片资产回写：九宫格本地数组（含失败 null 洞）、封面前本地、正文表情映射各归各位。
        _db.SetDynamicAssetLocals("cd1", """["/cmt-img/p1.jpg",null]""", "/cmt-img/cover.jpg", """{"[打call]":"/emotes/call.png"}""");

        // 盖章后退出欠账名单；底栏评论数从 page.count 回写（动态流不带 stat，巡捕回执是唯一来源）。
        Assert.Empty(_db.ListPendingCommentDynamics());
        var row = Assert.Single(_db.ListDynamics(7001));
        Assert.Equal(57, row.StatComment);
        Assert.NotNull(row.CommentsFetchedAtUtc);
        Assert.Equal("""["/cmt-img/p1.jpg",null]""", row.PicsLocalJson);
        Assert.Equal("/cmt-img/cover.jpg", row.CoverLocal);
        Assert.Equal("""{"[打call]":"/emotes/call.png"}""", row.BodyEmotesJson);
    }

    // ─── 动态配图补员（DynamicPicBackfillWorker）：候选筛选 / 单列写回 / 同序组装 ───

    private const string ThreePics = """[{"url":"https://i0.hdslb.com/bfs/d/a.jpg","width":800,"height":600},"""
        + """{"url":"https://i0.hdslb.com/bfs/d/b.jpg"},"""
        + """{"url":"https://i0.hdslb.com/bfs/d/c.jpg"}]""";

    /// <summary>塞一条带 pics_json 的动态（补员候选的原料）。</summary>
    private void Seed(string dynamicId, string? picsJson, string? picsLocalJson = null, long targetMid = 9001)
    {
        _db.UpsertDynamic(dynamicId, targetMid, targetMid, "图图", "DYNAMIC_TYPE_OPUS", "图文", picsJson, null, "2026-01-01T00:00:00+00:00");
        if (picsLocalJson is not null)
        {
            _db.SetDynamicPicsLocal(dynamicId, picsLocalJson);
        }
    }

    [Fact]
    public void ListDynamicsMissingPics_PicksRowsWithPicsButLocalHoles()
    {
        Seed("p-null", ThreePics); // 本地板整列 NULL（10-28 后动态图全灭的常见形状）
        Seed("p-allnull", ThreePics, "[null,null,null]"); // 全 null 数组：一张没存上
        Seed("p-partial", ThreePics, """["/cmt-img/a.jpg",null,"/cmt-img/c.jpg"]"""); // 部分落地，中间有洞
        Seed("p-full", ThreePics, """["/cmt-img/a.jpg","/cmt-img/b.jpg","/cmt-img/c.jpg"]"""); // 全落地 → 必须排除
        Seed("p-nopics", null); // 没图动态 → 必须排除
        Seed("p-empty", ""); // pics_json 空字串 → 必须排除
        Seed("p-emptyarr", "[]"); // pics_json 空数组 → 必须排除
        Seed("p-pascal", """[{"Url":"https://i0.hdslb.com/bfs/d/x.jpg","Width":800}]"""); // PascalCase 脏行也要进候选

        var ids = _db.ListDynamicsMissingPics(40).Select(r => r.DynamicId).ToList();
        Assert.Equal(["p-null", "p-allnull", "p-partial", "p-pascal"], ids);

        // 候选自证：把 pics_json 与现有本地板一并带回，worker 据此判哪几位还没下。
        var partial = _db.ListDynamicsMissingPics(40).Single(r => r.DynamicId == "p-partial");
        Assert.Equal(ThreePics, partial.PicsJson);
        Assert.Equal("""["/cmt-img/a.jpg",null,"/cmt-img/c.jpg"]""", partial.PicsLocalJson);
        Assert.Equal("p-null", _db.ListDynamicsMissingPics(40).First().DynamicId); // id 列随候选带回
        Assert.True(_db.ListDynamicsMissingPics(40).First().Id > 0);
    }

    [Fact]
    public void ListDynamicsMissingPics_OrdersOldestFirst_AndClampsLimit()
    {
        for (var i = 1; i <= 5; i++)
        {
            Seed($"ord-{i}", ThreePics);
        }

        // 老货优先（ORDER BY id 升序，没 attempts 列就靠它让补不上的行自然重逢）。
        var all = _db.ListDynamicsMissingPics(40);
        Assert.Equal(["ord-1", "ord-2", "ord-3", "ord-4", "ord-5"], all.Select(r => r.DynamicId));
        Assert.True(all.Select(r => r.Id).SequenceEqual(all.Select(r => r.Id).OrderBy(x => x)));
        Assert.Equal(["ord-1", "ord-2"], _db.ListDynamicsMissingPics(2).Select(r => r.DynamicId));

        // limit 下限 Clamp 到 1：传 0 不能变成「不限量」或空转。
        var floored = Assert.Single(_db.ListDynamicsMissingPics(0));
        Assert.Equal("ord-1", floored.DynamicId);
    }

    [Fact]
    public void ListDynamicsMissingPics_ClampsLimitAt200()
    {
        for (var i = 1; i <= 205; i++)
        {
            Seed($"big-{i}", ThreePics);
        }

        // 上限 Clamp 到 200：传 999 也不能一次捞光（worker 每轮只吃 40，这里守的是 Infrastructure 那道兼顶）。
        Assert.Equal(200, _db.ListDynamicsMissingPics(999).Count);
    }

    [Fact]
    public void SetDynamicPicsLocal_WritesOnlyThatColumn()
    {
        Seed("one-col", ThreePics, """[null,"/cmt-img/b.jpg",null]""");
        _db.SetDynamicAssetLocals("one-col", """[null,"/cmt-img/b.jpg",null]""", "/cmt-img/cover.jpg", """{"[打call]":"/emotes/call.png"}""");

        // 补档写回只碰 pics_local_json：卡面封面前本地与正文表情映射原样还在。
        _db.SetDynamicPicsLocal("one-col", """["/cmt-img/a.jpg","/cmt-img/b.jpg",null]""");
        var row = Assert.Single(_db.ListDynamics(9001, 50), r => r.DynamicId == "one-col");
        Assert.Equal("""["/cmt-img/a.jpg","/cmt-img/b.jpg",null]""", row.PicsLocalJson);
        Assert.Equal("/cmt-img/cover.jpg", row.CoverLocal);
        Assert.Equal("""{"[打call]":"/emotes/call.png"}""", row.BodyEmotesJson);

        // 对照组（为何不许复用 SetDynamicAssetLocals 补档）：同一根 UPDATE 写三列，null 也直写，一下就把另两列抹了。
        Seed("three-cols", ThreePics, """[null,null,null]""");
        _db.SetDynamicAssetLocals("three-cols", """[null,"/cmt-img/b.jpg",null]""", "/cmt-img/cover.jpg", """{"[打call]":"/emotes/call.png"}""");
        _db.SetDynamicAssetLocals("three-cols", """["/cmt-img/a.jpg","/cmt-img/b.jpg","/cmt-img/c.jpg"]""", null, null);
        var wiped = Assert.Single(_db.ListDynamics(9001, 50), r => r.DynamicId == "three-cols");
        Assert.Null(wiped.CoverLocal);
        Assert.Null(wiped.BodyEmotesJson);
    }

    [Fact]
    public void BackfillComposePicsLocal_KeepsFilledPositions_AndNullsFailures()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // a.jpg 那位已有本地板（还是张旧图）：map 里就算另有答案也不许覆盖。
            ["https://i0.hdslb.com/bfs/d/a.jpg"] = "/cmt-img/DIFFERENT.jpg",
            ["https://i0.hdslb.com/bfs/d/b.jpg"] = "/cmt-img/b.jpg",
            // c.jpg 故意不在 map 里：单张失败。
        };

        // 现有本地板比 pics_json 短一位：保留已落地那位，补上 b.jpg，c.jpg 落 null，数组长度与 pics_json 一致。
        var next = DynamicPicBackfillWorker.ComposePicsLocal(
            ThreePics, """["/cmt-img/old-a.jpg",null]""", map);
        Assert.NotNull(next);
        var items = next!;
        Assert.Equal(["/cmt-img/old-a.jpg", "/cmt-img/b.jpg", null], items);
        Assert.Equal(3, items.Count);

        // PascalCase 脏行（早期拿默认 options 裸奔入库的那批）也能读出 url。
        var pascal = DynamicPicBackfillWorker.ComposePicsLocal(
            """[{"Url":"https://i0.hdslb.com/bfs/d/x.jpg","Width":800}]""",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["https://i0.hdslb.com/bfs/d/x.jpg"] = "/cmt-img/x.jpg" });
        Assert.NotNull(pascal);
        Assert.Equal(["/cmt-img/x.jpg"], pascal!);

        // 坏 JSON / 空数组 / NULL ⇒ 整行今天办不了（回 null），与「补了 0 位」两回事。
        Assert.Null(DynamicPicBackfillWorker.ComposePicsLocal("这不是 JSON", null, map));
        Assert.Null(DynamicPicBackfillWorker.ComposePicsLocal("[]", null, map));
        Assert.Null(DynamicPicBackfillWorker.ComposePicsLocal(null, null, map));

        // 本轮新补上的位数：旧值 NULL 视作全空位；一位没变回 0（worker 据此不写库）。
        Assert.Equal(1, DynamicPicBackfillWorker.CountNewlyFilled("""["/cmt-img/old-a.jpg",null]""", items));
        Assert.Equal(3, DynamicPicBackfillWorker.CountNewlyFilled(null, ["/cmt-img/a.jpg", "/cmt-img/b.jpg", "/cmt-img/c.jpg"]));
        Assert.Equal(0, DynamicPicBackfillWorker.CountNewlyFilled("""["/cmt-img/a.jpg",null]""", ["/cmt-img/a.jpg", null]));
    }

    [Fact]
    public void BackfillPendingPicUrls_OnlyAsksForEmptySlots()
    {
        // 已有本地路径的位不重下（幂等：同一行进两轮，第二轮只吐还空着的那位）。
        Assert.Equal(
            ["https://i0.hdslb.com/bfs/d/b.jpg"],
            DynamicPicBackfillWorker.PendingPicUrls(ThreePics, """["/cmt-img/a.jpg",null,"/cmt-img/c.jpg"]"""));

        // 全落地 ⇒ 没得下；本地板 NULL ⇒ 三位全要下。
        Assert.Empty(DynamicPicBackfillWorker.PendingPicUrls(ThreePics, """["/cmt-img/a.jpg","/cmt-img/b.jpg","/cmt-img/c.jpg"]"""));
        Assert.Equal(3, DynamicPicBackfillWorker.PendingPicUrls(ThreePics, null).Count);

        // 同 url 重复占两位只下一张；坏链（没 url / 不是 http）不吐；pics_json 读不出 ⇒ 空表。
        var dupe = """[{"url":"https://i0.hdslb.com/bfs/d/a.jpg"},{"url":"https://i0.hdslb.com/bfs/d/a.jpg"},"""
            + """{"width":1},{"url":"i0.hdslb.com/bfs/d/d.jpg"}]""";
        Assert.Equal(["https://i0.hdslb.com/bfs/d/a.jpg"], DynamicPicBackfillWorker.PendingPicUrls(dupe, null));
        Assert.Empty(DynamicPicBackfillWorker.PendingPicUrls("这不是 JSON", null));

        // 旧值坏 JSON 视作没存过，三位全重下。
        Assert.Equal(3, DynamicPicBackfillWorker.PendingPicUrls(ThreePics, "{不是数组}").Count);
    }
}
