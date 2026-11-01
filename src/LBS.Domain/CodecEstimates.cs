namespace LBS.Domain;

/// <summary>
/// 某个编码版本的「预估」规格：出自 B 站 playurl 的标称值，<b>不是本地实测</b>。
///
/// 与 <see cref="CodecVersion"/> 上那两路码率（ffprobe 实测落盘文件）是两码事，前端必须分开报：
/// 到货的版本吃实测，没到货的才吃这份预估，好让人在点单加档之前就知道大概要占多少盘。
///
/// 三条来路都得记着：
/// ▸ videoKbps 是 B 站给的标称平均码率，与实测会差几个百分点；
/// ▸ bytes 是算出来的（DASH 不给现成大小，只有 bandwidth），(视频 + 音频) × 时长 ÷ 8；
/// ▸ 三档编码共用同一条音轨，所以三行的 audioKbps 通常是同一个数，那不是抄错。
/// </summary>
public sealed record CodecEstimate(
    string Codec,
    int? VideoKbps,
    int? AudioKbps,
    long? Bytes,
    int? Qn = null,
    int? Width = null,
    int? Height = null);
