using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Tvdb.Mapping;
using Xunit;

namespace Shoko.Plugin.Tvdb.Tests;

/// <summary>
/// Which of TvDB's aliases are read as romanised readings of a title, from
/// real aliases TvDB files on Japanese, Chinese and Korean shows.
/// </summary>
public class RomanizationDetectorTests
{
    [Theory]
    [InlineData("Sousou no Frieren")]
    [InlineData("Sōsō no Furīren")]
    [InlineData("Wan Pisu")]
    [InlineData("Shingeki no Kyojin")]
    [InlineData("Jyouheki")]
    [InlineData("Kimetsu no Yaiba")]
    [InlineData("Boku no Hero Academia")]
    [InlineData("Shimbun")]
    [InlineData("Kocchi Muite")]
    [InlineData("Hatsukoi Limited")]
    // Romaji around a name or a foreign word still counts.
    [InlineData("Sōsō no Frieren")]
    [InlineData("Hibike! Euphonium")]
    [InlineData("Musaigen no Phantom World")]
    public void AJapaneseShowsRomaji_IsRomaji(string alias)
        => Assert.Equal(TitleLanguage.Romaji, RomanizationDetector.ReadingOf(alias, TitleLanguage.Japanese, TitleLanguage.Japanese));

    [Theory]
    [InlineData("Charlotte (2015)")]
    [InlineData("Magical★Explorer")]
    [InlineData("NullMeta")]
    [InlineData("Kowloon Generic Romance")]
    [InlineData("Tropical-Rouge! PreCure")]
    public void AJapaneseShowsNamesAndEnglish_AreNoReading(string alias)
        => Assert.Null(RomanizationDetector.ReadingOf(alias, TitleLanguage.Japanese, TitleLanguage.Japanese));

    [Theory]
    [InlineData("Xiān Nì")]
    [InlineData("Xian Ni")]
    [InlineData("Dou Po Cangqiong")]
    [InlineData("Mo Dao Zu Shi")]
    [InlineData("Tian Guan Ci Fu")]
    [InlineData("Quanzhi Gaoshou")]
    public void AChineseShowsPinyin_IsPinyin(string alias)
        => Assert.Equal(TitleLanguage.Pinyin, RomanizationDetector.ReadingOf(alias, TitleLanguage.ChineseSimplified, TitleLanguage.ChineseSimplified));

    [Theory]
    [InlineData("Twin Moons")]
    [InlineData("Cinderella Chef")]
    [InlineData("SYWZ")]
    public void AChineseShowsEnglishAndInitials_AreNoReading(string alias)
        => Assert.Null(RomanizationDetector.ReadingOf(alias, TitleLanguage.ChineseSimplified, TitleLanguage.ChineseSimplified));

    [Fact]
    public void AKoreanShowsLatinAlias_IsAKoreanReadingWithoutAScore()
    {
        Assert.Equal(TitleLanguage.KoreanTranscription, RomanizationDetector.ReadingOf("Na Honjaman Rebereop", TitleLanguage.Korean, TitleLanguage.Korean));
        Assert.Null(RomanizationDetector.Share("Na Honjaman Rebereop", TitleLanguage.KoreanTranscription));
    }

    [Theory]
    [InlineData("Ah! My Goddess", TitleLanguage.Japanese)]
    [InlineData("Liz to Aoi Tori", TitleLanguage.Japanese)]
    [InlineData("Sousou no Frieren", TitleLanguage.English)]
    public void AnAliasOutsideTheShowsOwnLanguage_IsNoReading(string alias, TitleLanguage originalLanguage)
        => Assert.Null(RomanizationDetector.ReadingOf(alias, TitleLanguage.Korean, originalLanguage));

    [Fact]
    public void AnAliasOfAShowInALanguageWithoutReadings_IsNoReading()
        => Assert.Null(RomanizationDetector.ReadingOf("Sousou no Frieren", TitleLanguage.English, TitleLanguage.English));

    [Theory]
    [InlineData("王ドロボウJING in Seventh Heaven", false)]
    [InlineData("나 혼자만 레벨업", false)]
    [InlineData("Sousou no フリーレン", false)]
    [InlineData("2015", false)]
    [InlineData("Ｋａｎｏｎ", true)]
    [InlineData("Kimi・Boku", true)]
    [InlineData("Sōsō no Furīren", true)]
    public void OnlyLatinLetters_CountAsLatin(string value, bool expected)
        => Assert.Equal(expected, RomanizationDetector.IsLatinOnly(value));

    [Theory]
    [InlineData("Sousou no Frieren", true)]
    [InlineData("Toradora!", true)]
    [InlineData("Sōsō no Furīren", false)]
    [InlineData("Xiān Nì", false)]
    [InlineData("Shikkaku Mon no Saikyou Kenja", true)]
    public void ASpellingWithoutDiacritics_IsPlain(string value, bool expected)
        => Assert.Equal(expected, RomanizationDetector.IsPlain(value));

    [Fact]
    public void TheShare_CountsTheLettersOfTheWordsThatParse()
    {
        // "Sousou" and "no" parse, "Frieren" does not: 8 of 15 letters.
        Assert.Equal(8.0 / 15, RomanizationDetector.Share("Sousou no Frieren", TitleLanguage.Romaji));
        Assert.Equal(1.0, RomanizationDetector.Share("Sōsō no Furīren", TitleLanguage.Romaji));
        Assert.Equal(1.0, RomanizationDetector.Share("Ore no Imōto ga Konna ni Kawaii Wake ga Nai", TitleLanguage.Romaji));
        Assert.Equal(0.0, RomanizationDetector.Share("!!", TitleLanguage.Romaji));
    }

    [Theory]
    [InlineData("kokkoku", true)]
    [InlineData("matcha", true)]
    [InlineData("gakkou", true)]
    [InlineData("jyouheki", true)]
    [InlineData("zyuu", true)]
    [InlineData("tenshi", true)]
    [InlineData("sempai", true)]
    [InlineData("konnichiwa", true)]
    [InlineData("charlotte", false)]
    [InlineData("explorer", false)]
    [InlineData("frieren", false)]
    [InlineData("nn", true)]
    public void RomajiWords_ParseIntoSyllables(string word, bool expected)
        => Assert.Equal(expected, RomanizationDetector.IsRomajiWord(word));

    [Theory]
    [InlineData("zhongguo", true)]
    [InlineData("xiangqing", true)]
    [InlineData("nvhai", true)]
    [InlineData("cangqiong", true)]
    [InlineData("twin", false)]
    [InlineData("chef", false)]
    [InlineData("sywz", false)]
    public void PinyinWords_ParseIntoSyllables(string word, bool expected)
        => Assert.Equal(expected, RomanizationDetector.IsPinyinWord(word));
}
