using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArtworkMultiSource.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            // Ordering
            SortImagesByResolutionDesc = false;
            SortImagesByLanguagePriority = false;
            LanguagePriority1 = "ru";
            LanguagePriority2 = "en";

            // TMDb
            EnableTmdbImages = true;
            TmdbApiKey = string.Empty;
            // Optional. Comma-separated list like: "ru,en,null" (null = no language).
            // Leave blank to request all image languages from TMDb.
            TmdbImageLanguages = string.Empty;

            // TVDB
            TvdbSubscriberPin = string.Empty;

            // Scheduled refresh safety: preserve manually selected/local artwork by default.
            ReplaceAllImagesOnScheduledRefresh = false;
        }

        public bool SortImagesByResolutionDesc { get; set; }

        public bool SortImagesByLanguagePriority { get; set; }

        public string LanguagePriority1 { get; set; }

        public string LanguagePriority2 { get; set; }

        public bool EnableTmdbImages { get; set; }

        public string TmdbApiKey { get; set; }

        public string TmdbImageLanguages { get; set; }

        /// <summary>
        /// Optional TVDB subscriber PIN. The plugin always uses Jellyfin's public TVDB project key.
        /// </summary>
        public string TvdbSubscriberPin { get; set; }

        /// <summary>
        /// When true, the scheduled refresh task is allowed to replace existing images.
        /// Disabled by default to protect manually selected/local artwork.
        /// </summary>
        public bool ReplaceAllImagesOnScheduledRefresh { get; set; }
    }
}
