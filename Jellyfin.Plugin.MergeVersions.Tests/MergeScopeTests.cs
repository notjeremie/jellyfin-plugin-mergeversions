using System.Security.Claims;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

/// <summary>
/// The fork's scoped merging: two libraries that mirror the same files under different paths (same TMDB ids),
/// folders holding several copies of one film, and folders holding several unrelated titles.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class MergeScopeTests
{
    private static readonly DateTime August = new(2026, 8, 6, 10, 44, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 11, 9, 16, 51, 0, DateTimeKind.Utc);

    private static Movie Movie(string path, string? tmdb, DateTime? added = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = tmdb ?? Path.GetFileName(path), Path = path, DateCreated = added ?? August };
        if (tmdb != null)
        {
            movie.ProviderIds["Tmdb"] = tmdb;
        }

        return movie;
    }

    private static Episode Episode(string path, int season, int number)
        => new() { Id = Guid.NewGuid(), Name = $"Example Show {season}x{number}", Path = path, SeriesName = "Example Show",
                   ParentIndexNumber = season, IndexNumber = number, DateCreated = August };

    private static (MergeVersionsManager Manager, List<Guid[]> Merged, List<BaseItem> Updated) Create(
        MergeScope scope, IEnumerable<BaseItem> items, bool preserveDates = false)
    {
        var paths = new Mock<IServerApplicationPaths>();
        var dir = Path.Combine(Path.GetTempPath(), "mv-tests-" + Guid.NewGuid().ToString("N"));
        paths.SetupGet(p => p.PluginsPath).Returns(dir);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(dir);
        _ = new ScopedPlugin(paths.Object, new PluginConfiguration { MergeScope = scope, PreserveDateAdded = preserveDates });

        var merged = new List<Guid[]>();
        var updated = new List<BaseItem>();
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .Callback<Guid[], ClaimsPrincipal, CancellationToken>((ids, _, _) => merged.Add(ids))
            .Returns(Task.CompletedTask);
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var list = items.ToList();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(list);
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns<Guid>(id => list.FirstOrDefault(i => i.Id == id));
        library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((item, _, _, _) => updated.Add(item))
            .Returns(Task.CompletedTask);
        var manager = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);
        return (manager, merged, updated);
    }

    [Fact]
    public async Task FolderScopeMergesCopiesOfOneFilmInItsFolder()
    {
        var old = Movie("/media/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001");
        var bluray = Movie("/media/Movies/Example Movie (2020)/Example.BLURAY.mkv", "1001", Today);
        var (manager, merged, _) = Create(MergeScope.Folder, new BaseItem[] { old, bluray });

        await manager.MergeMoviesAsync(null);

        var group = Assert.Single(merged);
        Assert.Equal(new[] { old.Id, bluray.Id }.OrderBy(i => i), group.OrderBy(i => i));
    }

    [Fact]
    public async Task FolderScopeNeverMergesAMirrorLibraryWithTheMainLibrary()
    {
        var main = Movie("/media/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001");
        var mirror = Movie("/media-mirror/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001");
        var (manager, merged, _) = Create(MergeScope.Folder, new BaseItem[] { main, mirror });

        await manager.MergeMoviesAsync(null);

        Assert.Empty(merged);
    }

    [Fact]
    public async Task DatabaseScopeStillMergesAcrossLibrariesAsUpstream()
    {
        var main = Movie("/media/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001");
        var mirror = Movie("/media-mirror/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001");
        var (manager, merged, _) = Create(MergeScope.Database, new BaseItem[] { main, mirror });

        await manager.MergeMoviesAsync(null);

        Assert.Single(merged);
    }

    [Fact]
    public async Task FolderScopeKeepsDifferentSpecialsSharingAFolderApart()
    {
        // A folder holds several unrelated titles; only some are identified.
        var identified = Movie("/media/Movies/Misc Collection/First.Title.mkv", "2002");
        var other = Movie("/media/Movies/Misc Collection/Second.Title.mkv", null);
        var another = Movie("/media/Movies/Misc Collection/Third.Title.mkv", null);
        var differentId = Movie("/media/Movies/Misc Collection/Fourth.Title.mkv", "777");
        var (manager, merged, _) = Create(MergeScope.Folder, new BaseItem[] { identified, other, another, differentId });

        await manager.MergeMoviesAsync(null);

        Assert.Empty(merged);
    }

    [Fact]
    public async Task FolderScopeMergesEpisodeCopiesButNotAcrossLibrariesOrEpisodes()
    {
        var s1e1 = Episode("/media/Shows/Example Show/Season 01/Example.S01E01.720p.mkv", 1, 1);
        var s1e1Better = Episode("/media/Shows/Example Show/Season 01/Example.S01E01.1080p.mkv", 1, 1);
        var s1e2 = Episode("/media/Shows/Example Show/Season 01/Example.S01E02.mkv", 1, 2);
        var mirrorS1e1 = Episode("/media-mirror/Shows/Example Show/Season 01/Example.S01E01.720p.mkv", 1, 1);
        var (manager, merged, _) = Create(MergeScope.Folder, new BaseItem[] { s1e1, s1e1Better, s1e2, mirrorS1e1 });

        await manager.MergeEpisodesAsync(null);

        var group = Assert.Single(merged);
        Assert.Equal(new[] { s1e1.Id, s1e1Better.Id }.OrderBy(i => i), group.OrderBy(i => i));
    }

    [Fact]
    public async Task FourCopiesOfTheSameFilmAreMerged()
    {
        var copies = Enumerable.Range(1, 4)
            .Select(n => (BaseItem)Movie($"/media/Movies/Some Film (2026)/Some.Film.2026.release{n}.mkv", "1616419"))
            .ToList();
        var (manager, merged, _) = Create(MergeScope.Folder, copies);

        await manager.MergeMoviesAsync(null);

        Assert.Equal(4, Assert.Single(merged).Length);
    }

    [Fact]
    public async Task AMisnumberedSeasonIsNeverMergedIntoOneEpisode()
    {
        // Real case: Jellyfin read "Show S01 - 01", "Show S01 - 02"... as S01E71 for every file of a season.
        var season = Enumerable.Range(1, 13).Select(n =>
        {
            var episode = Episode($"/media/Shows/Example Anime/Season 01/Example S01 - {n:00}.mkv", 1, 71);
            episode.Name = $"Example Anime S01 - {n:00}";
            episode.DateCreated = Today.AddMinutes(n);
            return (BaseItem)episode;
        }).ToList();
        var (manager, merged, updated) = Create(MergeScope.Folder, season, preserveDates: true);

        await manager.MergeEpisodesAsync(null);

        Assert.Empty(merged);
        Assert.Empty(updated);
    }

    [Fact]
    public async Task CopiesWithDifferentNamesAreNotMerged()
    {
        var a = Episode("/media/Shows/Example Show/Season 01/Example.S01E01.720p.mkv", 1, 1);
        var b = Episode("/media/Shows/Example Show/Season 01/Example.S01E01.1080p.mkv", 1, 1);
        a.Name = "Episode 1";
        b.Name = "Episode 2";
        var (manager, merged, _) = Create(MergeScope.Folder, new BaseItem[] { a, b });

        await manager.MergeEpisodesAsync(null);

        Assert.Empty(merged);
    }

    [Fact]
    public async Task NewCopyGetsTheFirstDateAddedBackAndTheDateSurvivesTheOldCopy()
    {
        var old = Movie("/media/Movies/Example Movie (2020)/Example.WEBRip.mkv", "1001", August);
        var bluray = Movie("/media/Movies/Example Movie (2020)/Example.BLURAY.mkv", "1001", Today);
        var (manager, _, updated) = Create(MergeScope.Folder, new BaseItem[] { old, bluray }, preserveDates: true);

        await manager.MergeMoviesAsync(null);

        Assert.Same(bluray, Assert.Single(updated));
        Assert.Equal(August, bluray.DateCreated);

        // A clean-up deletes the WEBRip; Jellyfin recreates the BluRay entry with today's date.
        var survivor = Movie(bluray.Path, "1001", Today);
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { survivor });
        library.Setup(l => l.GetItemById(survivor.Id)).Returns(survivor);
        var restored = new List<BaseItem>();
        library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((item, _, _, _) => restored.Add(item))
            .Returns(Task.CompletedTask);
        // A new manager reads the remembered date from disk, as after a server restart.
        var later = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), Mock.Of<IVideoVersions>(MockBehavior.Strict));

        await later.MergeMoviesAsync(null);

        Assert.Same(survivor, Assert.Single(restored));
        Assert.Equal(August, survivor.DateCreated);
    }

    [Fact]
    public async Task DatesAreFixedBeforeMergingAndOnAFreshCopyOfTheItem()
    {
        // Regression: saving the listed (pre-merge) copy after the merge wrote its old version links back,
        // hiding the new copy. Dates must be fixed first, on the item as currently stored.
        var old = Movie("/media/Movies/Some Film (2026)/Some.Film.WEBRip.mkv", "1081003", August);
        var listedNew = Movie("/media/Movies/Some Film (2026)/Some.Film.BLURAY.mkv", "1081003", Today);
        var stored = Movie(listedNew.Path, "1081003", Today);
        stored.Id = listedNew.Id;

        var paths = new Mock<IServerApplicationPaths>();
        var dir = Path.Combine(Path.GetTempPath(), "mv-tests-" + Guid.NewGuid().ToString("N"));
        paths.SetupGet(p => p.PluginsPath).Returns(dir);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(dir);
        _ = new ScopedPlugin(paths.Object, new PluginConfiguration { MergeScope = MergeScope.Folder, PreserveDateAdded = true });

        var calls = new List<string>();
        BaseItem? saved = null;
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { old, listedNew });
        library.Setup(l => l.GetItemById(listedNew.Id)).Returns(stored);
        library.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((item, _, _, _) => { calls.Add("date"); saved = item; })
            .Returns(Task.CompletedTask);
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("merge"))
            .Returns(Task.CompletedTask);
        var manager = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);

        await manager.MergeMoviesAsync(null);

        Assert.Equal(new[] { "date", "merge" }, calls);
        Assert.Same(stored, saved);
        Assert.Equal(August, stored.DateCreated);
    }

    [Fact]
    public async Task ATitleThatNeverHadTwoCopiesKeepsItsOwnDate()
    {
        var single = Movie("/media/Movies/Single Movie (2021)/Single.mkv", "3003", Today);
        var (manager, merged, updated) = Create(MergeScope.Folder, new BaseItem[] { single }, preserveDates: true);

        await manager.MergeMoviesAsync(null);

        Assert.Empty(merged);
        Assert.Empty(updated);
        Assert.Equal(Today, single.DateCreated);
    }

    private sealed class ScopedPlugin : Plugin
    {
        public ScopedPlugin(IServerApplicationPaths paths, PluginConfiguration configuration)
            : base(paths, Mock.Of<IXmlSerializer>())
        {
            Configuration = configuration;
        }
    }
}

/// <summary>Plugin.Instance is static: tests that replace it must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}
