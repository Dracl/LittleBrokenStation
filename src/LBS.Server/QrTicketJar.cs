using System.Collections.Concurrent;
using LBS.Engine;

namespace LBS.Server;

/// <summary>
/// 扫码票的一枚：谁在哪台机器上取的、取的哪张、干什么用。
/// Phase / LastPollUtc 可变：轮询要在两次代问之间记住当前状态与上次代问的时刻。
/// </summary>
public sealed record QrSession(string SourceIp, string? Invite, DateTimeOffset CreatedUtc, string Mode, long? OwnerMemberId)
{
    public DateTimeOffset LastPollUtc { get; set; }

    /// <summary>阶段直接吃 Engine 的 QrPhase：票池与代客轮询说的是同一件事，不该各持一套枚举。</summary>
    public QrPhase Phase { get; set; } = QrPhase.Waiting;
}

/// <summary>
/// 票上那句人话的唯一出处。三张脸必须各说各的事，前端才拆得开、测试才钉得住：
/// 分站自己把票忘了、票在分站手里超时、以及 B 站说这张码废了，是三件事，不该糊成一句。
/// </summary>
public static class QrMessages
{
    /// <summary>
    /// 票池是内存态，重启即清空；bind 重开顶旧票与已销过也会走到这里——查无此票有三条来路，
    /// 所以这句话只说「不作数了」并把最常见的那条摆在括号里，不拿「重启」去冒充全部三种真相。
    /// </summary>
    public const string TicketGone = "这张码已经不作数了（分站刚重启过，或你已经换过新的），换一张就行";

    /// <summary>票还在分站手里，只是过了自己定的寿命。</summary>
    public const string TtlExpired = "二维码超时，请重新获取";

    /// <summary>B 站自己回了失效（86038），拿它的话优先。</summary>
    public const string UpstreamExpired = "B 站说这张二维码已失效";

    /// <summary>只在总闸真满时说一句实话，不再拿「排队」糊弄反复开弹窗的自己人。</summary>
    public const string TooManyWaiting = "同时等着扫码的人太多，稍候再试";
}

/// <summary>
/// 扫码票池（内存态，随进程生死）：原先是 MapMemberEndpoints 里的一个闭包字典，
/// 闭包没法 new 出来单测，那些「一个人反复开弹窗会不会把自己锁在门外」的账就只能靠读码相信它对。
/// 搬出来后计数按发起人而非来路：bind 票认席位（一人一张，重开顶掉自己旧票），
/// admit 票认 IP（超限时踢该 IP 最旧那张放行），而防陌生人刷码的总闸留着。
/// </summary>
public sealed class QrTicketJar
{
    private readonly ConcurrentDictionary<string, QrSession> _sessions = new();

    /// <summary>池里此刻有多少张活票。</summary>
    public int Total => _sessions.Count;

    /// <summary>取一枚新票。键是 B 站给的 qrcode_key，重复取同一枚按覆盖算（同一张码不该有两份状态）。</summary>
    public void Add(string key, string sourceIp, string? invite, string mode, long? ownerMemberId, DateTimeOffset now)
    {
        _sessions[key] = new QrSession(sourceIp, invite, now, mode, ownerMemberId);
    }

    /// <summary>凭 key 找票；查无此票（多半是分站重启过）返回 false。</summary>
    public bool TryGet(string key, out QrSession? session) => _sessions.TryGetValue(key, out session);

    /// <summary>销一枚票，成败与否都只说「好了」——不给外面套话的空间（存在性信号）。</summary>
    public bool Remove(string key) => _sessions.TryRemove(key, out _);

    /// <summary>清掉过了寿命的票，返回清了几张。取码前先扫一遍，免得过期的还占着坑。</summary>
    public int SweepStale(DateTimeOffset now, TimeSpan ttl)
    {
        var removed = 0;
        foreach (var stale in _sessions.Where(kv => now - kv.Value.CreatedUtc > ttl).Select(kv => kv.Key).ToList())
        {
            if (_sessions.TryRemove(stale, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>这台来路上有几张活票（陌生人刷码的计量单位）。</summary>
    public int CountByIp(string ip) =>
        _sessions.Values.Count(s => string.Equals(s.SourceIp, ip, StringComparison.Ordinal));

    /// <summary>
    /// 把某席位其余的票清掉，只留刚取的这张：一人一张。
    /// 「开弹窗 → 没扫 → 关掉 → 再开」是正常动作，按人算就不该因此把自己挤爆。
    /// </summary>
    public int RemoveOthersOfMember(long memberId)
    {
        var removed = 0;
        foreach (var stale in _sessions.Where(kv => kv.Value.OwnerMemberId == memberId).Select(kv => kv.Key).ToList())
        {
            if (_sessions.TryRemove(stale, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// 踢掉这台来路上最旧的一张，腾坑放行。同人超限不该吃 429——他只是在换一张码；
    /// 陌生人真要用脚本刷，最旧那张被踢就等于票没攒下来，刷的意图仍然落空。
    /// </summary>
    public bool RemoveOldestByIp(string ip)
    {
        var oldest = _sessions
            .Where(kv => string.Equals(kv.Value.SourceIp, ip, StringComparison.Ordinal))
            .OrderBy(kv => kv.Value.CreatedUtc)
            .Select(kv => (string?)kv.Key)
            .FirstOrDefault();

        return oldest is not null && _sessions.TryRemove(oldest, out _);
    }
}
