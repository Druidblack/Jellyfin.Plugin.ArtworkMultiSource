namespace Jellyfin.Plugin.ArtworkMultiSource
{
    public static class Constants
    {
        // Keep a URL-safe internal page name (used for the embedded config page route)
        public const string PluginName = "Artwork Multi Source provider";

        // Display name shown in Jellyfin UI
        public const string PluginDisplayName = "Artwork Multi Source provider";

        // IMPORTANT: keep stable once installed, to avoid Jellyfin treating it as a different plugin.
        public const string PluginGuid = "6215a9ee-6574-44cd-a5c5-4604b3709a76";

        // TVDB v4 project API key (public project key used by multiple clients)
        public const string TvdbProjectApiKey = "7f7eed88-2530-4f84-8ee7-f154471b8f87";
    }
}
