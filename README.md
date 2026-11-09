<h1 align="center">Jellyfin Merge Versions (Scoped)</h1>
<h3 align="center">A fork of <a href="https://github.com/danieladov/jellyfin-plugin-mergeversions">danieladov/jellyfin-plugin-mergeversions</a> for <a href="https://jellyfin.org">Jellyfin</a> 12</h3>

This fork keeps the original idea (automatically group repeated movies and episodes as versions of a single item) and makes it safer to run on libraries where the same title legitimately exists more than once.

It has its own plugin identity ("Merge Versions (Scoped)", assembly `Jellyfin.Plugin.MergeVersions.Scoped`), so it can be installed next to the upstream plugin, but you should only enable one of them at a time.

## What this fork adds

### Merge scope

The `MergeScope` setting decides what counts as "the same item":

| Scope | Behaviour |
|---|---|
| **Folder** (default) | Items are merged only when they live in the same folder on disk. Movies must also share the same TMDB id; episodes must share the same season and episode numbers. |
| **Library** | Items are merged within a single library. |
| **Database** | Upstream behaviour: items are merged across the whole server, matched by provider id. |

Folder is the safe default when you keep a mirror library of the same files (for example the same films with another language's metadata): upstream would merge those into one item, the Folder scope never does.

### Guards

- An item without a TMDB id is never merged.
- A group is merged only if all its items share the same name, and only if it has at most 3 items.

Why: Jellyfin sometimes misparses file names. A real case was a whole season named like "Show S01 - 01", "Show S01 - 02", ... that was read as S01E71 for every file; the plugin would have merged the entire season into one episode.

### Date added is preserved

With `PreserveDateAdded` (on by default), every version gets the date added of the first version. The date is remembered in a small JSON file next to the plugin configurations, so it survives even after the old copy is deleted. A new or better copy of a film therefore no longer shows up in "Recently added".

### Automatic merging

`AutoMergeService` runs a debounced pass a few minutes after items are added or identified (`AutoMergeOnLibraryChange`, `AutoMergeDelaySeconds`, default 120 s), instead of waiting for the scheduled task.

## Credits and license

Based on the work of Daniel Arez Diaz ([danieladov](https://github.com/danieladov)) and contributors. MIT licensed; the original copyright notice is kept in `LICENSE`.

## Install


## From Repository
1. In Jellyfin, go to Dashboard -> Plugins -> Repositories -> add and paste this link: https://raw.githubusercontent.com/notjeremie/jellyfin-plugin-mergeversions/scoped-merge/manifest.json
2. Go to Catalog and search for "Merge Versions (Scoped)"
3. Click on it and install
4. Restart Jellyfin


## From .zip file
1. Download the .zip file from the [release page](https://github.com/notjeremie/jellyfin-plugin-mergeversions/releases)
2. Extract it and place the .dll file in a folder called ```plugins/Merge Versions Scoped``` under  the program data directory or inside the portable install directory
3. Restart Jellyfin

## User Guide
1. To merge your movies or episodes you can do it from Schedule task or directly from the configuration of the plugin.
2. Spliting is only avaible through the configuration

Merge and split use the installed Jellyfin server's native video-version actions. No URL or API key configuration is needed. Manual operations require administrator permissions.

This does not migrate or repair local-version groups created by earlier plugin releases. Split follows Jellyfin's native behavior for linked alternate versions; it does not detach local alternate versions.

Cancellation stops the scan between groups, after the current native operation finishes.



## Build Process
1. Clone or download this repository
2. Ensure you have .NET Core SDK setup and installed
3. Build plugin with following command.
```sh
dotnet publish Jellyfin.Plugin.MergeVersions/Jellyfin.Plugin.MergeVersions.csproj --configuration Release --output bin
```
4. Place the resulting .dll file in a folder called ```plugins/Merge versions``` under  the program data directory or inside the portable install directory

## Tests

```sh
dotnet test Jellyfin.Plugin.MergeVersions.sln --configuration Release
```

The tests cover controller discovery/dispatch using a test double, DI scope lifetime, error handling, authorization requirements, asynchronous completion and cancellation. They do not replace a merge/split smoke test against a Jellyfin test library.

The adapter discovers `Jellyfin.Api.Controllers.VideosController` through MVC and resolves it from Jellyfin's service container. If Jellyfin changes the action signatures, operations fail with a compatibility error instead of falling back to manual relationship changes.


