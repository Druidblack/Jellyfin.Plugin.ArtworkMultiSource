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
        }

        // Resolution ordering
        public bool SortImagesByResolutionDesc { get; set; }

        // Language ordering
        public bool SortImagesByLanguagePriority { get; set; }
        public string LanguagePriority1 { get; set; }
        public string LanguagePriority2 { get; set; }

        // TMDb
        public bool EnableTmdbImages { get; set; }
        public string TmdbApiKey { get; set; }
        public string TmdbImageLanguages { get; set; }
    }
}
