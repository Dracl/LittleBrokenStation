using System.Net;
using System.Text;
using LBS.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace LBS.Tests;

/// <summary>
/// 扫码登录协议层（桩 HTTP）：取码、轮询状态机（0/86038/86090/86101）、
/// 成功瞬间的 Set-Cookie 收割、nav 验明正身。不发真请求。
/// </summary>
public class LbsQrLoginTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static LbsQrLogin ClientWith(HttpResponseMessage response, out StubHandler stub)
    {
        stub = new StubHandler(response);
        return new LbsQrLogin(NullLogger<LbsQrLogin>.Instance, stub);
    }

    [Fact]
    public async Task NewChallenge_Ok_ReturnsUrlAndKey()
    {
        var client = ClientWith(Json("""
            {"code":0,"message":"0","data":{"url":"https://passport.bilibili.com/h5-project/qrcode?scene=abc","qrcode_key":"KEY123"}}
            """), out var stub);

        var challenge = await client.NewChallengeAsync(CancellationToken.None);

        Assert.NotNull(challenge);
        Assert.Equal("KEY123", challenge!.QrcodeKey);
        Assert.StartsWith("https://passport.bilibili.com/", challenge.Url);
        Assert.NotEmpty(stub.Requests[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task NewChallenge_BilibiliRejects_ReturnsNull()
    {
        var client = ClientWith(Json("""{"code":-101,"message":"账号未登录","data":null}"""), out _);
        Assert.Null(await client.NewChallengeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(86101, QrPhase.Waiting)]
    [InlineData(86090, QrPhase.Scanned)]
    [InlineData(86038, QrPhase.Expired)]
    [InlineData(1994, QrPhase.Unknown)]
    public async Task Poll_TranslatesBilibiliCodes(int code, QrPhase expected)
    {
        var client = ClientWith(Json(
            "{\"code\":0,\"data\":{\"url\":\"\",\"refresh_token\":\"\",\"timestamp\":0,\"code\":" + code + ",\"message\":\"m\"}}"), out _);

        var result = await client.PollAsync("KEY", CancellationToken.None);
        Assert.Equal(expected, result.Phase);
    }

    [Fact]
    public async Task Poll_Success_HarvestsLoginCookies()
    {
        var response = Json("""
            {"code":0,"data":{"url":"https://www.bilibili.com","refresh_token":"RT-9","timestamp":1,"code":0,"message":"成功后跳转"}}
            """);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "SESSDATA=abc123; Path=/; Domain=.bilibili.com; HttpOnly");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "bili_jct=jct456; Path=/; Domain=.bilibili.com");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "DedeUserID=10086; Path=/; Domain=.bilibili.com");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "DedeUserID__ckMd5=md5x; Path=/; Domain=.bilibili.com");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "sid=websid; Path=/; Domain=.bilibili.com");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "buvid3=noise; Path=/; Domain=.bilibili.com");

        var client = ClientWith(response, out _);
        var result = await client.PollAsync("KEY", CancellationToken.None);

        Assert.Equal(QrPhase.Success, result.Phase);
        Assert.Equal("RT-9", result.RefreshToken);
        Assert.NotNull(result.CookieHeader);
        Assert.Contains("SESSDATA=abc123", result.CookieHeader);
        Assert.Contains("bili_jct=jct456", result.CookieHeader);
        Assert.Contains("DedeUserID=10086", result.CookieHeader);
        // 噪声 cookie 不收割
        Assert.DoesNotContain("buvid3", result.CookieHeader);
    }

    [Fact]
    public async Task Poll_SuccessWithoutSessdata_IsUnknown()
    {
        var response = Json("""
            {"code":0,"data":{"url":"","refresh_token":"","timestamp":0,"code":0,"message":"?"}}
            """);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "sid=websid; Path=/");

        var client = ClientWith(response, out _);
        var result = await client.PollAsync("KEY", CancellationToken.None);

        Assert.Equal(QrPhase.Unknown, result.Phase);
        Assert.Null(result.CookieHeader);
    }

    [Fact]
    public async Task ProbeIdentity_LoggedIn_ReturnsIdentity()
    {
        var client = ClientWith(Json("""
            {"code":0,"data":{"isLogin":true,"mid":10086,"uname":"小王","face":"https://face/x.jpg","vipStatus":0}}
            """), out var stub);

        var identity = await client.ProbeIdentityAsync("SESSDATA=abc", CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(10086L, identity!.Mid);
        Assert.Equal("小王", identity.Name);
        // cookie 必须走请求头而不是 URL
        Assert.DoesNotContain("SESSDATA", stub.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ProbeIdentity_NotLoggedIn_ReturnsNull()
    {
        var client = ClientWith(Json("""
            {"code":-101,"message":"账号未登录","data":{"isLogin":false}}
            """), out _);
        Assert.Null(await client.ProbeIdentityAsync("SESSDATA=stale", CancellationToken.None));
    }
}
