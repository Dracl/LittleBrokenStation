using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 扫码票池（<see cref="QrTicketJar"/>）那几笔账。票原先住在 MapMemberEndpoints 的闭包字典里，
/// 闭包 new 不出来，于是「一个人反复开弹窗会不会把自己锁在门外」只能靠读码相信它对；搬成类才钉得住。
/// 钉的是：① bind 一人一张，重开顶掉自己旧票（站长连开五次弹窗不该撞 429）；
/// ② admit 同人超限踢最旧那张放行，per-IP 不再回 429；③ 总闸与 per-IP 是两道闸，计数别串味；
/// ④ 过期只清超 TTL 的，健康票一张不许误伤；⑤ 退票口四种结局同形（不给存在性信号）；
/// ⑥ 三张作废脸各说各的事——分站把票忘了、票在分站手里超时、B 站说码废了，不能糊成一句。
/// 真出去问 B 站领码/代 poll 那两段要网络与活号，不在本文件。
/// </summary>
public sealed class QrTicketJarTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 4, 0, 0, TimeSpan.FromHours(8));
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);

    private readonly QrTicketJar _jar = new();

    [Fact]
    public void bind票一人一张_重开顶掉自己旧票()
    {
        _jar.Add("k1", "127.0.0.1", null, "bind", 7, T0);
        Assert.Equal(1, _jar.Total);

        // 站长关掉弹窗又开：新票落地前先把自己其余的票清掉，池里永远只剩他这一张。
        _jar.RemoveOthersOfMember(7);
        _jar.Add("k2", "127.0.0.1", null, "bind", 7, T0.AddSeconds(20));
        Assert.Equal(1, _jar.Total);
        Assert.False(_jar.TryGet("k1", out _));
        Assert.True(_jar.TryGet("k2", out var kept));
        Assert.Equal(7, kept!.OwnerMemberId);
    }

    [Fact]
    public void bind顶票只顶自己_别人的票一张不许动()
    {
        _jar.Add("mine", "127.0.0.1", null, "bind", 7, T0);
        _jar.Add("theirs", "10.0.0.9", null, "bind", 8, T0);
        _jar.Add("guest", "10.0.0.9", null, "admit", null, T0);

        Assert.Equal(1, _jar.RemoveOthersOfMember(7));
        Assert.True(_jar.TryGet("theirs", out _));
        Assert.True(_jar.TryGet("guest", out _));
        Assert.Equal(2, _jar.Total);
    }

    [Fact]
    public void admit同人超限踢最旧_腾出坑来而不是锁人()
    {
        for (var i = 0; i < 6; i++)
        {
            _jar.Add($"k{i}", "127.0.0.1", null, "admit", null, T0.AddSeconds(i));
        }

        Assert.Equal(6, _jar.CountByIp("127.0.0.1"));

        // 端点那边的规矩是 while (CountByIp >= 上限 && RemoveOldestByIp()) { }：踢到腾出坑为止。
        Assert.True(_jar.RemoveOldestByIp("127.0.0.1"));
        Assert.False(_jar.TryGet("k0", out _), "最旧那张该被踢掉");
        Assert.Equal(5, _jar.CountByIp("127.0.0.1"));

        _jar.Add("k6", "127.0.0.1", null, "admit", null, T0.AddSeconds(30));
        Assert.Equal(6, _jar.CountByIp("127.0.0.1"));
    }

    [Fact]
    public void 踢最旧只踢这台来路_别的来路不受牵连()
    {
        _jar.Add("old-mine", "127.0.0.1", null, "admit", null, T0);
        _jar.Add("other-ip", "10.0.0.9", null, "admit", null, T0.AddMinutes(10));

        Assert.True(_jar.RemoveOldestByIp("127.0.0.1"));
        Assert.True(_jar.TryGet("other-ip", out _), "踢人只踢自己来路的，不能顺手清掉别人的排队");
        Assert.False(_jar.RemoveOldestByIp("192.168.0.1"), "没票的来路踢不出东西，也不该炸");
    }

    [Fact]
    public void 总闸只数活票总数_与按来路计数是两道闸()
    {
        for (var i = 0; i < 4; i++)
        {
            _jar.Add($"a{i}", "127.0.0.1", null, "admit", null, T0.AddSeconds(i));
            _jar.Add($"b{i}", $"10.0.0.{i}", null, "admit", null, T0.AddSeconds(i));
        }

        Assert.Equal(8, _jar.Total);
        Assert.Equal(4, _jar.CountByIp("127.0.0.1"));

        // 429 只在 Total 撞上总闸时才该响，per-IP 那条走的是踢最旧，不参与这句文案。
        Assert.True(_jar.Total < 64);
    }

    [Fact]
    public void 扫过期只清超寿命的_健康票一张不许误伤()
    {
        _jar.Add("fresh", "127.0.0.1", null, "admit", null, T0);
        _jar.Add("dying", "127.0.0.1", null, "admit", null, T0 - Ttl + TimeSpan.FromSeconds(1));
        _jar.Add("stale", "127.0.0.1", null, "admit", null, T0 - Ttl - TimeSpan.FromSeconds(1));

        Assert.Equal(1, _jar.SweepStale(T0, Ttl));
        Assert.True(_jar.TryGet("fresh", out _));
        Assert.True(_jar.TryGet("dying", out _), "差一秒才到期，还不算过期");
        Assert.False(_jar.TryGet("stale", out _));
    }

    [Fact]
    public void 票上的来路与席位都记着_退票才核得准是谁的()
    {
        _jar.Add("k", "127.0.0.1", "  INV-1  ", "bind", 7, T0);
        Assert.True(_jar.TryGet("k", out var ticket));
        Assert.Equal("127.0.0.1", ticket!.SourceIp);
        Assert.Equal("bind", ticket.Mode);
        Assert.Equal(7, ticket.OwnerMemberId);
        Assert.Equal(T0, ticket.CreatedUtc);
        // 邀请码的清洗在端点那头做，票池不许偷改人家递进来的字。
        Assert.Equal("  INV-1  ", ticket.Invite);
    }

    [Fact]
    public void 退票与查无此票同形_拿它探不出谁的码还在()
    {
        _jar.Add("k", "127.0.0.1", null, "admit", null, T0);

        // 端点对四种结局回的是同一个 { ok: true }，这里钉的是票池配合得出这个同形：
        // 销得动、销第二回、销一枚不存在的，都不给调用方任何可分辨的返回值差异。
        Assert.True(_jar.Remove("k"));
        Assert.False(_jar.Remove("k"));
        Assert.False(_jar.Remove("never-existed"));
        Assert.Equal(0, _jar.Total);
    }

    [Fact]
    public void 同枚key重复取按覆盖算_一张码不该有两份状态()
    {
        _jar.Add("same", "127.0.0.1", null, "admit", null, T0);
        _jar.Add("same", "10.0.0.9", null, "bind", 9, T0.AddMinutes(1));

        Assert.Equal(1, _jar.Total);
        Assert.True(_jar.TryGet("same", out var latest));
        // B 站把同一枚 key 再发一次时，票要跟最新那份走，否则会出现两个主人。
        Assert.Equal("10.0.0.9", latest!.SourceIp);
    }

    [Fact]
    public void 三张作废脸各说各的事()
    {
        // 这三句必须互不相同：前端靠它们把「分站重启」与「B 站废码」拆成两张脸。
        Assert.NotEqual(QrMessages.TicketGone, QrMessages.TtlExpired);
        Assert.NotEqual(QrMessages.TtlExpired, QrMessages.UpstreamExpired);
        Assert.NotEqual(QrMessages.TicketGone, QrMessages.UpstreamExpired);
        Assert.DoesNotContain("排队", QrMessages.TooManyWaiting);

        // 分站重启那句要说出「重启」二字，用户才知道该等一等而不是骂 B 站。
        Assert.Contains("重启", QrMessages.TicketGone);
        Assert.Contains("B 站", QrMessages.UpstreamExpired);
    }

    [Fact]
    public void 轮询状态挂在票上_两次代问之间记得住()
    {
        _jar.Add("k", "127.0.0.1", null, "admit", null, T0);
        Assert.True(_jar.TryGet("k", out var ticket));

        Assert.Equal(LBS.Engine.QrPhase.Waiting, ticket!.Phase);
        ticket.Phase = LBS.Engine.QrPhase.Scanned;
        ticket.LastPollUtc = T0.AddSeconds(1);

        // 票池给的是同一个实例引用：端点改完状态，下一轮代问才读得到（值类型副本会把限流与「已扫码」全丢在原地）。
        Assert.True(_jar.TryGet("k", out var again));
        Assert.Equal(LBS.Engine.QrPhase.Scanned, again!.Phase);
        Assert.Equal(T0.AddSeconds(1), again.LastPollUtc);
    }
}
