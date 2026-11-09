using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    /// <summary>
    /// Runs a merge pass a short while after videos are added or identified, so a second copy of a title is
    /// grouped (and keeps its first date added) within minutes instead of at the next daily task.
    /// Events are debounced: a library scan adding hundreds of items triggers a single pass.
    /// </summary>
    public sealed class AutoMergeService : IHostedService, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly MergeVersionsManager _manager;
        private readonly ILogger<AutoMergeService> _logger;
        private readonly SemaphoreSlim _running = new(1, 1);
        private readonly Timer _timer;
        private int _moviesPending;
        private int _episodesPending;

        public AutoMergeService(ILibraryManager libraryManager, MergeVersionsManager manager, ILogger<AutoMergeService> logger)
        {
            _libraryManager = libraryManager;
            _manager = manager;
            _logger = logger;
            _timer = new Timer(_ => _ = RunAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            var config = Plugin.Instance?.PluginConfiguration;
            if (config == null || !config.AutoMergeOnLibraryChange)
            {
                return;
            }

            // Updates matter only when they can change the merge key: a new item gets its TMDB id from the
            // metadata download that follows its addition. Our own date edits (MetadataEdit) are ignored,
            // otherwise every pass would schedule the next one.
            var relevantUpdate = e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload)
                                 || e.UpdateReason.HasFlag(ItemUpdateType.MetadataImport);
            if (e.UpdateReason != ItemUpdateType.None && !relevantUpdate)
            {
                return;
            }

            switch (e.Item)
            {
                case Movie:
                    Interlocked.Exchange(ref _moviesPending, 1);
                    break;
                case Episode:
                    Interlocked.Exchange(ref _episodesPending, 1);
                    break;
                default:
                    return;
            }

            var delay = TimeSpan.FromSeconds(Math.Max(10, config.AutoMergeDelaySeconds));
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private async Task RunAsync()
        {
            if (!await _running.WaitAsync(0).ConfigureAwait(false))
            {
                // A pass is already running: try again once it has had time to finish.
                _timer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                if (Interlocked.Exchange(ref _moviesPending, 0) == 1)
                {
                    _logger.LogInformation("Automatic merge pass for movies");
                    await _manager.MergeMoviesAsync(null).ConfigureAwait(false);
                }

                if (Interlocked.Exchange(ref _episodesPending, 0) == 1)
                {
                    _logger.LogInformation("Automatic merge pass for episodes");
                    await _manager.MergeEpisodesAsync(null).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic merge pass failed");
            }
            finally
            {
                _running.Release();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _running.Dispose();
        }
    }
}
