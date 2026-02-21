using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkMultiSource.Providers
{
    public sealed class TmdbApiClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";
        private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly string _apiKey;
        private readonly bool _useBearerToken;
        private readonly JsonSerializerOptions _jsonOptions;

        private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TmdbTvImagesResponse Data)> _tvImagesCache = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TmdbMovieImagesResponse Data)> _movieImagesCache = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, TmdbSeasonImagesResponse Data)> _seasonImagesCache = new();
        private readonly ConcurrentDictionary<int, (DateTimeOffset CachedAt, int? TvId)> _tvdbFindCache = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, int? TvId)> _imdbFindTvCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, (DateTimeOffset CachedAt, int? MovieId)> _tvdbFindMovieCache = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, int? MovieId)> _imdbFindMovieCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<int, (DateTimeOffset CachedAt, int? TvdbId)> _tvExternalIdsCache = new();
        private readonly ConcurrentDictionary<int, (DateTimeOffset CachedAt, int? TvdbId)> _movieExternalIdsCache = new();

        public TmdbApiClient(HttpClient httpClient, ILogger logger, string apiKey)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = (apiKey ?? string.Empty).Trim();

            // TMDb supports either a v3 API key (query param) or a v4 Read Access Token (Bearer).
            // Heuristic: v4 tokens are long JWT-like strings that typically start with "eyJ".
            _useBearerToken = _apiKey.Length > 60 && (_apiKey.StartsWith("eyJ", StringComparison.Ordinal) || _apiKey.Contains('.', StringComparison.Ordinal));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public static string? BuildImageUrl(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            // file_path already starts with '/'
            return string.Concat(ImageBaseUrl, filePath);
        }

        public async Task<TmdbTvImagesResponse?> GetTvImagesAsync(int tvId, string includeImageLanguages, CancellationToken cancellationToken)
        {
            if (!IsConfigured || tvId <= 0)
            {
                return null;
            }

            var cacheKey = $"tv:{tvId}:{includeImageLanguages}";
            if (_tvImagesCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.Data;
            }

            var url = BuildUrl($"/tv/{tvId}/images", includeImageLanguages);
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDb TV images request failed for id {TvId} (status: {Status}). Check your TMDb key/token.", tvId, response.StatusCode);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbTvImagesResponse>(json, _jsonOptions);
                if (data != null)
                {
                    _tvImagesCache[cacheKey] = (DateTimeOffset.UtcNow, data);
                }

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb TV images response for id {TvId}", tvId);
                return null;
            }
        }

        
        public async Task<TmdbMovieImagesResponse?> GetMovieImagesAsync(int movieId, string includeImageLanguages, CancellationToken cancellationToken)
        {
            if (!IsConfigured || movieId <= 0)
            {
                return null;
            }

            var cacheKey = $"movie:{movieId}:{includeImageLanguages}";
            if (_movieImagesCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.Data;
            }

            var url = BuildUrl($"/movie/{movieId}/images", includeImageLanguages);
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDb movie images request failed for id {MovieId} (status: {Status}). Check your TMDb key/token.", movieId, response.StatusCode);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbMovieImagesResponse>(json, _jsonOptions);
                if (data != null)
                {
                    _movieImagesCache[cacheKey] = (DateTimeOffset.UtcNow, data);
                }

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb movie images response for id {MovieId}", movieId);
                return null;
            }
        }

public async Task<TmdbSeasonImagesResponse?> GetSeasonImagesAsync(int tvId, int seasonNumber, string includeImageLanguages, CancellationToken cancellationToken)
        {
            if (!IsConfigured || tvId <= 0)
            {
                return null;
            }

            var cacheKey = $"season:{tvId}:{seasonNumber}:{includeImageLanguages}";
            if (_seasonImagesCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.Data;
            }

            var url = BuildUrl($"/tv/{tvId}/season/{seasonNumber}/images", includeImageLanguages);
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDb season images request failed for tv {TvId} season {Season} (status: {Status}).", tvId, seasonNumber, response.StatusCode);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbSeasonImagesResponse>(json, _jsonOptions);
                if (data != null)
                {
                    _seasonImagesCache[cacheKey] = (DateTimeOffset.UtcNow, data);
                }

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb season images response for tv {TvId} season {Season}", tvId, seasonNumber);
                return null;
            }
        }

        public async Task<int?> FindTvIdByTvdbAsync(int tvdbId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || tvdbId <= 0)
            {
                return null;
            }

            if (_tvdbFindCache.TryGetValue(tvdbId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.TvId;
            }

            var url = BuildFindUrl(tvdbId);
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb find(tvdb_id) failed for TVDB {TvdbId} (status: {Status})", tvdbId, response.StatusCode);
                _tvdbFindCache[tvdbId] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbFindResponse>(json, _jsonOptions);
                var tmdbId = data?.TvResults?.FirstOrDefault()?.Id;
                _tvdbFindCache[tvdbId] = (DateTimeOffset.UtcNow, tmdbId);
                return tmdbId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb find response for TVDB {TvdbId}", tvdbId);
                _tvdbFindCache[tvdbId] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }

        public async Task<int?> FindTvIdByImdbAsync(string imdbId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(imdbId))
            {
                return null;
            }

            var normalized = imdbId.Trim();
            if (_imdbFindTvCache.TryGetValue(normalized, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.TvId;
            }

            var url = BuildFindUrl(normalized, "imdb_id");
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb find(imdb_id) failed for IMDb {ImdbId} (status: {Status})", normalized, response.StatusCode);
                _imdbFindTvCache[normalized] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbFindResponse>(json, _jsonOptions);
                var tmdbId = data?.TvResults?.FirstOrDefault()?.Id;
                _imdbFindTvCache[normalized] = (DateTimeOffset.UtcNow, tmdbId);
                return tmdbId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb find response for IMDb {ImdbId}", normalized);
                _imdbFindTvCache[normalized] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }

        public async Task<int?> GetTvdbIdForTvAsync(int tvId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || tvId <= 0)
            {
                return null;
            }

            if (_tvExternalIdsCache.TryGetValue(tvId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.TvdbId;
            }

            var url = BuildUrl($"/tv/{tvId}/external_ids", "");
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb tv external_ids failed for id {TvId} (status: {Status})", tvId, response.StatusCode);
                _tvExternalIdsCache[tvId] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbTvExternalIds>(json, _jsonOptions);
                var tvdb = data?.TvdbId;
                _tvExternalIdsCache[tvId] = (DateTimeOffset.UtcNow, tvdb);
                return tvdb;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb tv external_ids response for id {TvId}", tvId);
                _tvExternalIdsCache[tvId] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }

        public async Task<int?> GetTvdbIdForMovieAsync(int movieId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || movieId <= 0)
            {
                return null;
            }

            if (_movieExternalIdsCache.TryGetValue(movieId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.TvdbId;
            }

            var url = BuildUrl($"/movie/{movieId}/external_ids", "");
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb movie external_ids failed for id {MovieId} (status: {Status})", movieId, response.StatusCode);
                _movieExternalIdsCache[movieId] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbMovieExternalIds>(json, _jsonOptions);
                var tvdb = data?.TvdbId;
                _movieExternalIdsCache[movieId] = (DateTimeOffset.UtcNow, tvdb);
                return tvdb;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb movie external_ids response for id {MovieId}", movieId);
                _movieExternalIdsCache[movieId] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }


        public async Task<int?> FindMovieIdByTvdbAsync(int tvdbId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || tvdbId <= 0)
            {
                return null;
            }

            if (_tvdbFindMovieCache.TryGetValue(tvdbId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.MovieId;
            }

            var url = BuildFindUrl(tvdbId.ToString(), "tvdb_id");
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb find(tvdb_id) failed for TVDB {TvdbId} (status: {Status})", tvdbId, response.StatusCode);
                _tvdbFindMovieCache[tvdbId] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbFindResponse>(json, _jsonOptions);
                var tmdbId = data?.MovieResults?.FirstOrDefault()?.Id;
                _tvdbFindMovieCache[tvdbId] = (DateTimeOffset.UtcNow, tmdbId);
                return tmdbId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb find response for TVDB {TvdbId}", tvdbId);
                _tvdbFindMovieCache[tvdbId] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }

        public async Task<int?> FindMovieIdByImdbAsync(string imdbId, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(imdbId))
            {
                return null;
            }

            var normalized = imdbId.Trim();
            if (_imdbFindMovieCache.TryGetValue(normalized, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.MovieId;
            }

            var url = BuildFindUrl(normalized, "imdb_id");
            using var response = await SendAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TMDb find(imdb_id) failed for IMDb {ImdbId} (status: {Status})", normalized, response.StatusCode);
                _imdbFindMovieCache[normalized] = (DateTimeOffset.UtcNow, null);
                return null;
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonSerializer.Deserialize<TmdbFindResponse>(json, _jsonOptions);
                var tmdbId = data?.MovieResults?.FirstOrDefault()?.Id;
                _imdbFindMovieCache[normalized] = (DateTimeOffset.UtcNow, tmdbId);
                return tmdbId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse TMDb find response for IMDb {ImdbId}", normalized);
                _imdbFindMovieCache[normalized] = (DateTimeOffset.UtcNow, null);
                return null;
            }
        }


        private string BuildUrl(string path, string includeImageLanguages)
        {
            var baseUrl = $"{BaseUrl}{path}";
            var query = new System.Collections.Generic.List<string>();

            // v3 key uses api_key query param; v4 token uses Authorization header.
            if (!_useBearerToken)
            {
                query.Add($"api_key={Uri.EscapeDataString(_apiKey)}");
            }

            if (!string.IsNullOrWhiteSpace(includeImageLanguages))
            {
                query.Add($"include_image_language={Uri.EscapeDataString(includeImageLanguages)}");
            }

            return query.Count == 0 ? baseUrl : $"{baseUrl}?{string.Join('&', query)}";
        }

        private string BuildFindUrl(int tvdbId)
        {
            return BuildFindUrl(tvdbId.ToString(), "tvdb_id");
        }

        private string BuildFindUrl(string externalId, string externalSource)
        {
            var baseUrl = $"{BaseUrl}/find/{Uri.EscapeDataString(externalId)}";
            var query = new System.Collections.Generic.List<string>
            {
                $"external_source={Uri.EscapeDataString(externalSource)}"
            };

            if (!_useBearerToken)
            {
                query.Insert(0, $"api_key={Uri.EscapeDataString(_apiKey)}");
            }

            return $"{baseUrl}?{string.Join('&', query)}";
        }

        private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_useBearerToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            return await _httpClient.SendAsync(request, cancellationToken);
        }
    }
}
