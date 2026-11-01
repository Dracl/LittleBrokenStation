using System.Text.Json;
using LBS.Domain;
using LBS.Infrastructure;
using LBS.Server;

namespace LBS.Tests;

/// <summary>
/// 加档前那份「预估」的账：playurl 响应怎么解析、什么时候该出网、行情怎么存。
///
/// 这一手的全部风险在「拿没到的数冒充到了」，所以钉子里一半在验这件事：B 站没给的编码不许编出一个数、
/// 站长要的档位没给就往下取（宁报低的）、上游答了但没给货与根本没问成要分得开（前者盖戳别撞、后者该重试）。
/// 数字取的是真响应的形状（dash.video[] 带 codecid 与 bandwidth，DASH 不给 size）。
/// </summary>
public sealed class CodecEstimateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StationDb _db;

    public CodecEstimateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-estimate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new StationDb(new DataPaths(_tempDir, _tempDir, _tempDir, _tempDir, _tempDir));
        _db.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不碍事
        }
    }

    private const long Ms = 200_000; // 200 秒，凑整好算：(5,000,000+200,000)×200,000÷8000 = 130,000,000

    private static string PlayUrl(JsonElement? dash, int code = 0, long timelength = Ms)
    {
        var data = dash is null
            ? $"{{\"timelength\":{timelength}}}"
            : $"{{\"timelength\":{timelength},\"dash\":{dash.Value.GetRawText()}}}";
        return $"{{\"code\":{code},\"message\":\"OK\",\"ttl\":1,\"data\":{data}}}";
    }

    private static JsonElement Dash(string videoJson, string audioJson = """
        [{"id":30280,"bandwidth":200000},{"id":30216,"bandwidth":66000}]
        """) => JsonDocument.Parse($"{{\"video\":{videoJson},\"audio\":{audioJson}}}").RootElement.Clone();

    private static CodecEstimate? Find(CodecEstimateParser.Outcome outcome, string codec) =>
        outcome.Items.FirstOrDefault(i => i.Codec == codec);

    [Fact]
    public void 三档齐活_码率按千位取整_体积按带宽与时长算()
    {
        var raw = PlayUrl(Dash("""
            [
              {"id":112,"codecid":7,"bandwidth":5000000,"width":1920,"height":1080,"frameRate":"30.000"},
              {"id":112,"codecid":12,"bandwidth":3277228,"width":1920,"height":1080},
              {"id":112,"codecid":13,"bandwidth":3017647,"width":1920,"height":1080}
            ]
            """));

        var outcome = CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30280);

        Assert.True(outcome.Answered);
        Assert.Equal(3, outcome.Items.Count);

        var avc = Find(outcome, "avc")!;
        Assert.Equal(5000, avc.VideoKbps);
        Assert.Equal(200, avc.AudioKbps);           // 30280 那条，不是 30216
        Assert.Equal(130_000_000L, avc.Bytes);      // (视频+音频)×时长÷8，DASH 不给现成 size
        Assert.Equal(112, avc.Qn);
        Assert.Equal(1920, avc.Width);
        Assert.Equal(1080, avc.Height);

        Assert.Equal(3277, Find(outcome, "hevc")!.VideoKbps);
        Assert.Equal(3018, Find(outcome, "av1")!.VideoKbps);
        // 三档共用同一条音轨：音频数一样不是抄错
        Assert.Equal(Find(outcome, "avc")!.AudioKbps, Find(outcome, "av1")!.AudioKbps);
    }

    [Fact]
    public void B站没给HEVC_就不编出那一档()
    {
        var raw = PlayUrl(Dash("""
            [
              {"id":112,"codecid":7,"bandwidth":5000000},
              {"id":112,"codecid":13,"bandwidth":3017647}
            ]
            """));

        var outcome = CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30280);

        Assert.True(outcome.Answered);
        Assert.Null(Find(outcome, "hevc"));
        Assert.Equal(2, outcome.Items.Count);
    }

    [Fact]
    public void 站长要112而B站只给到64_往低取并记下实际那一档()
    {
        var raw = PlayUrl(Dash("""
            [
              {"id":64,"codecid":7,"bandwidth":1466122},
              {"id":32,"codecid":7,"bandwidth":643210}
            ]
            """));

        var avc = Find(CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30280), "avc")!;

        Assert.Equal(64, avc.Qn);        // 宁报低的那一档，不拿没到的高档吓人
        Assert.Equal(1466, avc.VideoKbps);
    }

    [Fact]
    public void 只有比要求更高的档_取最接近的那一档并如实记qn()
    {
        var raw = PlayUrl(Dash("""
            [
              {"id":112,"codecid":7,"bandwidth":5000000},
              {"id":80,"codecid":7,"bandwidth":3633308}
            ]
            """));

        var avc = Find(CodecEstimateParser.Parse(raw, wantQn: 16, wantAudioId: 30280), "avc")!;

        Assert.Equal(80, avc.Qn);        // 真点单会拿到的就是这档
        Assert.Equal(3633, avc.VideoKbps);
    }

    [Fact]
    public void 上游答了但没给货_算问过了却不留行情()
    {
        // 非大会员 / 付费稿 / 风控打回都长这样：code 非零
        var answered = CodecEstimateParser.Parse("""{"code":-101,"message":"账号未登录","data":null}""", 112, 30280);
        Assert.True(answered.Answered);
        Assert.Empty(answered.Items);

        // code 正常但连 dash 都没有（durl 形态或空货架）
        var noDash = CodecEstimateParser.Parse(PlayUrl(null), 112, 30280);
        Assert.True(noDash.Answered);
        Assert.Empty(noDash.Items);
    }

    [Fact]
    public void 根本没问成_不算问过_好让下一轮重试()
    {
        Assert.False(CodecEstimateParser.Parse(null, 112, 30280).Answered);
        Assert.False(CodecEstimateParser.Parse("", 112, 30280).Answered);
        Assert.False(CodecEstimateParser.Parse("<html>风控拦截页</html>", 112, 30280).Answered);
        Assert.False(CodecEstimateParser.Parse("""{"message":"没有 code 字段"}""", 112, 30280).Answered);
    }

    [Fact]
    public void 站长设的音质没给_退吃带宽最高的那条音轨()
    {
        var raw = PlayUrl(Dash("""
            [{"id":112,"codecid":7,"bandwidth":5000000}]
            """, """[{"id":30232,"bandwidth":106585},{"id":30216,"bandwidth":66203}]"""));

        var avc = Find(CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30251), "avc")!;

        Assert.Equal(107, avc.AudioKbps); // 30251 不在，退而取最高的 30232
    }

    [Fact]
    public void 时长缺失_体积就不编()
    {
        var raw = PlayUrl(Dash("""[{"id":112,"codecid":7,"bandwidth":5000000}]"""), timelength: 0);

        var avc = Find(CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30280), "avc")!;

        Assert.Equal(5000, avc.VideoKbps);
        Assert.Null(avc.Bytes);           // 没时长算不出体积，留空比编一个强
    }

    [Fact]
    public void 条目没带宽_当没这条流()
    {
        var raw = PlayUrl(Dash("""[{"id":112,"codecid":7,"width":1920,"height":1080}]"""));

        Assert.Null(Find(CodecEstimateParser.Parse(raw, wantQn: 112, wantAudioId: 30280), "avc"));
    }

    // ── 该不该出网与落库 ────────────────────────────────────────────────────────

    [Fact]
    public void 有行情就不问_空着才问()
    {
        // 站长 11-02 定的口径：只看库里有没有，不问时间新旧，不设保鲜期与冷却窗。
        Assert.True(CodecEstimateService.ShouldSkipFetch(3));
        Assert.True(CodecEstimateService.ShouldSkipFetch(1));
        Assert.False(CodecEstimateService.ShouldSkipFetch(0));
    }

    [Fact]
    public void 保存是整页覆盖_上次有这次没给的不留旧行()
    {
        var full = new[]
        {
            new CodecEstimate("avc", 5000, 200, 130_000_000, 112, 1920, 1080),
            new CodecEstimate("hevc", 3277, 200, 90_000_000, 112, 1920, 1080),
            new CodecEstimate("av1", 3018, 200, 84_000_000, 112, 1920, 1080),
        };
        _db.SaveCodecEstimates(9L, 1, full, "2026-11-02T03:00:00.0000000+00:00");
        Assert.Equal(3, _db.ListCodecEstimates(9L, 1).Count);

        // 第二天会员掉了，B 站只给 AVC 了：另两档必须跟着消失
        _db.SaveCodecEstimates(9L, 1, new[] { full[0] }, "2026-11-03T03:00:00.0000000+00:00");

        var left = Assert.Single(_db.ListCodecEstimates(9L, 1));
        Assert.Equal("avc", left.Codec);
        Assert.Equal(5000, left.VideoKbps);
        Assert.Equal(130_000_000L, left.Bytes);
        Assert.Equal(112, left.Qn);
    }

    [Fact]
    public void 空清单写进去_库里就留空下一轮才会重问()
    {
        // 上游答了却一条流没给（非会员拿不到 HEVC/AV1、付费稿）：存空等于什么都没存，
        // 于是下一轮 ShouldSkipFetch(0) 仍为假 —— 这是「空着才拉」的另一半。
        _db.SaveCodecEstimates(9L, 2, new[] { new CodecEstimate("avc", 5000, 200, 130_000_000, 112) },
            "2026-11-02T03:00:00.0000000+00:00");
        Assert.Single(_db.ListCodecEstimates(9L, 2));

        _db.SaveCodecEstimates(9L, 2, Array.Empty<CodecEstimate>(), "2026-11-02T05:00:00.0000000+00:00");

        Assert.Empty(_db.ListCodecEstimates(9L, 2));
    }

    [Fact]
    public void 各分P各存各的_互不串页()
    {
        _db.SaveCodecEstimates(9L, 1, new[] { new CodecEstimate("avc", 5000, 200, 130_000_000, 112) },
            "2026-11-02T03:00:00.0000000+00:00");
        _db.SaveCodecEstimates(9L, 2, new[] { new CodecEstimate("avc", 800, 66, 20_000_000, 32) },
            "2026-11-02T03:00:00.0000000+00:00");

        Assert.Equal(5000, Assert.Single(_db.ListCodecEstimates(9L, 1)).VideoKbps);
        Assert.Equal(800, Assert.Single(_db.ListCodecEstimates(9L, 2)).VideoKbps);
        Assert.Empty(_db.ListCodecEstimates(9L, 3)); // 没问过的第三页不该被前两页的行情罩住
    }
}
