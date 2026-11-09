using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
        private readonly IFileSystem _fileSystem;
        private readonly IVideoVersions _videoVersions;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task MergeMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated movies");

            var groups = GetMoviesFromLibrary()
                .Select(movie => (Key: GetMovieMergeKey(movie), Item: (BaseItem)movie))
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key, x => x.Item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var duplicateMovies = groups.Where(NeedsMerge).ToList();

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = movies.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateMovies.Count * 100);
            }

            await PreserveDatesAsync(groups, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = GetMoviesFromLibrary();
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {Name} ({Year})", movie.Name, movie.ProductionYear);
                await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)movies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated episodes");

            var episodes = GetEpisodesFromLibrary();
            var groups = episodes
                .Select(episode => (Key: GetScopedEpisodeMergeKey(episode), Item: (BaseItem)episode))
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key, x => x.Item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var duplicateEpisodes = groups.Where(NeedsMerge).ToList();

            _logger.LogInformation(
                "Found {EpisodeCount} episodes and {DuplicateGroupCount} duplicate episode groups",
                episodes.Count,
                duplicateEpisodes.Count);

            var current = 0;
            foreach (var episodeGroup in duplicateEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = episodeGroup.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(episodeGroup.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateEpisodes.Count * 100);
            }

            await PreserveDatesAsync(groups, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {EpisodeNumber} ({SeriesName})", episode.IndexNumber, episode.SeriesName);
                await _videoVersions.SplitAsync(episode.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)episodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .Select(m => m as Movie)
                .Where(m => m.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsEligible)
                .ToList();
        }

        private static PluginConfiguration Config => Plugin.Instance.PluginConfiguration;

        /// <summary>More copies than this in one group is an anomaly, not versions of one title.</summary>
        internal const int MaxVersionsPerGroup = 3;

        private bool NeedsMerge(IGrouping<string, BaseItem> group)
            => group.Count() > 1 && IsPlausible(group) && group.Any(item => item is Video video
                                                     && video.PrimaryVersionId == null
                                                     && !video.LinkedAlternateVersions.Any());

        /// <summary>
        /// Guards against groups that only share a key by accident. Seen on a real library: Jellyfin parsed
        /// "Show S01 - 01", "S01 - 02"... as S01E71 for every file, so a whole season shared one
        /// episode key. Real versions of one title carry the same name; a season of distinct episodes does not.
        /// </summary>
        private bool IsPlausible(IGrouping<string, BaseItem> group)
        {
            var count = group.Count();
            var names = group.Select(item => (item.Name ?? string.Empty).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (count <= MaxVersionsPerGroup && names == 1)
            {
                return true;
            }

            _logger.LogWarning(
                "Not merging {Count} items sharing key {Key}: {Names} different names, at most {Max} versions allowed",
                count, group.Key, names, MaxVersionsPerGroup);
            return false;
        }

        /// <summary>Key under which two movies are versions of the same title, or null to never merge it.</summary>
        internal string GetMovieMergeKey(Movie movie)
        {
            if (!movie.ProviderIds.TryGetValue("Tmdb", out var tmdb) || string.IsNullOrWhiteSpace(tmdb))
            {
                // Without an id we cannot tell two films sharing a folder apart (e.g. a folder of several
                // stand-up specials where only one is identified): never merge.
                return null;
            }

            switch (Config.MergeScope)
            {
                case MergeScope.Folder:
                    var folder = GetDirectory(movie.Path);
                    return folder == null ? null : $"folder:{folder}|tmdb:{tmdb}";
                case MergeScope.Library:
                    var library = GetLibraryKey(movie);
                    return library == null ? null : $"library:{library}|tmdb:{tmdb}";
                default:
                    return $"tmdb:{tmdb}";
            }
        }

        /// <summary>Key under which two episodes are versions of the same episode, or null to never merge it.</summary>
        internal string GetScopedEpisodeMergeKey(Episode episode)
        {
            switch (Config.MergeScope)
            {
                case MergeScope.Folder:
                    var folder = GetDirectory(episode.Path);
                    if (folder == null || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
                    {
                        return null;
                    }

                    return $"folder:{folder}|{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}";
                case MergeScope.Library:
                    var library = GetLibraryKey(episode);
                    return library == null ? null : $"library:{library}|{GetEpisodeMergeKey(episode)}";
                default:
                    return GetEpisodeMergeKey(episode);
            }
        }

        private static string GetDirectory(string path)
            => string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);

        private string GetLibraryKey(BaseItem item)
            => _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Id.ToString("N");

        // ---------------------------------------------------------------- date added

        private readonly ConcurrentDictionary<string, DateTime> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
        private bool _firstSeenLoaded;

        private static string FirstSeenPath
        {
            get
            {
                try
                {
                    return Plugin.Instance?.FirstSeenFile;
                }
                catch (Exception e) when (e is ArgumentException or NullReferenceException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Gives every version of a title the date its first version arrived. Titles that ever had several
        /// versions are remembered, so the copy that survives when an older one is deleted keeps that date too.
        /// </summary>
        private async Task PreserveDatesAsync(List<IGrouping<string, BaseItem>> groups, CancellationToken cancellationToken)
        {
            if (!Config.PreserveDateAdded)
            {
                return;
            }

            LoadFirstSeen();
            var changed = false;
            foreach (var group in groups)
            {
                var known = _firstSeen.TryGetValue(group.Key, out var remembered);
                if ((group.Count() < 2 && !known) || (group.Count() > 1 && !IsPlausible(group)))
                {
                    continue;
                }

                var earliest = group.Min(item => item.DateCreated);
                if (known && remembered < earliest)
                {
                    earliest = remembered;
                }

                if (!known || remembered != earliest)
                {
                    _firstSeen[group.Key] = earliest;
                    changed = true;
                }

                foreach (var item in group.Where(item => item.DateCreated > earliest.AddMinutes(1)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation("Keeping first date added {Date} for {Name}", earliest, item.Name);
                    item.DateCreated = earliest;
                    await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (changed)
            {
                SaveFirstSeen();
            }
        }

        private void LoadFirstSeen()
        {
            if (_firstSeenLoaded)
            {
                return;
            }

            _firstSeenLoaded = true;
            var path = FirstSeenPath;
            try
            {
                if (path != null && File.Exists(path))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path));
                    foreach (var pair in saved ?? new Dictionary<string, DateTime>())
                    {
                        _firstSeen[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Could not read {Path}; dates will only be kept for titles merged from now on", path);
            }
        }

        private void SaveFirstSeen()
        {
            var path = FirstSeenPath;
            if (path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, DateTime>(_firstSeen)));
                File.Move(temp, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(e, "Could not save {Path}", path);
            }
        }

        private static string GetEpisodeMergeKey(Episode episode)
        {
            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                return $"number:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}";
            }

            foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
            {
                if (episode.ProviderIds.TryGetValue(provider, out var providerId)
                    && !string.IsNullOrWhiteSpace(providerId))
                {
                    return $"provider:{episode.SeriesName}:{provider}:{providerId}";
                }
            }

            return $"title:{episode.SeriesName}:{episode.SeasonName}:{episode.Name}:{episode.ProductionYear}";
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
            }
        }
    }
}
