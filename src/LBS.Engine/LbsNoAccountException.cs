namespace LBS.Engine;

/// <summary>
/// 这条 B 站请求没有可用账号。分站不再有站级兜底登录态：投稿链出投稿人自己的号，
/// 采集与加档出采集号（站长席位的活号优先），两边都不拿匿名请求顶上去——
/// 顶上去只会被 B 站按 -101/-352 打回，还可能把设备的 SESSDATA 拖进风控。
/// 抛出即代表「本轮该跳过并说明原因」，不是「换个号再试」。
/// </summary>
public sealed class LbsNoAccountException(string message) : Exception(message);
