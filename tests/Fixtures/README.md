# Fixtures

The shapes here are TheTVDB v4's own, checked against live answers captured on
2026-09-26 with the project's licensed key: `/series/81797/extended` with and
without `meta=translations`, the default episode listing and its English
translation, `/series/81797/translations/eng`,
`/people/412417/extended?meta=translations`, `/characters/65111900`,
`/search`, `/genders`, `/sources/types`, `/languages`, a refused login and a
404. The captures themselves are not committed; these are trimmed to what the
tests need. No key or token is in any of them.

**Real values:** One Piece's record (names, overview, dates, score, status,
its names and overviews in German, English, Italian and Japanese with the
aliases among them, season types, the seasons' and episodes' IDs, the
companies, genres, tag, content ratings, remote IDs and the artwork paths it
has; the Japanese and German overviews cut to their first paragraph), the first two
episodes and the first special with their English translations, Luffy's credit,
and Mayumi Tanaka's own record (`people-412417-extended.json`, trimmed to one
role).

**Added by hand**, each for a case the real record does not have:

- artworks `1` to `9` have made-up IDs over real paths, but for `8` and `9`,
  an image on another host and TheTVDB's placeholder for a missing image;
- the remote IDs for an official website and an unknown site, the second US
  rating, a rating for no country, and Shueisha as a production company;
- every credit but Luffy's: Zoro (`7002`, person `602`), a guest star credited
  on one episode, and a creator and a director (One Piece lists no crew);
- the DVD and absolute listings, arranged by hand on the real episode shape,
  with a DVD extra (`100099`) the show does not have;
- `people-602-extended.json` (born in a year only, with a title's IMDb ID and a
  TMDB ID without its site's name) and `people-700001-extended.json` (a partial
  birth date and a death), which are not real people's records;
- the second search hit and the login token;
- `episode-361887-extended.json`, the first episode's own record trimmed to
  the fields the listing has, for looking up the show of an episode a hint
  names.

What the captures showed that the hand-written fixtures had guessed:

- a credit's `url` is the person's page, and credits carry no aliases;
- a person's slug has the ID in front (`412417-mayumi-tanaka`), a biography may
  be blank with the text under `translations.overviewTranslations`, and the
  language-less aliases are under `translations.aliases`;
- a person's IMDb and TMDB IDs are source types 16 and 15, and `/genders` calls
  3 "Other";
- the translated episode listing answers with the show itself, its `episodes`
  beside its fields;
- `meta=translations` adds `translations` to the show's record: a name per
  language, the aliases among them marked `isAlias`, an overview per
  language, and the aliases again without their languages, which is all
  `/series/{id}/translations/{language}` answers, for every language at once;
- the seasons on the show's record have no `name`, and a special's
  `absoluteNumber` is `0`;
- `pt` is Brazilian Portuguese and `zhtw` Taiwan's Chinese in TheTVDB's codes.
