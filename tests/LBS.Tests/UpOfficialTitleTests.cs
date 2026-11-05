using System.Text.Json;
using LBS.Engine;
using Xunit;

namespace LBS.Tests;

/// <summary>
/// card.official.title 的判型口径（11-05 真机对版破案）：B 站的个人认证走 <c>type=0</c>，
/// 无认证才是 <c>type=-1</c>（实测：无认证 UP 一律 {role:0,title:"",type:-1}；
/// 七禾いえ「bilibili直播高能主播」与影视飓风「2025百大…」都是 type=0 的真认证）。
/// 早先那版把 type==0 当无认证一票否决，13 位在册 UP 的认证全被杀成 NULL —— 这几格就是钉死这个方向的。
/// </summary>
public sealed class UpOfficialTitleTests
{
    private static string? Parse(string cardJson) =>
        LbsUpInfoFetcher.OfficialTitle(JsonDocument.Parse(cardJson).RootElement);

    [Fact]
    public void 个人认证type0_title照收()
    {
        // 七禾いえ真身响应（2026-11-05 实抓）。
        Assert.Equal("bilibili直播高能主播",
            Parse("""{"name":"七禾いえ","official":{"role":7,"title":"bilibili直播高能主播","desc":"","type":0}}"""));
    }

    [Fact]
    public void 无认证type负1_回null()
    {
        // 千夜QYS3 / mw626576 真身响应（2026-11-05 实抓）：type=-1 且 title 本就空。
        Assert.Null(Parse("""{"official":{"role":0,"title":"","desc":"","type":-1}}"""));
    }

    [Fact]
    public void official对象缺省_回null()
    {
        Assert.Null(Parse("""{"name":"无认证老档案"}"""));
    }

    [Fact]
    public void 登录态形态official被藏成null_回null()
    {
        // 采集号 cookie 下的 card 实测形态（11-05 破案）：字段在位但值是 null —— 判型不能对 null 意外，
        // 真正的货由 ResolveOfficialAsync 的匿名补读送来。
        Assert.Null(Parse("""{"name":"七禾いえ","official":null}"""));
    }

    [Fact]
    public void 匿名另一副面孔_officialVerifydesc照收()
    {
        // 站里匿名补读实拍（11-05 TEMP-DIAG）：official 缺席/被空，认证装在 official_verify.desc。
        Assert.Equal("bilibili直播高能主播",
            Parse("""{"name":"小七anna","official_verify":{"type":0,"desc":"bilibili直播高能主播"}}"""));
        Assert.Equal("bilibili知名UP主",
            Parse("""{"official":null,"official_verify":{"type":0,"desc":"bilibili知名UP主"}}"""));
    }

    [Fact]
    public void officialVerify型是负1_也算没认证()
    {
        // 无认证的另一副面孔（实拍）：{type:-1,desc:""}。
        Assert.Null(Parse("""{"official_verify":{"type":-1,"desc":""}}"""));
    }

    [Fact]
    public void type不是负1但title空_也算没认证()
    {
        // title 是页头唯一要画的字，空 title 无论 type 几何都不值得画一枚空章。
        Assert.Null(Parse("""{"official":{"role":0,"title":"   ","desc":"","type":0}}"""));
    }

    [Fact]
    public void title带首尾空白_收进来先trim()
    {
        Assert.Equal("课堂优质讲师",
            Parse("""{"official":{"role":7,"title":" 课堂优质讲师 ","desc":"","type":0}}"""));
    }
}
