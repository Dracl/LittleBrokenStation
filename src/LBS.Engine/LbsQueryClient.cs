using DownKyi.Application.Bilibili;

namespace LBS.Engine;

/// <summary>
/// 带出号能力的查询客户端（实现 IBilibiliApiClient）：LBS 采集器（UP 名片、view 统计、
/// 评论、收藏夹导入、动态）显式注入本类——cookie 跟着调用链的环境号走（提交链内天然在投稿人号上，
/// 采集入口由 LbsCollectPolicy 显式钉在采集号上）。链上没号就当场判无号，不再拿匿名请求冒充数据。
/// 实现接口是为了上游 VideoInfo/FavoritesResource 那些扩展方法在本实例上照常可用（内部走到分流后的 GET）。
/// </summary>
public sealed class LbsQueryClient(LbsAccountRouter router, LbsApiThrottle throttle) : IBilibiliApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string? CurrentCookie() => router.CurrentCookie;

    /// <summary>没号就别把请求发出去：匿名打 B 站接口只会被 -101/-352 打回，还可能把 cookie 拖进风控。</summary>
    private string RequireCookie() => CurrentCookie() ?? throw new LbsNoAccountException(
        "采集这条请求没有可用账号：站里的活吃采集号（站长席位的活号优先），请先在「个人空间」绑一个未过期的 B 站号。");

    private static Uri GetUri(BilibiliHttpRequest request) => new(request.RequestAddress, UriKind.Absolute);

    public async Task<string> GetStringAsync(BilibiliHttpRequest request, CancellationToken cancellationToken)
    {
        var uri = GetUri(request);
        await throttle.EnterAsync(uri, cancellationToken);
        var cookie = RequireCookie();
        using var res = await SendAsync(request, cookie, cancellationToken);
        var text = await res.Content.ReadAsStringAsync(cancellationToken);
        throttle.ReportResult(uri, text, (int)res.StatusCode);
        return text;
    }

    public async Task<Stream> OpenReadAsync(BilibiliHttpRequest request, CancellationToken cancellationToken)
    {
        await throttle.EnterAsync(GetUri(request), cancellationToken);
        var cookie = RequireCookie();

        // 流式返回：HttpResponseMessage 随流一起释放，调用方 dispose 本流即断开连接。
        var res = await SendAsync(request, cookie, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        return new AsyncDisposableReadChannel(res, await res.Content.ReadAsStreamAsync(cancellationToken));
    }

    public async Task DownloadFileAsync(BilibiliHttpRequest request, string destinationPath, CancellationToken cancellationToken)
    {
        await throttle.EnterAsync(GetUri(request), cancellationToken);
        var cookie = RequireCookie();
        using var res = await SendAsync(request, cookie, cancellationToken);

        // 先写同目录的 <名>.<guid8>.tmp，验过货再 File.Move 转正。
        // 以前是 FileMode.Create 直写目标路径：下载中途断网/取消/进程被杀，目标路径上就躺着一副
        // 空壳或半截图，而这些缓存件按 sha1(url) 命名——下次 File.Exists 直接当命中，坏件就永久住下了
        // （头像/表情/评论图三区都中过这一招）。同目录改名不多拷一份。
        // 名字留 .tmp 结尾：静态区那道半成品闸（Program.cs 的 IsStaleTransientPath）认的就是这个后缀。
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{destinationPath}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await res.Content.CopyToAsync(fs, cancellationToken);
            }

            // 空壳不算到货：宁可回一句失败让调用方重下，也不给缓存区留一个「存在但 0 字节」的陷阱。
            if (new FileInfo(temp).Length == 0)
            {
                throw new IOException("文件抓回来是个空壳（0 字节），不算到货");
            }

            File.Move(temp, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemp(temp);
        }
    }

    /// <summary>残件删不动只是占个位：名字带 guid，下一单不会撞上它，静态区也拿不到它。</summary>
    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (IOException)
        {
        }
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
