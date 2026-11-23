using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.Plugin.Tvdb.Mapping;

/// <summary>
/// Tells a romanised reading of a Japanese, Chinese or Korean title apart
/// from a name or a title in another language written in the same letters.
/// </summary>
/// <remarks>
/// TvDB files a reading as a plain alias in the show's own language, with
/// nothing to mark it, so it is inferred: the alias has to be in the show's
/// original language, written in Latin letters only, and for Japanese and
/// Chinese most of its letters have to sit in words made entirely of romaji
/// or pinyin syllables. Korean romanisations allow too much to tell anything
/// apart, so the original language is their only check.
/// </remarks>
internal static class RomanizationDetector
{
    /// <summary>
    /// The least share of a title's letters that must be romaji for it to be
    /// read as romaji. Low enough for romaji around a foreign name or word
    /// ("Sōsō no Frieren", "Hibike! Euphonium"), which TvDB's readings often
    /// are, and above the English titles, which rarely parse at all.
    /// </summary>
    internal const double MinimumRomajiShare = 0.4;

    /// <summary>
    /// The least share of a title's letters that must be pinyin for it to be
    /// read as pinyin.
    /// </summary>
    internal const double MinimumPinyinShare = 0.7;

    /// <summary>
    /// The reading language of an alias, when it is a romanised reading of
    /// the title of a show in <paramref name="originalLanguage"/>.
    /// </summary>
    /// <param name="value">The alias.</param>
    /// <param name="language">The language the alias is filed in.</param>
    /// <param name="originalLanguage">The show's original language.</param>
    /// <returns>
    /// <see cref="TitleLanguage.Romaji"/>, <see cref="TitleLanguage.Pinyin"/>
    /// or <see cref="TitleLanguage.KoreanTranscription"/>, or
    /// <see langword="null"/> when the alias is no reading.
    /// </returns>
    public static TitleLanguage? ReadingOf(string value, TitleLanguage language, TitleLanguage originalLanguage)
    {
        if (ReadingLanguage(originalLanguage) is not { } reading || ReadingLanguage(language) != reading || !IsLatinOnly(value))
            return null;

        return reading switch
        {
            TitleLanguage.Romaji when Share(value, reading) < MinimumRomajiShare => null,
            TitleLanguage.Pinyin when Share(value, reading) < MinimumPinyinShare => null,
            _ => reading,
        };
    }

    /// <summary>
    /// The language a romanised reading of a title in a language is in.
    /// </summary>
    /// <param name="language">The title's language.</param>
    /// <returns>The reading language, or <see langword="null"/> for a language without one.</returns>
    public static TitleLanguage? ReadingLanguage(TitleLanguage language)
        => language switch
        {
            TitleLanguage.Japanese => TitleLanguage.Romaji,
            TitleLanguage.Chinese or TitleLanguage.ChineseSimplified or TitleLanguage.ChineseTraditional => TitleLanguage.Pinyin,
            TitleLanguage.Korean => TitleLanguage.KoreanTranscription,
            _ => null,
        };

    /// <summary>
    /// Whether a text is written in Latin letters: at least one, and no
    /// Hangul, kana or Han characters at all.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsLatinOnly(string value)
    {
        foreach (var rune in value.EnumerateRunes())
            if (IsHangul(rune.Value) || IsKana(rune.Value) || IsHan(rune.Value))
                return false;

        return Fold(value).Any(char.IsAsciiLetterLower);
    }

    /// <summary>
    /// Whether a text spells its letters without diacritics, the way a
    /// wapuro-style reading does.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns><see langword="true"/> when every letter is plain ASCII.</returns>
    public static bool IsPlain(string value)
        => value.All(character => char.IsAscii(character) || !char.IsLetter(character));

    /// <summary>
    /// The share of a title's letters that sit in words made entirely of the
    /// syllables of a reading language.
    /// </summary>
    /// <remarks>
    /// Diacritics are folded away, the text is lower-cased, apostrophes are
    /// dropped, and the words are the runs of the letters <c>a</c> to <c>z</c>.
    /// </remarks>
    /// <param name="value">The title.</param>
    /// <param name="reading">The reading language.</param>
    /// <returns>
    /// The share, from 0 to 1, or <see langword="null"/> for a reading
    /// language without a syllable test, such as
    /// <see cref="TitleLanguage.KoreanTranscription"/>.
    /// </returns>
    public static double? Share(string value, TitleLanguage reading)
    {
        Func<string, bool>? isWord = reading switch
        {
            TitleLanguage.Romaji => IsRomajiWord,
            TitleLanguage.Pinyin => IsPinyinWord,
            _ => null,
        };
        if (isWord is null)
            return null;

        var words = Words(value);
        var letters = words.Sum(word => word.Length);
        return letters is 0 ? 0 : (double)words.Where(isWord).Sum(word => word.Length) / letters;
    }

    /// <summary>
    /// Whether a lower-case word is made entirely of romaji syllables:
    /// Hepburn, Kunrei and wapuro spellings, with doubled consonants for a
    /// sokuon and a syllabic <c>n</c>.
    /// </summary>
    /// <param name="word">The word, folded and lower-case.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsRomajiWord(string word)
    {
        // parses[i] is whether the word from i on parses.
        var parses = new bool[word.Length + 1];
        parses[word.Length] = true;
        for (var i = word.Length - 1; i >= 0; i--)
        {
            var next = i + 1 < word.Length ? word[i + 1] : '\0';
            // A sokuon doubles the next consonant (kk, ss, tt, cch), or is a t
            // before ch.
            if ((next == word[i] && "kgsztdpbcfhjm".Contains(word[i]) || word[i] is 't' && next is 'c') && parses[i + 1])
            {
                parses[i] = true;
                continue;
            }

            // A syllabic n, written m before b, m and p.
            if ((word[i] is 'n' || word[i] is 'm' && next is 'b' or 'm' or 'p') && parses[i + 1])
            {
                parses[i] = true;
                continue;
            }

            for (var length = Math.Min(3, word.Length - i); length > 0 && !parses[i]; length--)
                parses[i] = parses[i + length] && _romajiSyllables.Contains(word.Substring(i, length));
        }

        return parses[0];
    }

    /// <summary>
    /// Whether a lower-case word is made entirely of pinyin syllables,
    /// without tone marks.
    /// </summary>
    /// <param name="word">The word, folded and lower-case.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsPinyinWord(string word)
    {
        // parses[i] is whether the word from i on parses.
        var parses = new bool[word.Length + 1];
        parses[word.Length] = true;
        for (var i = word.Length - 1; i >= 0; i--)
            for (var length = Math.Min(6, word.Length - i); length > 0 && !parses[i]; length--)
                parses[i] = parses[i + length] && _pinyinSyllables.Contains(word.Substring(i, length));

        return parses[0];
    }

    // The romaji syllables: the vowels, each consonant before each vowel (the
    // long vowels are vowels in a row), the contracted -ya, -yu and -yo
    // syllables in every spelling, and the sh-, ch-, ts- and foreign sounds.
    private static readonly FrozenSet<string> _romajiSyllables = new[] { "a", "i", "u", "e", "o" }
        .Concat("kgsztdnhbpmyrwfjv".SelectMany(consonant => "aiueo".Select(vowel => $"{consonant}{vowel}")))
        .Concat(new[] { "k", "g", "n", "h", "b", "p", "m", "r", "j", "z", "s", "t", "d", "f", "v", "sh", "ch" }.SelectMany(onset => "auoe".Select(vowel => $"{onset}y{vowel}")))
        .Concat(
        [
            "shi", "chi", "tsu", "sha", "shu", "sho", "she", "cha", "chu", "cho", "che",
            "tsa", "tsi", "tse", "tso", "thi", "thu", "dhi", "dhu", "dyu", "kwa", "gwa", "dzu",
        ])
        .ToFrozenSet(StringComparer.Ordinal);

    // The pinyin syllables: every initial before every final, which allows a
    // few that do not exist, and the spellings with y- and w- for the
    // syllables without an initial. Ü is written u once folded, or v.
    private static readonly FrozenSet<string> _pinyinSyllables = new[]
        {
            "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x", "zh", "ch", "sh", "r", "z", "c", "s", "y", "w", "",
        }
        .SelectMany(initial => new[]
            {
                "a", "o", "e", "i", "u", "v", "ai", "ei", "ui", "ao", "ou", "iu", "ie", "ve", "er", "an", "en", "in", "un", "vn", "ang", "eng",
                "ing", "ong", "ia", "iao", "ian", "iang", "iong", "ua", "uo", "uai", "uan", "uang", "ue", "ueng",
            }
            .Select(final => initial + final))
        .Concat(["yi", "wu", "yu", "yue", "yuan", "yun", "you", "yan", "yin", "yang", "ying", "yong", "ya", "yao", "ye", "wa", "wo", "wai", "wei", "wan", "wen", "wang", "weng", "r"])
        .ToFrozenSet(StringComparer.Ordinal);

    // The words of a text: diacritics folded away, lower-cased, apostrophes
    // dropped, and split into runs of a to z.
    private static List<string> Words(string value)
    {
        List<string> words = [];
        var word = new StringBuilder();
        foreach (var character in Fold(value))
        {
            if (char.IsAsciiLetterLower(character))
            {
                word.Append(character);
                continue;
            }

            if (character is '\'' or '‘' or '’' or 'ʼ' or '`')
                continue;

            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }

    // The text decomposed (full-width letters included) without its
    // combining marks, lower-cased.
    private static string Fold(string value)
    {
        var folded = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(character));

        return folded.ToString();
    }

    private static bool IsHangul(int codePoint)
        => codePoint is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF or >= 0xFFA0 and <= 0xFFDC;

    // The middle dot (U+30FB) is punctuation, and appears between Latin words.
    private static bool IsKana(int codePoint)
        => codePoint is >= 0x3040 and <= 0x30FA or >= 0x30FC and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F or >= 0x1B000 and <= 0x1B16F;

    private static bool IsHan(int codePoint)
        => codePoint is >= 0x2E80 and <= 0x2FDF or 0x3005 or 0x3007 or >= 0x3021 and <= 0x3029 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF;
}
