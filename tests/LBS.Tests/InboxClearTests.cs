using LBS.Domain;
using LBS.Engine;
using LBS.Infrastructure;
using LBS.Server;
using Microsoft.Data.Sqlite;

namespace LBS.Tests;

/// <summary>
/// 清空信箱（站长 11-03：「加个清空按钮，隐藏还是真删？」——小花选真删，站长准）：
/// 钉两件事——①清空只销自己这本账，他人信分毫不动、未读归零；
/// ②清的是存量不是订阅：清完新信照进（那把订阅闸在 notify_mutes，不在此处）。
/// 夹具照 ArrivalReportTests 抄。
/// </summary>
public sealed class InboxClearTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;
    private readonly MemberGate _gate;

    public InboxClearTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir);
        _db = new StationDb(paths);
        _db.Initialize();
        _gate = new MemberGate(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private static BilibiliIdentity Identity(long mid, string name = "小张") => new(mid, name, "https://face/y.jpg");

    private long AdmitSeat(long mid, string name = "小张")
    {
        if (!_db.HasOwner())
        {
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), invite: null, "SESSDATA=a");
        }
        else
        {
            var (_, code) = _db.CreateMemberInvite(null);
            MemberEndpoints.AdmitMember(_db, _gate, Identity(mid, name), code, "SESSDATA=a");
        }

        return _db.GetMemberByMid(mid)!.Id;
    }

    [Fact]
    public void 清空只销自己这本账_他人信分毫不动_未读归零()
    {
        var me = AdmitSeat(1001, "站长");
        var peer = AdmitSeat(1002, "阿席");
        // 两席各有私发与广播两路信（欢迎信也在账上，一并该销自己的那份）
        _db.SendToMember(me, MemberMail.KindWelcome, "欢迎来到小破分站", "正文", "/me");
        _db.SendToMember(me, MemberMail.KindArrival, "分站到货 · 1 件新货", "正文");
        _db.SendToMember(peer, MemberMail.KindComplete, "投稿已入档", "正文", "/video/BVclr000001");
        _db.BroadcastToActive(MemberMail.KindAnnounce, "全站大事件", "正文");
        var mineBefore = _db.ListMessages(me).Count;
        Assert.True(mineBefore >= 3);

        var cleared = _db.ClearInbox(me);

        Assert.Equal(mineBefore, cleared);
        Assert.Empty(_db.ListMessages(me));
        Assert.Equal(0, _db.CountUnread(me));
        // 对席账本分毫不动：清空从不越席
        Assert.True(_db.ListMessages(peer).Count >= 2);
    }

    [Fact]
    public void 清的是存量不是订阅_清完新信照进()
    {
        var me = AdmitSeat(1001, "站长");
        _db.SendToMember(me, MemberMail.KindArrival, "分站到货 · 1 件新货", "正文");
        Assert.True(_db.ClearInbox(me) >= 1); // 自己的账清干净（欢迎信同销）
        Assert.Empty(_db.ListMessages(me));

        _db.SendToMember(me, MemberMail.KindArrival, "分站到货 · 1 件新货", "又一波");
        Assert.Single(_db.ListMessages(me), m => m.Kind == MemberMail.KindArrival);
    }
}
