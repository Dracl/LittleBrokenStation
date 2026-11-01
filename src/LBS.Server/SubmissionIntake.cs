using LBS.Domain;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 投稿受理的唯一顺序：先验人（401）→ 再验号（403）→ 最后才落台账（409 只留给真重复）。
///
/// 这条顺序是拿数据换回来的。原来四张投稿口（单链、收藏夹文件、收藏夹链接、UP 主整搬）都是
/// <c>AddVideosBulk</c> 先写台账，再查席位、再过 <see cref="SubmissionGate"/>，而拒收的两条路不回滚：
/// 闸门一拒，台账里就留下一行无标题、无归属、永不下載的幽灵行，而它已经把那个 BV 号占死
/// （bvid 上的去重靠台账本身），同一条链接此后永远投不进来 —— 11-02 的 BV1Fz4y1Q7kB（id=468）
/// 与卡住的 467 就是这么来的，10-26 那批 151 条无归属稿同出一门。
///
/// 所以「写台账」这件事也收进这个口：想为投稿写台账只能从 <see cref="Accept"/> 进，
/// 而 Accept 只允许在 <see cref="Deny"/> 放行之后调。顺序由调用形状说话，不靠注释里那句「记得先判」。
/// 后台爬工（SubscriptionCrawler）不在此列 —— 它本来就没有投稿人，落的就该是无归属行。
/// </summary>
public static class SubmissionIntake
{
    /// <summary>受理成功：台账已落、归属已定，端点只剩入队与回执。</summary>
    public sealed record Accepted(IReadOnlyList<long> InsertedIds, int Added, int Duplicated);

    /// <summary>
    /// 投稿前的两道脸：没登录 401，素净席位（无未过期 B 站号）403；站长不受此闸（见 <see cref="SubmissionGate"/>）。
    /// 放行返回 null。两句词的唯一出处在这里，前端只做兜底、不另写一套。
    /// </summary>
    public static IResult? Deny(StationDb db, StationMember? member)
    {
        if (member is null)
        {
            return Results.Json(new { error = "member-required", message = "投稿要留名：登录进站再丢链接" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!SubmissionGate.Allows(db, member))
        {
            return Results.Json(new
            {
                error = "no-bilibili-account",
                message = "还没绑 B 站号，先去何处？用户控制室——绑定后就能投稿，看片私信不耽误",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>
    /// 落台账并当场定归属（投稿人 = 这个席位，不再留「先插后补归属」的空档）。
    /// 只在 <see cref="Deny"/> 放行之后调；Added == 0 意味着这些 BV 号本来就在台账里，
    /// 端点自己决定那张脸：单链报 409「已经到过了」，批量报「来过 N 条」。
    /// </summary>
    public static Accepted Accept(StationDb db, StationMember member, IReadOnlyList<NewVideo> videos)
    {
        var outcome = db.AddVideosBulk(videos);
        foreach (var videoId in outcome.InsertedIds)
        {
            db.SetVideoSubmitterMember(videoId, member.Id);
        }

        return new Accepted(outcome.InsertedIds, outcome.Added, outcome.Duplicated);
    }
}
