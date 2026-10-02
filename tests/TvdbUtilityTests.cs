using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Mapping;
using Shoko.Plugin.Tvdb.Metadata;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// The identifiers, codes and image paths the plugin hands the core.
/// </summary>
public class TvdbUtilityTests
{
    [Fact]
    public void TheSource_IsTvdbAndReadsBackFromTheOldSpellings()
    {
        Assert.Equal("tvdb", MetadataSource.Tvdb.Value);
        Assert.Equal("TvDB", MetadataSource.Tvdb.Name);
        Assert.Equal("The television and film database at thetvdb.com.", MetadataSource.Tvdb.Description);
        Assert.True(MetadataSource.TryGet("TvDB", out var old));
        Assert.Same(MetadataSource.Tvdb, old);
        Assert.True(MetadataSource.TryGet("thetvdb", out var alias));
        Assert.Same(MetadataSource.Tvdb, alias);
    }

    [Fact]
    public void Identifiers_AreWrittenTheWayTheCoreReadsThem()
    {
        Assert.Equal("tvdb://series/81797", TvdbUtility.SeriesGuid(81797).ToString());
        Assert.Equal("tvdb://season/31893", TvdbUtility.SeasonGuid(31893).ToString());
        Assert.Equal("tvdb://episode/361887", TvdbUtility.EpisodeGuid(361887).ToString());
        Assert.Equal("tvdb://creator/412417", TvdbUtility.CreatorGuid(412417).ToString());
        Assert.Equal("tvdb://character/65111900", TvdbUtility.CharacterGuid(65111900).ToString());
        Assert.Equal("tvdb://tag/301", TvdbUtility.TagGuid(301).ToString());
        Assert.Equal("tvdb://tag/genre/1", TvdbUtility.GenreGuid(1).ToString());
        Assert.Equal("tvdb://studio/51", TvdbUtility.StudioGuid(51).ToString());
        Assert.Equal("tvdb://network/50", TvdbUtility.NetworkGuid(50).ToString());
        Assert.Equal("tvdb://ordering/81797-dvd", TvdbUtility.OrderingGuid(81797, "dvd").ToString());
        Assert.Equal("tvdb://season/81797-dvd-2", TvdbUtility.SeasonTypeSeasonGuid(81797, "dvd", 2).ToString());
    }

    [Fact]
    public void AnID_IsReadBackOnlyFromTheRightSourceAndKind()
    {
        Assert.True(TvdbUtility.TryGetID(TvdbUtility.SeriesGuid(81797), MetadataEntityType.Series, out var id));
        Assert.Equal(81797, id);
        Assert.False(TvdbUtility.TryGetID(TvdbUtility.SeriesGuid(81797), MetadataEntityType.Episode, out _));
        Assert.False(TvdbUtility.TryGetID(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "1"), MetadataEntityType.Series, out _));
        Assert.False(TvdbUtility.TryGetID(TvdbUtility.SeasonTypeSeasonGuid(81797, "dvd", 1), MetadataEntityType.Season, out _));
        Assert.False(TvdbUtility.TryGetID(null, MetadataEntityType.Series, out _));
    }

    [Theory]
    [InlineData("jpn", "ja")]
    [InlineData("eng", "en")]
    [InlineData("deu", "de")]
    [InlineData(" JPN ", "ja")]
    [InlineData("xxx", "xxx")]
    [InlineData("por", "pt")]
    [InlineData("pt", "pt-BR")]
    [InlineData("zho", "zh")]
    [InlineData("zhtw", "zh-hant")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ALanguage_IsKeptUnderItsTwoLetterCodeWhereItHasOne(string? code, string? expected)
        => Assert.Equal(expected, TvdbUtility.ToLanguageCode(code));

    [Theory]
    [InlineData("por", TitleLanguage.Portuguese)]
    [InlineData("pt", TitleLanguage.BrazilianPortuguese)]
    [InlineData("zho", TitleLanguage.Chinese)]
    [InlineData("zhtw", TitleLanguage.ChineseTraditional)]
    [InlineData("xxx", TitleLanguage.Unknown)]
    public void TvDBsOwnLanguageCodes_AreReadAsTheLanguagesTheyStandFor(string code, TitleLanguage expected)
        => Assert.Equal(expected, TvdbUtility.ToTitleLanguage(code));

    [Theory]
    [InlineData("jpn", "ja")]
    [InlineData("pt", "pt")]
    [InlineData("zhtw", "zh")]
    [InlineData("xxx", null)]
    [InlineData(null, null)]
    public void AnImageLanguage_IsTwoLettersOrNothing(string? code, string? expected)
        => Assert.Equal(expected, TvdbUtility.ToImageLanguageCode(code));

    [Theory]
    [InlineData("usa", "US")]
    [InlineData("jpn", "JP")]
    [InlineData("gbr", "GB")]
    [InlineData("de", "DE")]
    [InlineData("nowhere", null)]
    [InlineData("zzz", null)]
    [InlineData(null, null)]
    public void ACountry_IsKeptUnderItsTwoLetterCode(string? code, string? expected)
        => Assert.Equal(expected, TvdbUtility.ToCountryCode(code));

    [Theory]
    [InlineData("https://artworks.thetvdb.com/banners/posters/81797-1.jpg", "posters/81797-1.jpg")]
    [InlineData("http://artworks.thetvdb.com/banners/v4/series/81797/posters/abc.jpg", "v4/series/81797/posters/abc.jpg")]
    [InlineData("/banners/fanart/original/81797-1.jpg", "fanart/original/81797-1.jpg")]
    [InlineData("episodes/81797/361887.jpg", "episodes/81797/361887.jpg")]
    [InlineData("https://images.example.com/posters/elsewhere.jpg", null)]
    [InlineData("https://artworks.thetvdb.com/somewhere/else.jpg", null)]
    [InlineData("https://artworks.thetvdb.com/banners/images/missing/series.jpg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AnImage_IsTheRestOfItsPathUnderTheBanners(string? value, string? expected)
        => Assert.Equal(expected, TvdbImages.ToResourceID(value));

    [Fact]
    public void AnImagePathLongerThanTheCoreHolds_IsDroppedRatherThanCut()
        => Assert.Null(TvdbImages.ToResourceID("/banners/" + new string('a', TvdbImages.MaxResourceIDLength + 1)));

    [Fact]
    public void TheTemplate_RebuildsTheImageURL()
        => Assert.Equal("https://artworks.thetvdb.com/banners/posters/81797-1.jpg", string.Format(System.Globalization.CultureInfo.InvariantCulture, TvdbImages.TemplateUrl, "posters/81797-1.jpg"));

    [Theory]
    [InlineData(1, ImageEntityType.Banner)]
    [InlineData(2, ImageEntityType.Primary)]
    [InlineData(3, ImageEntityType.Backdrop)]
    [InlineData(6, ImageEntityType.Banner)]
    [InlineData(7, ImageEntityType.Primary)]
    [InlineData(8, ImageEntityType.Backdrop)]
    [InlineData(23, ImageEntityType.Logo)]
    [InlineData(22, ImageEntityType.None)]
    public void AKindOfArtwork_MapsOntoAnImageType(int artworkType, ImageEntityType expected)
        => Assert.Equal(expected, TvdbImages.ToImageType(artworkType));
}
