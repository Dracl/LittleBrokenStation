using System.Text.Json;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 送信员（M5.2 站内信箱）：欢迎信、归档回执、失败回执统一从这里寄出。
/// 三处钩子都钉在写库热路径上（收编 / 状态同步 / 下载失败），所以一切异常只吞成
/// warning 日志——信寄不出去，绝不能把进站、归档或下载的主流程带下水。
/// </summary>
public static class MemberMail
{
    public const string KindWelcome = "welcome";
    public const string KindComplete = "complete";
    public const string KindFail = "fail";
    public const string KindAnnounce = "announce";
    /// <summary>到站播报（11-03）：订阅新货完整到站的全站通报，卡面载荷住 payload_json。</summary>
    public const string KindArrival = "arrival";

    /// <summary>入档回执的卡面型（11-03 站长：「这条通知也做个排版」）：messages.msg_type 认这一枚决定
    /// 渲染单件卡还是纯文本；载荷形状与到站播报的到货卡同源（一件也是数组），前端同一套行版式接两家。</summary>
    public const string MsgTypeVideoReceipt = "video_receipt";

    /// <summary>欢迎信：只在首次建档（邀请/注册兑现或首扫继位）那一刻寄一次。收件人是站内席位 id。</summary>
    public static void NotifyWelcome(StationDb db, ILogger? logger, long toMemberId)
    {
        TrySend(logger, "欢迎信", () =>
        {
            var stationName = db.GetSetting("station-name");
            stationName = string.IsNullOrWhiteSpace(stationName) ? "小破分站" : stationName.Trim();
            db.SendToMember(toMemberId, KindWelcome, "欢迎来到小破分站",
                $"「{stationName}」灯给你留着——进站就是一起守站的人了。去用户控制室看看自己的账号池和投稿账本吧。",
                "/me");
        }, toMemberId);
    }

    /// <summary>归档回执：视频到站且投稿有归属才寄；无归属行（站长代下等）静默跳过。</summary>
    public static void NotifyVideoArrived(StationDb db, ILogger? logger, long videoId)
    {
        TrySend(logger, "归档回执", () =>
        {
            var target = db.GetVideoMailTarget(videoId);
            if (target?.SubmitterMemberId is not long submitterMemberId)
            {
                return;
            }

            var display = string.IsNullOrWhiteSpace(target.Title) ? target.Bvid : target.Title;
            // 卡面快照在这封信寄出那一刻定格（到站播报/分享卡同族语义，读路径不现查）：
            // 封面、UP、时长从此不随日后删档改口；取不到料（理论不可达的拆窗竞态）退回纯文本 body，信照寄不误。
            var card = db.GetArrivalCardByVideoId(videoId);
            var payloadJson = card is null
                ? null
                : JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        bvid = card.Bvid,
                        title = card.Title,
                        cover = card.Cover,
                        coverUrl = card.CoverUrl,
                        coverThumbs = card.CoverThumbs,
                        up = card.UpName,
                        duration = card.DurationText,
                    },
                });
            db.SendToMember(submitterMemberId, KindComplete, "投稿已入档",
                $"「{display}」完整入档：正片、弹幕、字幕与封面都已上架，随时可放映。",
                $"/watch/{target.Bvid}", msgType: card is null ? null : MsgTypeVideoReceipt, payloadJson: payloadJson,
                muteChannel: NotifyMutes.Submit);
        }, videoId);
    }

    /// <summary>失败回执：把 last_error 摘要带给投稿人，别让人干等。</summary>
    public static void NotifyVideoFailed(StationDb db, ILogger? logger, long videoId, string? error)
    {
        TrySend(logger, "失败回执", () =>
        {
            var target = db.GetVideoMailTarget(videoId);
            if (target?.SubmitterMemberId is not long submitterMemberId)
            {
                return;
            }

            var display = string.IsNullOrWhiteSpace(target.Title) ? target.Bvid : target.Title;
            var reason = FirstLine(error ?? target.LastError);
            db.SendToMember(submitterMemberId, KindFail, "投稿打了个趔趄",
                $"「{display}」没能入档：{(string.IsNullOrWhiteSpace(reason) ? "引擎没说原因" : reason)}。稍后重试或换个链接再来。",
                null, muteChannel: NotifyMutes.Submit);
        }, videoId);
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private static void TrySend(ILogger? logger, string what, Action send, long subjectId)
    {
        try
        {
            send();
        }
        catch (Exception ex)
        {
            // 信箱是锦上添花：写失败只留痕，主流程照常走。
            logger?.LogWarning(ex, "站内信箱：{What}没寄出去（对象 {SubjectId}）", what, subjectId);
        }
    }
}
