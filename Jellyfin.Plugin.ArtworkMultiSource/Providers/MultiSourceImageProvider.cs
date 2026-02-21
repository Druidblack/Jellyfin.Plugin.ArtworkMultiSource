using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ArtworkMultiSource.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkMultiSource.Providers
{
    /// <summary>
    /// Remote image provider that aggregates artwork from TVDB and TMDb.
    /// </summary>
    public class MultiSourceImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<MultiSourceImageProvider> _logger;
        private readonly TvdbApiClient _tvdbClient;

        private TmdbApiClient _tmdbClient;
        private string _tmdbKeySnapshot = string.Empty;

        // Runtime configuration (reloaded per request)
        private PluginConfiguration _config;
        private string _tmdbIncludeImageLanguages = string.Empty;

        private bool _sortByLanguagePriority;
        private string[] _languagePriorityOrder = Array.Empty<string>();

        private readonly HttpClient _httpClient;

        // Size cache for probing image dimensions (used for resolution sorting)
        private readonly ConcurrentDictionary<string, (int Width, int Height)> _sizeCache = new(StringComparer.OrdinalIgnoreCase);

        // Backdrops are intentionally constrained to a reasonable default to avoid overwhelming the image picker UI.
        // (These options were configurable in newer versions; in v1.0.7.2 we keep the simple fixed defaults.)
        private const int FixedMaxBackdrops = 5;
        private const int FixedBackdropMinWidth = 1920;
        private const int FixedBackdropMinHeight = 1080;
        private const double FixedBackdropMinAspectRatio = 1.78;

        public MultiSourceImageProvider(ILogger<MultiSourceImageProvider> logger)
        {
            _logger = logger;
            _config = Plugin.GetConfigurationSafe(_logger);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{Constants.PluginName}/1.0");

            _tvdbClient = new TvdbApiClient(_httpClient, _logger, Constants.TvdbProjectApiKey);
            _tmdbClient = new TmdbApiClient(_httpClient, _logger, _config.TmdbApiKey ?? string.Empty);
            _tmdbKeySnapshot = _config.TmdbApiKey ?? string.Empty;

            RefreshConfigDerivedFields();
        }

        public string Name => $"{Constants.PluginDisplayName} (TVDB+TMDb)";

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            // Images are served from public CDNs (TVDB / TMDb). Jellyfin expects the provider
            // to return a streamed HTTP response for the selected remote image.
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        public bool Supports(BaseItem item)
            => item is Series || item is Season || item is Movie;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            // IMPORTANT:
            // For Movies we must NOT provide ImageType.Thumb.
            // In Jellyfin the "Thumb" slot for movies is commonly used for extracted video thumbnails,
            // and pulling remote "thumbs" for movies is not desired in this plugin's workflow.
            if (item is Movie)
            {
                // Movies: do NOT provide Backdrops or Thumbs.
                // Backdrops for movies are handled better by other providers / local workflows,
                // and "Thumb" is commonly reserved for extracted video thumbnails.
                return new[] { ImageType.Primary, ImageType.Banner, ImageType.Logo, ImageType.Art };
            }

            // Series/Season may legitimately have remote thumbs (e.g. TVDB keyart/thumb variants).
            return new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Logo, ImageType.Art, ImageType.Thumb };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            // Ensure runtime toggles are honored without a server restart.
            RefreshConfigDerivedFields();

            var tvdbId = GetTvdbId(item);
            var tmdbId = GetTmdbId(item);
            var imdbId = GetImdbId(item);

            // Movie artwork
            if (item is Movie)
            {
                var movieImages = await GetMovieImagesAsync(item, tvdbId, tmdbId, imdbId, cancellationToken).ConfigureAwait(false);

                // Safety: never return Movie thumbs/backdrops even if any upstream source exposes them.
                // (Movies should keep Jellyfin's own thumbnail workflow intact and avoid remote backdrops.)
                movieImages = movieImages.Where(i => i.Type != ImageType.Thumb && i.Type != ImageType.Backdrop).ToList();

                return await ApplyOrderingAsync(movieImages, cancellationToken).ConfigureAwait(false);
            }

            // Series/Season require at least one identifier we can pivot from.
            // If TVDB/TMDb ids are missing but IMDb is present, we can resolve TMDb via /find and then pivot to TVDB.
            if (string.IsNullOrWhiteSpace(tvdbId) && string.IsNullOrWhiteSpace(tmdbId) && string.IsNullOrWhiteSpace(imdbId))
            {
                _logger.LogDebug("Missing TVDB, TMDb and IMDb ids; cannot fetch remote artwork for {Name}", item.Name);
                return Array.Empty<RemoteImageInfo>();
            }

            int? tvdbSeriesId = null;
            if (!string.IsNullOrWhiteSpace(tvdbId) && int.TryParse(tvdbId, out var parsedSeriesId))
            {
                tvdbSeriesId = parsedSeriesId;
            }

            // Resolve TMDb tv id if missing (common for TV libraries that only carry TVDB ids).
            int? tmdbSeriesId = null;
            if (_config.EnableTmdbImages && EnsureTmdbClientConfigured())
            {
                if (!string.IsNullOrWhiteSpace(tmdbId) && int.TryParse(tmdbId, out var parsedTmdbId))
                {
                    tmdbSeriesId = parsedTmdbId;
                }
                else if (tvdbSeriesId.HasValue)
                {
                    tmdbSeriesId = await _tmdbClient.FindTvIdByTvdbAsync(tvdbSeriesId.Value, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    tmdbSeriesId = await _tmdbClient.FindTvIdByImdbAsync(imdbId!, cancellationToken).ConfigureAwait(false);
                }
            }

            // Resolve TVDB series id if missing (common when the library uses TMDb as the primary metadata source).
            if (!tvdbSeriesId.HasValue && _config.EnableTmdbImages && EnsureTmdbClientConfigured() && tmdbSeriesId.HasValue)
            {
                var resolvedTvdb = await _tmdbClient.GetTvdbIdForTvAsync(tmdbSeriesId.Value, cancellationToken).ConfigureAwait(false);
                if (resolvedTvdb.HasValue)
                {
                    tvdbSeriesId = resolvedTvdb.Value;
                    tvdbId = resolvedTvdb.Value.ToString();
                    _logger.LogDebug("Resolved TVDB series id {TvdbId} from TMDb tv id {TmdbId} for {Name}", tvdbId, tmdbSeriesId.Value, item.Name);
                }
            }

            TvdbSeriesExtended? tvdbSeries = null;
            if (tvdbSeriesId.HasValue)
            {
                tvdbSeries = await _tvdbClient.GetSeriesExtendedAsync(tvdbSeriesId.Value, cancellationToken).ConfigureAwait(false);
            }

            // TMDb TV images
            TmdbTvImagesResponse? tmdbTv = null;
            if (_config.EnableTmdbImages && EnsureTmdbClientConfigured() && tmdbSeriesId.HasValue)
            {
                tmdbTv = await _tmdbClient.GetTvImagesAsync(tmdbSeriesId.Value, _tmdbIncludeImageLanguages, cancellationToken).ConfigureAwait(false);
            }

            var images = new List<RemoteImageInfo>();

            // Series images
            if (item is Series)
            {
                var seriesImages = new List<RemoteImageInfo>();

                if (tmdbTv != null)
                {
                    seriesImages.AddRange(ToTmdbImageInfos(tmdbTv.Posters, ImageType.Primary));
                    seriesImages.AddRange(ToTmdbImageInfos(tmdbTv.Logos, ImageType.Logo));
                }

                if (tvdbSeriesId.HasValue)
                {
                    seriesImages.AddRange(GetTvdbSeriesImages(tvdbSeriesId.Value, tvdbSeries));
                }

                // Dedup by URL
                images.AddRange(seriesImages
                    .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                    .GroupBy(i => i.Url!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()));

                // Backdrops: combine TVDB + TMDb, apply basic quality filtering and a fixed max.
                var max = FixedMaxBackdrops;

                var tvdbBackdrops = FilterBackdrops(GetTvdbBackdrops(tvdbSeriesId, tvdbSeries));
                var tmdbBackdrops = tmdbTv != null ? FilterBackdrops(ToTmdbImageInfos(tmdbTv.Backdrops, ImageType.Backdrop)) : new List<RemoteImageInfo>();

                var combinedBackdrops = tvdbBackdrops
                    .Concat(tmdbBackdrops)
                    .Where(b => !string.IsNullOrWhiteSpace(b.Url))
                    .GroupBy(b => b.Url!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (_config.SortImagesByResolutionDesc)
                {
                    combinedBackdrops = SortByResolutionDesc(combinedBackdrops);
                }

                combinedBackdrops = combinedBackdrops
                    .Take(max)
                    .ToList();

                images.AddRange(combinedBackdrops);
            }

            // Season images
            if (item is Season season)
            {
                var seasonNumber = season.IndexNumber;
                if (!seasonNumber.HasValue)
                {
                    seasonNumber = TryParseSeasonNumber(item.Name);
                }

                if (!seasonNumber.HasValue)
                {
                    _logger.LogDebug("Season image lookup skipped: no season number for {Name}", item.Name);
                    return Array.Empty<RemoteImageInfo>();
                }

                var seasonImages = new List<RemoteImageInfo>();

                // TVDB season posters/banners (main source)
                var displayOrder = season.Series?.DisplayOrder;
                if (string.IsNullOrWhiteSpace(displayOrder))
                {
                    displayOrder = "official";
                }

                if (!string.IsNullOrWhiteSpace(tvdbId))
                {
                    seasonImages.AddRange(await GetTvdbSeasonImagesAsync(tvdbId!, seasonNumber.Value, displayOrder!, cancellationToken, tvdbSeries).ConfigureAwait(false));
                }

                // TMDb season posters
                if (_config.EnableTmdbImages && EnsureTmdbClientConfigured() && tmdbSeriesId.HasValue)
                {
                    var tmdbSeason = await _tmdbClient.GetSeasonImagesAsync(tmdbSeriesId.Value, seasonNumber.Value, _tmdbIncludeImageLanguages, cancellationToken).ConfigureAwait(false);
                    if (tmdbSeason != null)
                    {
                        seasonImages.AddRange(ToTmdbImageInfos(tmdbSeason.Posters, ImageType.Primary));
                    }
                }

                images.AddRange(seasonImages
                    .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                    .GroupBy(i => i.Url!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()));
            }

            return await ApplyOrderingAsync(images, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<RemoteImageInfo>> ApplyOrderingAsync(IEnumerable<RemoteImageInfo> images, CancellationToken cancellationToken)
        {
            var list = images
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .ToList();

            return await ApplyResolutionOrderingIfEnabledAsync(list, cancellationToken).ConfigureAwait(false);
        }

        private bool EnsureTmdbClientConfigured()
        {
            var key = _config.TmdbApiKey ?? string.Empty;
            if (!string.Equals(_tmdbKeySnapshot, key, StringComparison.Ordinal))
            {
                _tmdbKeySnapshot = key;
                _tmdbClient = new TmdbApiClient(_httpClient, _logger, _tmdbKeySnapshot);
            }

            return _tmdbClient.IsConfigured;
        }

        private void RefreshConfigDerivedFields()
        {
            _config = Plugin.GetConfigurationSafe(_logger);

            // Rebuild TMDb client if the key/token was changed in the UI.
            EnsureTmdbClientConfigured();

            _tmdbIncludeImageLanguages = NormalizeTmdbIncludeImageLanguages(_config.TmdbImageLanguages);

            _sortByLanguagePriority = _config.SortImagesByLanguagePriority;
            _languagePriorityOrder = BuildLanguagePriorityOrder(_config.LanguagePriority1, _config.LanguagePriority2);
        }

        private static string? GetTvdbId(BaseItem item)
        {
            // Seasons usually do NOT carry provider ids themselves; inherit from the parent Series.
            if (item is Season season && season.Series != null)
            {
                var seriesId = season.Series.GetProviderId(MetadataProvider.Tvdb);
                if (!string.IsNullOrWhiteSpace(seriesId))
                {
                    return seriesId;
                }
            }

            var id = item.GetProviderId(MetadataProvider.Tvdb);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private static string? GetTmdbId(BaseItem item)
        {
            // Seasons usually do NOT carry provider ids themselves; inherit from the parent Series.
            if (item is Season season && season.Series != null)
            {
                var seriesId = season.Series.GetProviderId(MetadataProvider.Tmdb);
                if (!string.IsNullOrWhiteSpace(seriesId))
                {
                    return seriesId;
                }
            }

            var id = item.GetProviderId(MetadataProvider.Tmdb);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private static string? GetImdbId(BaseItem item)
        {
            var id = item.GetProviderId(MetadataProvider.Imdb);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private static int? TryParseSeasonNumber(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Common patterns: "Season 1", "S01", "Сезон 1", "Season 01 (2019)"
            var m = Regex.Match(name, @"(?i)\b(?:season|s|сезон)\s*0*(\d{1,2})\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                return n;
            }

            return null;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetMovieImagesAsync(BaseItem item, string? tvdbId, string? tmdbId, string? imdbId, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();

            int? tvdbMovieId = null;
            if (!string.IsNullOrWhiteSpace(tvdbId) && int.TryParse(tvdbId, out var parsedTvdb))
            {
                tvdbMovieId = parsedTvdb;
            }

            int? tmdbMovieId = null;
            if (!string.IsNullOrWhiteSpace(tmdbId) && int.TryParse(tmdbId, out var parsedTmdb))
            {
                tmdbMovieId = parsedTmdb;
            }

            // Resolve TMDb id if missing
            if (_config.EnableTmdbImages && EnsureTmdbClientConfigured() && !tmdbMovieId.HasValue)
            {
                if (tvdbMovieId.HasValue)
                {
                    tmdbMovieId = await _tmdbClient.FindMovieIdByTvdbAsync(tvdbMovieId.Value, cancellationToken).ConfigureAwait(false);
                }

                if (!tmdbMovieId.HasValue && !string.IsNullOrWhiteSpace(imdbId))
                {
                    tmdbMovieId = await _tmdbClient.FindMovieIdByImdbAsync(imdbId!, cancellationToken).ConfigureAwait(false);
                }
            }

            // Resolve TVDB movie id if missing (when the library only has TMDb ids).
            if (!tvdbMovieId.HasValue && _config.EnableTmdbImages && EnsureTmdbClientConfigured() && tmdbMovieId.HasValue)
            {
                var resolvedTvdb = await _tmdbClient.GetTvdbIdForMovieAsync(tmdbMovieId.Value, cancellationToken).ConfigureAwait(false);
                if (resolvedTvdb.HasValue)
                {
                    tvdbMovieId = resolvedTvdb.Value;
                    _logger.LogDebug("Resolved TVDB movie id {TvdbId} from TMDb movie id {TmdbId} for {Name}", tvdbMovieId.Value, tmdbMovieId.Value, item.Name);
                }
            }

            // TVDB movie images
            TvdbMovieExtended? tvdbMovie = null;
            if (tvdbMovieId.HasValue)
            {
                tvdbMovie = await _tvdbClient.GetMovieExtendedAsync(tvdbMovieId.Value, cancellationToken).ConfigureAwait(false);

                images.AddRange(GetTvdbMovieImages(tvdbMovieId.Value, tvdbMovie));
                // Movies: do not fetch/return remote backdrops.
            }

            // TMDb movie images
            if (_config.EnableTmdbImages && EnsureTmdbClientConfigured() && tmdbMovieId.HasValue)
            {
                var tmdbMovie = await _tmdbClient.GetMovieImagesAsync(tmdbMovieId.Value, _tmdbIncludeImageLanguages, cancellationToken).ConfigureAwait(false);
                if (tmdbMovie != null)
                {
                    images.AddRange(ToTmdbImageInfos(tmdbMovie.Posters, ImageType.Primary));
                    images.AddRange(ToTmdbImageInfos(tmdbMovie.Logos, ImageType.Logo));
                }
            }

            // Note: movies intentionally do not handle backdrops in this plugin.

            // Dedup by URL
            return images
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .GroupBy(i => i.Url!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static string NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "00";
            }

            var v = language.Trim();
            if (string.Equals(v, "00", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "null", StringComparison.OrdinalIgnoreCase))
            {
                return "00";
            }

            // TVDB sometimes returns ISO-639-2 (3-letter) codes; map common ones to ISO-639-1.
            if (v.Length == 3)
            {
                v = v.ToLowerInvariant();
                v = v switch
                {
                    "rus" => "ru",
                    "eng" => "en",
                    "deu" => "de",
                    "ger" => "de",
                    "fra" => "fr",
                    "fre" => "fr",
                    "spa" => "es",
                    "ita" => "it",
                    "jpn" => "ja",
                    "kor" => "ko",
                    "zho" => "zh",
                    "chi" => "zh",
                    "por" => "pt",
                    "pol" => "pl",
                    "ukr" => "uk",
                    _ => v
                };
            }

            return v;
        }

        private static bool IsNoLanguage(string? language)
            => string.Equals(NormalizeLanguage(language), "00", StringComparison.OrdinalIgnoreCase);

        private bool IsLanguageMatch(string? imageLanguage, string? desiredLanguage)
        {
            var img = NormalizeLanguage(imageLanguage);
            var desired = NormalizeLanguage(desiredLanguage);

            if (desired == "00")
            {
                return img == "00";
            }

            return string.Equals(img, desired, StringComparison.OrdinalIgnoreCase);
        }

        private int GetLanguageRank(RemoteImageInfo image)
        {
            if (_languagePriorityOrder.Length == 0)
            {
                return int.MaxValue;
            }

            var lang = NormalizeLanguage(image.Language);
            for (var i = 0; i < _languagePriorityOrder.Length; i++)
            {
                if (string.Equals(_languagePriorityOrder[i], lang, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            // Any non-priority language (including those not listed) goes after the explicit priorities.
            return _languagePriorityOrder.Length;
        }

        private static string[] BuildLanguagePriorityOrder(params string?[] values)
        {
            var list = new List<string>();
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var v = NormalizeLanguage(raw);
                if (!list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(v);
                }
            }

            return list.ToArray();
        }

        private static string NormalizeTmdbIncludeImageLanguages(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var v = value.Trim();

            // Treat "all" (user-friendly) as empty => do not send include_image_language at all.
            if (string.Equals(v, "all", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Normalize separators and whitespace
            var parts = v
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            // Ensure "null" is normalized to literal "null" for TMDb (represents no language).
            for (int i = 0; i < parts.Count; i++)
            {
                if (string.Equals(parts[i], "00", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[i], "none", StringComparison.OrdinalIgnoreCase))
                {
                    parts[i] = "null";
                }
            }

            // Deduplicate
            parts = parts
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return string.Join(",", parts);
        }

        private static double? ToRating(int? score)
        {
            if (!score.HasValue)
            {
                return null;
            }

            // TVDB score is usually 0..100
            return score.Value / 10.0;
        }

        private static string? GetTvdbArtworkUrl(TvdbArtwork artwork)
        {
            if (!string.IsNullOrWhiteSpace(artwork.Image))
            {
                return artwork.Image;
            }

            if (!string.IsNullOrWhiteSpace(artwork.Thumbnail))
            {
                return artwork.Thumbnail;
            }

            return null;
        }

        private static bool IsBackdropType(TvdbArtwork artwork)
        {
            // TVDB artwork type ids (commonly):
            // 3=backgrounds, 4=season backgrounds, etc.
            // Keep it tolerant: treat types that look like backdrops as backdrops.
            return artwork.Type == 3 || artwork.Type == 4 || artwork.Type == 10 || artwork.Type == 11;
        }

        private List<RemoteImageInfo> GetTvdbSeriesImages(int seriesId, TvdbSeriesExtended? series)
        {
            if (series?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            var list = new List<RemoteImageInfo>();

            list.AddRange(series.Artworks.Where(a => a.Type == 2 && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Primary,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                // IMPORTANT: Do not set image ratings. Jellyfin may use them for its own ordering,
                // which would override our language/resolution sorting.
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => a.Type == 1 && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Banner,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => (a.Type == 23 || a.Type == 5 || a.Type == 8) && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Logo,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => (a.Type == 22 || a.Type == 9) && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Art,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            list.AddRange(series.Artworks.Where(a => a.Type == 13 && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Thumb,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            return list;
        }

        private List<RemoteImageInfo> GetTvdbBackdrops(int? seriesId, TvdbSeriesExtended? series)
        {
            if (!seriesId.HasValue || series?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            var backdropCandidates = series.Artworks
                .Where(a => IsBackdropType(a))
                .Where(a => GetTvdbArtworkUrl(a) != null)
                .ToList();

            var list = new List<RemoteImageInfo>();

            foreach (var a in backdropCandidates)
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = GetTvdbArtworkUrl(a)!,
                    Type = ImageType.Backdrop,
                    Language = NormalizeLanguage(a.Language),
                    Width = a.Width,
                    Height = a.Height,
                    CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
                });
            }

            return list;
        }

        private List<RemoteImageInfo> GetTvdbMovieImages(int movieId, TvdbMovieExtended? movie)
        {
            if (movie?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            var list = new List<RemoteImageInfo>();

            // posters
            list.AddRange(movie.Artworks.Where(a => a.Type == 2 && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Primary,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            // banners
            list.AddRange(movie.Artworks.Where(a => a.Type == 1 && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Banner,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            // logos
            list.AddRange(movie.Artworks.Where(a => (a.Type == 23 || a.Type == 5 || a.Type == 8) && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Logo,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            // art
            list.AddRange(movie.Artworks.Where(a => (a.Type == 22 || a.Type == 9) && GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Art,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            return list;
        }

        private List<RemoteImageInfo> GetTvdbMovieBackdrops(int movieId, TvdbMovieExtended? movie)
        {
            if (movie?.Artworks == null)
            {
                return new List<RemoteImageInfo>();
            }

            return movie.Artworks
                .Where(a => IsBackdropType(a))
                .Where(a => GetTvdbArtworkUrl(a) != null)
                .Select(a => new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = GetTvdbArtworkUrl(a)!,
                    Type = ImageType.Backdrop,
                    Language = NormalizeLanguage(a.Language),
                    Width = a.Width,
                    Height = a.Height,
                    CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
                })
                .ToList();
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetTvdbSeasonImagesAsync(string tvdbId, int seasonNumber, string displayOrder, CancellationToken cancellationToken, TvdbSeriesExtended? series = null)
        {
            if (!int.TryParse(tvdbId, out var seriesId))
            {
                return Array.Empty<RemoteImageInfo>();
            }

            series ??= await _tvdbClient.GetSeriesExtendedAsync(seriesId, cancellationToken).ConfigureAwait(false);
            if (series?.Artworks == null || series.Seasons == null)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var seasonsMatchingNumber = series.Seasons
                .Where(s => s.Number == seasonNumber)
                .ToList();

            if (seasonsMatchingNumber.Count == 0)
            {
                return Array.Empty<RemoteImageInfo>();
            }

            var selectedSeason = seasonsMatchingNumber
                .FirstOrDefault(s => string.Equals(s.Type?.Type, displayOrder, StringComparison.OrdinalIgnoreCase))
                ?? seasonsMatchingNumber.FirstOrDefault(s => string.Equals(s.Type?.Type, "official", StringComparison.OrdinalIgnoreCase))
                ?? seasonsMatchingNumber.First();

            var seasonId = selectedSeason.Id;
            _logger.LogDebug("Selected TVDB season {SeasonId} for S{SeasonNumber} using type '{Type}' (display order: {DisplayOrder})",
                seasonId, seasonNumber, selectedSeason.Type?.Type ?? "unknown", displayOrder);

            var list = new List<RemoteImageInfo>();

            var posters = series.Artworks.Where(a => a.Type == 7 && a.SeasonId == seasonId);
            var banners = series.Artworks.Where(a => a.Type == 6 && a.SeasonId == seasonId);

            list.AddRange(posters.Where(a => GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Primary,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            list.AddRange(banners.Where(a => GetTvdbArtworkUrl(a) != null).Select(a => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = GetTvdbArtworkUrl(a)!,
                Type = ImageType.Banner,
                Language = NormalizeLanguage(a.Language),
                Width = a.Width,
                Height = a.Height,
                CommunityRating = _config.SortImagesByResolutionDesc ? null : ToRating(a.Score)
            }));

            // If no poster found, use season.Image from seasons list as a last resort
            if (!list.Any(l => l.Type == ImageType.Primary) && selectedSeason != null && !string.IsNullOrWhiteSpace(selectedSeason.Image))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = selectedSeason.Image,
                    Type = ImageType.Primary
                });
            }

            return list;
        }

        private List<RemoteImageInfo> ToTmdbImageInfos(IEnumerable<TmdbImage>? images, ImageType type)
        {
            var list = new List<RemoteImageInfo>();
            if (images == null)
            {
                return list;
            }

            foreach (var img in images)
            {
                var url = TmdbApiClient.BuildImageUrl(img.FilePath);
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                list.Add(new RemoteImageInfo
                {
                    // Keep ProviderName identical across sources so Jellyfin shows one combined list,
                    // and our ordering (language/resolution) is visible without per-provider grouping.
                    ProviderName = Name,
                    Url = url!,
                    Type = type,
                    // TMDb uses iso_639_1; in our model it's mapped to the 'Language' property.
                    Language = NormalizeLanguage(img.Language),
                    Width = img.Width,
                    Height = img.Height,
                    // If resolution sorting is enabled, suppress ratings so Jellyfin does not re-order the list.
                    CommunityRating = _config.SortImagesByResolutionDesc ? null : ToTmdbRating(img.VoteAverage)
                });
            }

            return list;
        }

        private static double? ToTmdbRating(double? voteAverage)
        {
            if (!voteAverage.HasValue)
            {
                return null;
            }

            // TMDb image votes are commonly in a 0-5 range; normalize to 0-10 for Jellyfin.
            var v = voteAverage.Value;
            if (v <= 5.0)
            {
                v *= 2.0;
            }

            return Math.Round(Math.Min(10.0, Math.Max(0.0, v)), 2);
        }

        private static bool PassBackdropQuality(RemoteImageInfo img)
        {
            var w = img.Width ?? 0;
            var h = img.Height ?? 0;

            if (w > 0 && w < FixedBackdropMinWidth)
            {
                return false;
            }

            if (h > 0 && h < FixedBackdropMinHeight)
            {
                return false;
            }

            if (w > 0 && h > 0)
            {
                var ar = (double)w / h;
                if (ar < FixedBackdropMinAspectRatio)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<RemoteImageInfo> FilterBackdrops(List<RemoteImageInfo> backdrops)
        {
            if (backdrops.Count == 0)
            {
                return backdrops;
            }

            // If we don't have sizes, keep them (sizes may be probed later for sorting).
            return backdrops
                .Where(PassBackdropQuality)
                .ToList();
        }

        private static long GetPixelArea(RemoteImageInfo image)
        {
            var w = image.Width ?? 0;
            var h = image.Height ?? 0;
            return (long)w * (long)h;
        }

        private static List<RemoteImageInfo> SortByResolutionDesc(List<RemoteImageInfo> images)
        {
            if (images.Count <= 1)
            {
                return images;
            }

            return images
                .OrderByDescending(GetPixelArea)
                .ThenByDescending(i => i.Width ?? 0)
                .ThenByDescending(i => i.Height ?? 0)
                .ThenBy(i => i.Language ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<RemoteImageInfo>> ApplyResolutionOrderingIfEnabledAsync(List<RemoteImageInfo> images, CancellationToken cancellationToken)
        {
            var useResolution = _config.SortImagesByResolutionDesc;
            var useLanguage = _sortByLanguagePriority && _languagePriorityOrder.Length > 0;

            if ((!useResolution && !useLanguage) || images.Count <= 1)
            {
                return images;
            }

            // Ensure we can compare resolutions across all sources (TVDB/TMDb/Fanart, etc.) when needed.
            // Some sources may omit width/height; probe missing sizes on-demand when resolution sorting is enabled.
            if (useResolution)
            {
                await PopulateMissingSizesAsync(images, cancellationToken);
            }

            // Sort within each image type, mixing all sources together.
            // This avoids "provider blocks" where each source is sorted separately.
            var ordered = new List<RemoteImageInfo>(images.Count);

            foreach (var type in new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Logo, ImageType.Art, ImageType.Thumb })
            {
                var group = images.Where(i => i.Type == type).ToList();
                if (group.Count == 0)
                {
                    continue;
                }

                if (useLanguage)
                {
                    var q = group.OrderBy(i => GetLanguageRank(i));

                    if (useResolution)
                    {
                        q = q
                            .ThenByDescending(GetPixelArea)
                            .ThenByDescending(i => i.Width ?? 0)
                            .ThenByDescending(i => i.Height ?? 0);
                    }

                    group = q
                        .ThenBy(i => NormalizeLanguage(i.Language), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    // Legacy behavior: resolution-only ordering.
                    group = group
                        .OrderByDescending(GetPixelArea)
                        .ThenByDescending(i => i.Width ?? 0)
                        .ThenByDescending(i => i.Height ?? 0)
                        .ThenBy(i => NormalizeLanguage(i.Language), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                ordered.AddRange(group);
            }

            // Any remaining / unknown types.
            var known = new HashSet<ImageType>(new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Logo, ImageType.Art, ImageType.Thumb });
            var rest = images.Where(i => !known.Contains(i.Type)).ToList();
            if (rest.Count > 0)
            {
                if (useLanguage)
                {
                    var q = rest.OrderBy(i => GetLanguageRank(i));

                    if (useResolution)
                    {
                        q = q
                            .ThenByDescending(GetPixelArea)
                            .ThenByDescending(i => i.Width ?? 0)
                            .ThenByDescending(i => i.Height ?? 0);
                    }

                    rest = q
                        .ThenBy(i => NormalizeLanguage(i.Language), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    rest = rest
                        .OrderByDescending(GetPixelArea)
                        .ThenByDescending(i => i.Width ?? 0)
                        .ThenByDescending(i => i.Height ?? 0)
                        .ThenBy(i => NormalizeLanguage(i.Language), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.Url ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                ordered.AddRange(rest);
            }

            return ordered;
        }

        private async Task PopulateMissingSizesAsync(List<RemoteImageInfo> images, CancellationToken cancellationToken)
        {
            var toProbe = images
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .Where(i => (i.Width ?? 0) <= 0 || (i.Height ?? 0) <= 0)
                .ToList();

            if (toProbe.Count == 0)
            {
                return;
            }

            const int maxConcurrency = 6;
            using var sem = new SemaphoreSlim(maxConcurrency);

            var tasks = toProbe.Select(async img =>
            {
                await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var size = await TryGetImageSizeAsync(img.Url!, cancellationToken).ConfigureAwait(false);
                    if (size.HasValue)
                    {
                        img.Width = size.Value.Width;
                        img.Height = size.Value.Height;
                    }
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task<(int Width, int Height)?> TryGetImageSizeAsync(string url, CancellationToken cancellationToken)
        {
            if (!_config.SortImagesByResolutionDesc) return null;

            if (_sizeCache.TryGetValue(url, out var cached))
            {
                return cached;
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                req.Headers.Range = new RangeHeaderValue(0, 65535); // first 64KB is enough for headers of most images

                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    return null;
                }

                // IMPORTANT: do not read the full image body. Some CDNs ignore Range and would send MBs.
                // We only need the first chunk to parse headers.
                await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[65536];
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                var header = buffer.AsSpan(0, read).ToArray();
                var size = ImageHeaderParser.TryParseImageSize(header);
                if (size.HasValue)
                {
                    _sizeCache[url] = size.Value;
                }

                return size;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Very small image header parser (PNG/JPEG/WebP) used only to get width/height for sorting.
    /// </summary>
    internal static class ImageHeaderParser
    {
        public static (int Width, int Height)? TryParseImageSize(byte[] data)
        {
            if (data == null || data.Length < 16)
            {
                return null;
            }

            // PNG: bytes 16-24 (IHDR)
            if (data.Length >= 24 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                var w = ReadInt32BigEndian(data, 16);
                var h = ReadInt32BigEndian(data, 20);
                if (w > 0 && h > 0)
                {
                    return (w, h);
                }
            }

            // JPEG: scan markers for SOF0/SOF2
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < data.Length)
                {
                    if (data[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }

                    byte marker = data[i + 1];
                    int len = (data[i + 2] << 8) + data[i + 3];
                    if (len <= 0) break;

                    // SOF0 (0xC0) or SOF2 (0xC2)
                    if (marker == 0xC0 || marker == 0xC2)
                    {
                        int h = (data[i + 5] << 8) + data[i + 6];
                        int w = (data[i + 7] << 8) + data[i + 8];
                        if (w > 0 && h > 0)
                        {
                            return (w, h);
                        }
                        break;
                    }

                    i += 2 + len;
                }
            }

            // WebP: RIFF....WEBP VP8X
            if (data.Length >= 30 &&
                data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
                data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            {
                // Look for VP8X chunk header "VP8X" at offset 12..16
                for (int i = 12; i + 16 < data.Length; i++)
                {
                    if (data[i] == (byte)'V' && data[i + 1] == (byte)'P' && data[i + 2] == (byte)'8' && data[i + 3] == (byte)'X')
                    {
                        // width-1 at i+8..i+10 (24-bit little endian), height-1 at i+11..i+13
                        int w = 1 + (data[i + 8] | (data[i + 9] << 8) | (data[i + 10] << 16));
                        int h = 1 + (data[i + 11] | (data[i + 12] << 8) | (data[i + 13] << 16));
                        if (w > 0 && h > 0)
                        {
                            return (w, h);
                        }
                        break;
                    }
                }
            }

            return null;
        }

        private static int ReadInt32BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
