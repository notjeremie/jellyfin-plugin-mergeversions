using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    /// <summary>
    /// Where two videos must live to be considered versions of the same title.
    /// </summary>
    public enum MergeScope
    {
        /// <summary>Upstream behaviour: same provider id anywhere in the database (merges across libraries).</summary>
        Database = 0,

        /// <summary>Same provider id inside the same library only.</summary>
        Library = 1,

        /// <summary>Same folder on disk (movies: same TMDB id too; episodes: same season/episode numbers).</summary>
        Folder = 2,
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public String[] LocationsExcluded { get; set; }

        /// <summary>
        /// Defaults to <see cref="MergeScope.Folder"/>: the safe choice when the same film exists in several
        /// libraries (for instance a second library that mirrors the same files in another language).
        /// </summary>
        public MergeScope MergeScope { get; set; }

        /// <summary>
        /// Give every version of a title the date its first version was added, so a new or better copy of an
        /// existing film does not come back in "Recently added". Also remembered after the old copy is deleted.
        /// </summary>
        public bool PreserveDateAdded { get; set; }

        /// <summary>Merge automatically shortly after new videos are added, instead of waiting for the daily task.</summary>
        public bool AutoMergeOnLibraryChange { get; set; }

        /// <summary>Quiet period after the last library change before an automatic pass starts.</summary>
        public int AutoMergeDelaySeconds { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            MergeScope = MergeScope.Folder;
            PreserveDateAdded = true;
            AutoMergeOnLibraryChange = true;
            AutoMergeDelaySeconds = 120;
        }
    }
}
