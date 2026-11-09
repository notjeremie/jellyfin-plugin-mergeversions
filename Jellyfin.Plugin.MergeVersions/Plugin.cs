using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.MergeVersions
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages 
    {
        public Plugin(IServerApplicationPaths appPaths, IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Merge Versions (Scoped)";

        public static Plugin Instance { get; private set; }

        public override string Description
            => "Merge Versions fork: merges versions per folder, per library or database-wide, preserves the original date added and merges automatically.";

        public PluginConfiguration PluginConfiguration => Configuration;

        /// <summary>
        /// Where the first date added of every title that ever had several versions is remembered. Next to the
        /// plugin configurations, a path Jellyfin always provides (DataFolderPath is not always set).
        /// </summary>
        public string FirstSeenFile => System.IO.Path.Combine(
            ApplicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.MergeVersions.Scoped.first-seen.json");

        private readonly Guid _id = new Guid("6d1c3a0e-5b7f-4e8a-9c2d-1f4b8e7a6c35");
        public override Guid Id => _id;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Merge Versions (Scoped)",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configurationpage.html"
                }
            };
        }
    }
}
