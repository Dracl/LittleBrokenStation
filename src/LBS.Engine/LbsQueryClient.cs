using DownKyi.Application.Bilibili;

namespace LBS.Engine;

/// <summary>
/// 带分号分流的查询客户端（装饰器，实现 IBilibiliApiClient）：LBS 采集器（UP 名片、view 统计、
/// 评论、收藏夹导入）显式注入本类——cookie 跟着调用链的环境号走（提交链内天然在视频归属号上，
/// API 入口用 RunAsAsync 显式进入），副号未配则回落站长主号的上游客户端。
/// 实现接口是为了上游 VideoInfo/FavoritesResource 那些扩展方法在本实例上照常可用（内部走到分流后的 GET）。
/// </summary>
public sealed class LbsQueryClient(
    IBilibiliApiClient main,
    LbsAltLoginManager alt,
    LbsAccountRouter router,
    LbsApiThrottle throttle) : IBilibiliApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string? CurrentCookie() => alt.ForOwner(router.Effective(router.Current));

    private static Uri GetUri(BilibiliHttpRequest request) => new(request.RequestAddress, UriKind.Absolute);

    public async Task<string> GetStringAsync(BilibiliHttpRequest request, CancellationToken cancellationToken)
    {
        var uri = GetUri(request);
        await throttle.EnterAsync(uri, cancellationToken);
        var cookie = CurrentCookie();
        if (cookie is null)
        {
            var body = await main.GetStringAsync(request, cancellationToken);
            // 主号回落链拿不到 HTTP 状态码，靠 body 认 -352（B 站风控多数仍回 200 + body 报错）。
            throttle.ReportResult(uri, body, null);
            return body;
        }

        using var res = await SendAsync(request, cookie, cancellationToken);
        var text = await res.Content.ReadAsStringAsync(cancellationToken);
        throttle.ReportResult(uri, text, (int)res.StatusCode);
        return text;
    }

    public async Task<Stream> OpenReadAsync(BilibiliHttpRequest request, CancellationToken cancellationToken)
    {
        await throttle.EnterAsync(GetUri(request), cancellationToken);
        var cookie = CurrentCookie();
        if (cookie is null)
        {
            return await main.OpenReadAsync(request, cancellationToken);
        }

        // 流式返回：HttpResponseMessage 随流一起释放，调用方 dispose 本流即断开连接。
        var res = await SendAsync(request, cookie, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        return new AsyncDisposableReadChannel(res, await res.Content.ReadAsStreamAsync(cancellationToken));
    }

    public async Task DownloadFileAsync(BilibiliHttpRequest request, string destinationPath, CancellationToken cancellationToken)
    {
        await throttle.EnterAsync(GetUri(request), cancellationToken);
        var cookie = CurrentCookie();
        if (cookie is null)
        {
            await main.DownloadFileAsync(request, destinationPath, cancellationToken);
            return;
        }

        using var res = await SendAsync(request, cookie, cancellationToken);
        await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await res.Content.CopyToAsync(fs, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        BilibiliHttpRequest request,
        string cookie,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, request.RequestAddress);
        if (request.Referer is not null)
        {
            req.Headers.Referrer = new Uri(request.Referer);
        }

        // 与上游客户端对齐的头组合：origin + cookie，UA 与 Sec-CH-UA 族走随机指纹池（LbsBrowserFingerprints）。
        req.Headers.Add("origin", "https://www.bilibili.com");
        req.Headers.Add("cookie", cookie);
        LbsBrowserFingerprints.Apply(req);
        var res = await Http.SendAsync(req, completion, cancellationToken);
        // 风控信号在抛异常前先交卷，不然 412/429 被 EnsureSuccessStatusCode 吞成普通失败，熔断永远不响。
        throttle.ReportResult(GetUri(request), null, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
        return res;
    }

    /// <summary>把 response 的生命周期挂到流上：调用方只管 dispose 流，不会漏掉响应。</summary>
    private sealed class AsyncDisposableReadChannel(HttpResponseMessage response, Stream inner) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask DisposeAsync()
        {
            return DisposeAsyncCore();
        }

        private async ValueTask DisposeAsyncCore()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
