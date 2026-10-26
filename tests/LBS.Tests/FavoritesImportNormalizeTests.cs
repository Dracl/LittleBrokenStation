using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Favorites.Models;
using LBS.Engine;

namespace LBS.Tests;

/// <summary>
/// 收藏夹输入的归一化：站长从地址栏复制的 favlist 链接或纯数字 fid，
/// 都要能翻译成上游 ParseEntrance 认得的 ml 形态；不认识的写法原样放行由上游拒收。
/// </summary>
public class FavoritesImportNormalizeTests
{
    [Theory]
    [InlineData("1306978874", "ml1306978874")]
    [InlineData("ml1306978874", "ml1306978874")]
    [InlineData("ML1306978874", "ML1306978874")]
    [InlineData("https://space.bilibili.com/100969474/favlist?fid=1306978874", "ml1306978874")]
    [InlineData("https://space.bilibili.com/100969474/favlist?fid=ML1306978874&ctype=11", "ml1306978874")]
    [InlineData("https://space.bilibili.com/100969474/favlist?share_fid=9&FID=1306978874", "ml1306978874")]
    public void Normalize_ProducesMlForm(string input, string expected)
    {
        Assert.Equal(expected, LbsFavoritesImport.NormalizeToFavoritesId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("https://space.bilibili.com/1/favlist?fid=all")]
    public void Normalize_PassesThroughWhatItCannotTranslate(string input)
    {
        Assert.Equal(input, LbsFavoritesImport.NormalizeToFavoritesId(input));
    }

    [Fact]
    public void NormalizedFeedsUpstreamParser()
    {
        // 归一化不是自嗨：产出必须真能被上游 ParseEntrance 解析出 mediaId
        var normalized = LbsFavoritesImport.NormalizeToFavoritesId(
            "https://space.bilibili.com/100969474/favlist?fid=1306978874");
        Assert.True(ParseEntrance.IsFavoritesId(normalized));
        Assert.Equal(1306978874L, ParseEntrance.GetFavoritesId(normalized));
    }
}

/// <summary>
/// 收藏夹条目拣选：v3 接口的 link 可能是 bilibili:// 深链，认 bv_id 正身；
/// 下架条目（attr 非 0）和无 BV 号的条目进 Ignored。
/// </summary>
public class FavoritesImportPickTests
{
    [Fact]
    public void DeepLinkEntries_ArePickedByBvid()
    {
        // 实况标本：v3 资源接口对普通视频也回 bilibili://video/<aid> 这种 app 深链
        var media = new FavoritesMedia
        {
            Title = "miku 2020生日动图 1080P版",
            Link = "bilibili://video/456782499",
            Bvid = "",
            LegacyBvid = "BV1o541187Wh",
            Cover = "https://i0.hdslb.com/bfs/archive/x.jpg",
            Pubtime = 1587000000,
        };

        var videos = LbsFavoritesImport.PickVideos([media], out var ignored);

        Assert.Equal(0, ignored);
        var video = Assert.Single(videos);
        Assert.Equal("BV1o541187Wh", video.Bvid);
        Assert.Equal("https://www.bilibili.com/video/BV1o541187Wh", video.Url);
        Assert.Equal("https://i0.hdslb.com/bfs/archive/x.jpg", video.CoverUrl);
        Assert.StartsWith("2020-04-16", video.PublishedAtUtc);
    }

    [Fact]
    public void DelistedEntries_AreIgnored()
    {
        var dead = new FavoritesMedia { Title = "已失效视频", Link = "bilibili://video/536993869", LegacyBvid = "BV17i4y1v7YE", Attr = 1 };
        var alive = new FavoritesMedia { Title = "好好活着", Link = "bilibili://video/1", LegacyBvid = "BV1GM41157Bz" };

        var videos = LbsFavoritesImport.PickVideos([dead, alive], out var ignored);

        Assert.Equal(1, ignored);
        Assert.Equal("BV1GM41157Bz", Assert.Single(videos).Bvid);
    }

    [Fact]
    public void EntriesWithoutAnyBvid_AreIgnored()
    {
        var ogv = new FavoritesMedia { Title = "番剧", Link = "https://www.bilibili.com/bangumi/play/ss32982" };

        var videos = LbsFavoritesImport.PickVideos([ogv], out var ignored);

        Assert.Empty(videos);
        Assert.Equal(1, ignored);
    }

    [Fact]
    public void WebLinkIsKeptWhenSane()
    {
        var media = new FavoritesMedia { Title = "老链接", Link = "https://www.bilibili.com/video/BV1uh411N7As" };

        var videos = LbsFavoritesImport.PickVideos([media], out var ignored);

        Assert.Equal(0, ignored);
        Assert.Equal("https://www.bilibili.com/video/BV1uh411N7As", Assert.Single(videos).Url);
    }
}
