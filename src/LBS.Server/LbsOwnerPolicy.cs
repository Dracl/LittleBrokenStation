using LBS.Engine;
using LBS.Infrastructure;

namespace LBS.Server;

/// <summary>
/// 采集出号策略：mixed 跟随视频归属号（下载是谁、采集就是谁）；main/alt 由站长室采集设置强制
/// （alt 没配自动回落主号）。老馆藏视频没定档归属时，第一次被采集就轮转定档，之后始终一致。
/// </summary>
public sealed class LbsOwnerPolicy(StationDb db, LbsAccountRouter router)
{
    public LbsOwner OwnerForVideo(long videoId) => router.CollectMode switch
    {
        LbsCollectMode.Main => LbsOwner.Main,
        LbsCollectMode.Alt => router.Effective(LbsOwner.Alt),
        _ => ResolveMixed(videoId),
    };

    /// <summary>把一段与某视频绑定的采集活钉在该归属号的上下文里跑。</summary>
    public Task<T> RunForVideoAsync<T>(long videoId, Func<Task<T>> action) =>
        router.RunAsAsync(OwnerForVideo(videoId), action);

    public Task RunForVideoAsync(long videoId, Func<Task> action) =>
        router.RunAsAsync(OwnerForVideo(videoId), action);

    /// <summary>整段方法级的上下文（一趟里多个采集调用共用一个号时用）。</summary>
    public IDisposable BeginForVideo(long videoId) => router.BeginScope(OwnerForVideo(videoId));

    /// <summary>不挂具体视频的采集动作（收藏夹整架导入）按采集偏好出号。</summary>
    public Task<T> RunForActivityAsync<T>(Func<Task<T>> action) =>
        router.RunAsAsync(router.CollectOwner(), action);

    private LbsOwner ResolveMixed(long videoId)
    {
        if (db.GetVideoOwner(videoId) is { } stored)
        {
            return (LbsOwner)stored;
        }

        var assigned = router.AssignNext();
        db.SetVideoOwner(videoId, (int)assigned);
        return assigned;
    }
}
