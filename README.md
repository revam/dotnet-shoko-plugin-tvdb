# Shoko TvDB Metadata

Supplies TvDB v4 series, season and episode metadata to Shoko through the
metadata provider contract, under the `tvdb` metadata source.

This is a **reference implementation** as much as a plugin. It shows a
third-party provider doing the whole job on the contract: refreshing into the
core's stores, offering images, pausing while its source will not take work,
searching, auto-linking and matching episodes, and storing a source's other
episode orderings as global orderings. What it does, it does properly: real
authentication, real rate limiting, and tests.

## How it fits the core

The core owns the links, the stores and the jobs. The plugin owns the refresh.

- **The source.** `TvdbSources` registers `tvdb` (display name `TvDB`,
  `thetvdb` accepted as input, with a short description) from its static
  constructor, which `RegisterServices` runs by touching the class, before the
  core closes registration after plugin setup. The plugin reads it as
  `MetadataSource.Tvdb`, a C# 14 extension member. The old `TvDB` spelling reads
  back as the same source, ignoring case.
- **Identifiers.** Every entry is a `MetadataGuid`: `tvdb://series/81797`,
  `tvdb://season/31893`, `tvdb://episode/361887`, `tvdb://creator/412417`,
  `tvdb://character/65111900`, `tvdb://tag/63`, `tvdb://tag/genre/19`,
  `tvdb://studio/43210` and `tvdb://network/111`, each with TvDB's own ID.
- **No getters.** The core resolves every entry from its own stores, so a
  stored show still reads while the plugin is disabled or gone.
- **No queue code.** The core's `RefreshMetadataJob`, `SearchMetadataJob`,
  `DownloadMetadataImagesJob` and `PurgeMetadataJob` call the provider. The core
  decides when a show is due, forces, locks, syncs the episode links and
  queues the images afterwards.
- **Links.** Every link lives in the core's cross-reference store and is
  written through `IMetadataLinkingService`, so a user searches, links, unlinks,
  resets and re-matches through Shoko's own API.

## What a refresh writes

`RefreshSeries` fetches `/series/{id}/extended?meta=translations`, which
carries the show's names and overviews in every language, and the episodes in
the show's default season type, again in each language of the server's
episode title and description language orders but the show's own. For One
Piece (1,242 episodes, 500 to a page) with English and German in those orders
that is ten requests: the record, three pages of episodes, and three more in
each of English and German. It writes:

| Into | What |
|---|---|
| `IMetadataSeriesStore` | The show with its seasons and episodes: titles and overviews in the languages kept (see [Languages](#languages)), air dates, runtimes, status, original language, popularity, content ratings (one per country, under its two-letter code), resources (TvDB's page, IMDb, TMDB, Wikidata and the rest, each with its bare ID) and the show's IMDb, TMDB and TVmaze IDs as its cross-source IDs (`imdb://series/tt0388629`). An episode has none, TvDB's episode listing carrying no IDs elsewhere. |
| `IMetadataTagStore` | Genres as `tvdb://tag/genre/<id>` with `TagKind.Genre`, and tag options as `tvdb://tag/<id>` with their category. |
| `IMetadataStudioStore` | Studios (as animation studios) and production companies, and the networks the show aired on, each with its country. |
| `IMetadataPeopleStore` | The cast (`SetCast`, one credit per character) and crew (`SetCrew`, TvDB's job name, mapped onto a role type where one fits). Credits for a single episode are left out. A person is written with what their own record adds (see [People](#people)). A character is written with its name, its aliases and its page, which TvDB files among the show's people (`https://thetvdb.com/series/<slug>/people/<id>`) with no dereferrer, so a show without a slug leaves its characters unlinked. |
| `IMetadataOrderingService` | Each other season type the show has (DVD, absolute, alternate, regional and so on) as a global ordering, `tvdb://ordering/<series>-<type>`, one group per season, `tvdb://season/<series>-<type>-<number>`. An ordering is removed once the show no longer has its season type. |
| The plugin's own database | What only TvDB has (see [Its own database](#its-own-database)): the slug, status, season types and where the images are (`Shows`), the portraits of people and characters (`Portraits`), what each person's own record said and when it was fetched (`People`), and the slugs of the companies, for their pages (`Companies`). |

The show's own seasons are the season type TvDB uses for it by default. A
season TvDB lists no record for is made up as
`tvdb://season/<series>-<type>-<number>`, which never looks like one of
TvDB's own numeric season IDs, so an ordering group can never take a stored
season's ID.

A quick refresh leaves out the cast and crew, the orderings and the matching.
The networks are written on every refresh and the cast and crew on every
other one; whether the people, characters, studios and networks they name are
fetched on their own is the provider's `creator`, `character`, `studio` and
`network` kinds, all on by default. The orderings follow the refresh options,
and the settings where the options leave it open. After a full refresh the
episodes of every anime linked to the show are matched again, keeping the
links already there. The core removes the links to an episode TvDB no
longer lists when the refresh saves the show. A show TvDB no longer has is
left as it was stored. A refresh
that could not get the whole answer fails rather than write part of it: when
TvDB refuses the key partway through, loses a page of episodes, or lists
none for a show with episodes stored. The core then keeps the last refresh
time and tries again.

`CleanUp`, called after the core purges a show, forgets the show's record,
the portraits and records of people no stored show credits any more, and the
slugs of companies no stored show names. The core has
removed the show's orderings by then, as it does for any purged series.

## Languages

The translations kept follow the server's language orders, the ones
`IMetadataTextManager.GetLanguageOrder` hands a provider, as the bundled
TMDb plugin's do:

| Text | Kept in |
|---|---|
| The show's names | The series title language order and English, or every language with **Download All Titles** on |
| The show's overviews | The description language order and English, or every language with **Download All Overviews** on |
| An episode's names | The episode title language order |
| An episode's overviews | The description language order |
| A person's biography | The first in the description language order, then English, then any |

The show's own name and overview, in its original language, are always kept,
and so are its English ones, whatever the orders say: they cost nothing and
are what the auto-matching searches. An episode gets no such exception.
The show's record carries every translation, so the switches cost nothing; an
episode's translations cost one request per page of episodes per language, so
they never widen past the orders. The episodes are fetched once per language
either order names, the show's own aside.

A language is asked for under TvDB's code: ISO 639-2/T (three letters), but
`pt` for Brazilian Portuguese (`por` is Portugal's) and `zhtw` for Taiwan's,
traditional, Chinese. A regional language TvDB has no code for, such as
American English, takes its language's code. The order's main (original
language) entry stands for the show's original language. A language TvDB
cannot express, such as romaji, is skipped.

## People

The credits on a show carry only a person's name and photo. While the
provider's `creator` kind is on (the default), a refresh that writes the cast
and crew also fetches each credited person's own record,
`/people/{id}/extended?meta=translations`, and writes:

- their name as the record has it, with the credited name, their aliases and
  their name in every language as other names;
- one biography as the overview: the first in the server's description
  language order that has one, then English, then any;
- their gender, birth and death, where a date TvDB knows only part of
  (`1967`, `1930-04-00`) keeps the parts it knows;
- their page on TvDB under their slug, after the dereferrer link, and their
  IMDb (`nm` IDs), TMDB, TVmaze and Wikidata IDs and social accounts;
- their photo, when the credits had none.

**The cost** is one request per person, and only for a person never fetched or
fetched more than 30 days ago. The record is kept in the plugin's `People`
table and shared across every show the person is in, so a second show
with the same cast costs nothing. The core's people store stamps a person
whenever a refresh writes them, so its stamp cannot tell when a record was
fetched; the plugin's own record does. A show with a large cast fetches at
most **Person Details Per Refresh** people (50 by default), those never
fetched first in credit order, and the rest on later refreshes.

A person TvDB does not have (404) keeps what the credits say and is not
asked for again for 30 days. A failed fetch keeps what the credits say, or the
last record fetched, and never fails the refresh; a refused key, a rate limit
or a server error also stops the fetching until the next refresh. Turning the
`creator` kind off stops the fetching, but what was fetched before is still
written.

Left out, as the core's people store has no place for them: the birthplace,
the biographies in the other languages, the awards, races, tag options and
score, and the person's other roles. No original name is set either, since
the record does not say which language is the person's own.

## One entry at a time

The provider also refreshes a single person, character, studio or network when
the core asks (`IMetadataEntityProvider`): its `creator`, `character`,
`studio` and `network` kinds, turned on and off per kind like the rest. A
person comes from `/people/{id}/extended?meta=translations` and is kept in
`People` like the ones a show's refresh fetches, so the next refresh writes
them with it; a character from `/characters/{id}`, its page under the stored
show's slug; a company from `/companies/{id}`, written as the studio or the
network asked for, with its slug kept for its page
(`https://thetvdb.com/companies/<slug>`). A show's refresh keeps the slugs of
its companies too.

An entry is asked for again 30 days after it was last written, as a show's
refresh writes its people from their credits and fetches their own records
only up to **Person Details Per Refresh**.

## Images

`GetImages` offers, from what the refresh kept:

- a show's posters, backgrounds, banners and logos, its default poster marked;
- a season's posters, backgrounds and banners, its default poster first;
- an episode's thumbnail, as its backdrop;
- a person's photo and a character's image.

Every image is a resource ID completing the template
`https://artworks.thetvdb.com/banners/{0}`, which the plugin registers with
`IImageManager.RegisterTemplateUrl` on start. Images on other hosts, TvDB's
placeholders for a missing image and paths longer than the core's image table
holds are left out. The core decides what to download from the admin's image
settings for the source.

## Not configured

Without an API key, which only happens on a build from source with none set,
the provider reports `IsConfigured` as false with `NotConfiguredReason` saying
so. The core then skips auto-linking quietly and answers a search through the
API with `503 Service Unavailable`, and a refresh throws
`MetadataProviderNotConfiguredException`. A missing key is never a pause.

## Pausing

The provider reports a `MetadataProviderPauseStatus`, and the core holds its
jobs back, while:

- TvDB refused the key or PIN (for an hour, or until the settings are saved);
- TvDB rate limits the plugin (for as long as its `Retry-After` says);
- TvDB answered with a server error (for a minute).

## Linking and matching

- **Search** goes to `/search?type=series`, paged locally.
- **Auto-linking** searches on up to three of the anime's titles, stopping
  once a show matches on both title and date, and has the core's
  `IMetadataMatchingEngine` judge every show found (`MatchSeries`, as TMDB's
  auto-search is judged), against all of the anime's titles, the show's
  aliases and translations among its names.
  - **Episodes.** A stored show is judged with every season's episodes, and
    the first three other shows rated anything, over all the titles searched,
    with the episodes of TvDB's default listing for them (one call per
    500 episodes, each show fetched once per anime). The engine lines their
    air dates up with the anime's (`SeriesMatch.EpisodeAlignment`).
  - **Hints.** The shows the anime's links on other sources name, such as a
    linked TMDB show's TvDB ID, come from
    `IMetadataLinkingService.GetCrossSourceHints`; an episode named without
    its show is looked up on TvDB for it, and a film is left out. Each is
    judged by the engine with its episodes, from the store or TvDB, and
    handed back as `CrossSourceLink`, turned down only when it is restricted
    or of a kind the engine refuses. The core takes one only when the search
    took nothing it competes with, or only picks rated below it.
  - **What is taken.** TvDB's search is across every kind of show, and a
    name that only came close was wrong 16 times in 19 on a sample. So the
    show the engine picks is taken only when one of its names matches one of
    the anime's titles, when its names come close and its episodes line up
    conclusively with the anime's, or when a hint names it. A show matched on
    its dates alone is turned down even when they line up, as two weekly shows
    aired over the same weeks do. A pick turned down is a `TitleMismatch`
    saying so, and takes the shows it outranked down with it, unless they
    would have been taken.

  `FindAutoLinks` hands every show back, the one taken first and the rest with
  the reason and the title that found them, then the hints, and writes
  nothing: the core links the one taken, matches its episodes and refreshes
  it, and that refresh matches the episodes again.
- **Matching episodes** runs the core's `IMetadataMatchingEngine` with the
  date-and-title-within-seasons strategy over the stored episodes, within one
  season or one group of an ordering when asked, and can leave out the episodes
  other anime are already linked to.
- **Actions**: three per-series actions in the WebUI (auto-link, refresh,
  unlink), each a thin shell over the core's services. The core's own generic
  metadata actions cover the rest.

## Its own database

The plugin keeps what only TvDB has in an Entity Framework Core database of
its own, `TvdbDbContext`, registered from `RegisterServices` with
`AddPluginDbContext<Plugin, TvdbDbContext>("tvdb")`. The server owns the rest:
it configures the context (SQLite in WAL mode, at `data/<plugin-id>/tvdb.db3`),
applies its migrations while it starts, before `Ready`, copies the file before
each one, includes it in its backups, and removes it when the plugin is
uninstalled with its data. The plugin never names a provider.

There are four tables, one per kind of record, keyed by TvDB's ID (the
portraits by `creator/<id>` or `character/<id>`). Scalars are columns; the
artwork, names, biographies and remote IDs are JSON columns mapped as complex
collections, and the season posters and episode thumbnails are JSON objects
through a value converter. `TvdbStore` opens a context per call and serializes
its writes, so two refreshes crediting the same person never race.

Entity Framework Core is referenced for compiling only
(`ExcludeAssets="runtime;native"`): the server already loads it and SQLite, and
the plugin's output carries neither. The migrations are in
`source/Storage/Migrations`. Add one, from the repository root, with:

```bash
dotnet tool install --global dotnet-ef --version 10.0.12
dotnet ef migrations add <Name> --project source --startup-project design --output-dir Storage/Migrations
```

`design/` exists only for that: it references Entity Framework Core in full and
builds the context for the tool. It is not in the solution and is never
shipped. Keep migrations provider-neutral (no raw SQL), so they can later run on
a server's own database, and never edit one a release has shipped.

## Upgrading from the earlier build

Earlier builds kept the shows in `Tvdb/store.json` under the server's data
directory. On the first start with nothing stored in the core while shows are
linked, the plugin asks the core to refresh every linked show once, and it
deletes the old file.

## Credentials

**An official build needs no API key and no PIN.** It ships a licensed
TvDB project key, which the release workflow stamps into
`Constants.ProjectApiKey` from the `TVDB_PROJECT_KEY` secret. A licensed key
authenticates on its own, so a user of an official build sets nothing.

TvDB issues keys per project, not to individual users. The **API Key**
setting is for a build from source or a fork, which has no key stamped into
it: register the fork as a project at <https://thetvdb.com/api-information>
and paste its key there. A key in the setting wins over a stamped one.

The **Subscriber PIN** setting is optional. It only matters for a
user-supported key, which authenticates as the TvDB subscriber whose PIN
comes with it (found under the account's dashboard after subscribing at
<https://thetvdb.com/subscribe>). With a licensed key, leave it empty. When no
PIN is set, the `pin` field is left out of the login body entirely, since
TvDB rejects an empty one.

A key embedded in a binary can be read back out of it. The stamped key stays
out of the source tree, not out of the hands of anyone holding the DLL.

## Settings

| Setting | Default | What it does |
|---|---|---|
| API Key | *(none)* | For a build from source or a fork: its own TvDB project key. Official builds ship one, and a key set here wins over it. |
| Subscriber PIN | *(none)* | Optional. Only for a user-supported key; leave it empty with a licensed one. |
| Search Result Limit | `10` | How many search hits to judge per title when auto-linking. |
| Consider Existing Other Links | off | Leave out episodes another anime is linked to when matching. |
| Download All Titles | off | Keep the show's names in every language, not only in the series title language order. Episode names never widen. See [Languages](#languages). |
| Download All Overviews | off | Keep the show's overviews in every language, not only in the description language order. Episode overviews never widen. |
| Download Alternate Orderings | on | Store the other season types as orderings. |
| Person Details Per Refresh | `50` | The most people one refresh fetches their own records for, while the `creator` kind is on; the rest wait for a later refresh. One request per person not fetched in the last 30 days, shared across shows. |

Whether the provider answers at all is not a setting here. That belongs to
`IMetadataProviderManager`, which turns a provider on and off per source and per
entity type; a provider is off when it is first registered, so installing this
plugin does not on its own change where a title comes from. The image settings
are the core's, per source. There is no switch here for the cast and crew,
people's own records or networks either: a refresh always writes the credits
and networks, and the provider's kinds decide what is fetched.

## What it does not do

- **No movies.** `IMetadataMovieProvider` is not implemented. TvDB has a
  movie database, and adding it is a second provider shape.
- **No collections.** TvDB's lists are a user feature rather than a
  franchise graph, so there is nothing to gather that Shoko would recognise.
- **No relations or suggestions.** TvDB has neither.
- **No company logos.** TvDB's company artwork is not on the show record.
- **No season pages.** The core keeps no resources for a season
  (`MetadataSeasonData` and `ISeason` have none), so a season's page on
  TvDB, `https://thetvdb.com/dereferrer/season/{id}`, has nowhere to go.
- **Specials are placed in the aired order only.** TvDB says where a special
  airs among the episodes (`airsBeforeSeason`, `airsBeforeEpisode`,
  `airsAfterSeason`), in its aired order. The plugin stores that on the
  episode only when the show's own seasons are in the aired order. The other
  season types, kept as orderings, place no specials: an ordering reads every
  episode outside its special group as a regular one, numbered in its group.
- **No character overviews.** TvDB's character record lists the
  languages it has a name or overview in, but not the text, and no endpoint
  serves it, so fetching each character on its own would add nothing.

## Building

```bash
dotnet build Shoko.Plugin.Tvdb.slnx -c Release
dotnet test tests/Shoko.Plugin.Tvdb.Tests.csproj
scripts/pack.sh --zip
```

The test fixtures are trimmed from live TvDB answers, with a few records
added by hand for cases One Piece does not have; see
[`tests/Fixtures/README.md`](tests/Fixtures/README.md).

### Releasing

Publishing a release on GitHub or Gitea runs `.github/workflows/release.yml`
or `.gitea/workflows/release.yml`. Each checks that the tag matches the
project's `<Version>`, pulls the live manifest from the `metadata` branch
(seeded by hand once), stamps the project key, builds and packs with the
`shoko-build` tool pinned in `.config/dotnet-tools.json`, uploads the archive
and commits the updated manifest back to `metadata`.

- `TVDB_PROJECT_KEY` (secret, both hosts) is the licensed key. Unset, the
  build keeps the placeholder and its users need a key of their own.
- `ASSET_BASE_DOMAIN` (variable or secret, Gitea only) is the bare domain the
  Gitea archives are served from. No host is written in the tree.

### The abstractions reference

`source/Shoko.Plugin.Tvdb.csproj` takes `Shoko.Abstractions` as a
`PackageReference`, pinned in the `ShokoAbstractionsVersion` property in
`Directory.Build.props`, with `ExcludeAssets="runtime"`: the server supplies
the abstractions at runtime, and a plugin that ships its own copy resolves
`IPlugin` against that copy and is then skipped without an error. The plugin
needs no queue package; every job it runs is the core's. The same file takes
`Microsoft.EntityFrameworkCore.Sqlite` at the server's version with
`ExcludeAssets="runtime;native"`, for the same reason.

## License

MIT. See [LICENSE](LICENSE).
