using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkMultiSource.Providers
{
    public class TvdbApiClient : IDisposable
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        private static readonly TimeSpan EpisodeCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan SeriesCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan MovieCacheDuration = TimeSpan.FromHours(6);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly string _projectApiKey;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TvdbEpisodeCacheEntry> _episodeCache = new();
        private readonly ConcurrentDictionary<int, TvdbSeriesCacheEntry> _seriesCache = new();
        private readonly ConcurrentDictionary<int, TvdbMovieCacheEntry> _movieCache = new();

        private string _subscriberPin = string.Empty;
        private string? _token;
        private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

        public TvdbApiClient(HttpClient httpClient, ILogger logger, string projectApiKey)
        {
            _httpClient = httpClient;
            _logger = logger;
            _projectApiKey = projectApiKey ?? string.Empty;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public void UpdateSubscriberPin(string? subscriberPin)
        {
            var normalized = subscriberPin?.Trim() ?? string.Empty;
            if (string.Equals(_subscriberPin, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _subscriberPin = normalized;
            _token = null;
            _tokenExpiry = DateTimeOffset.MinValue;
        }

        public async Task<TvdbEpisode?> FindEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken)
        {
            var episodes = await GetEpisodesForSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false);
            foreach (var episode in episodes)
            {
                if (episode.SeasonNumber == seasonNumber && episode.Number == episodeNumber)
                {
                    return episode;
                }
            }

            return null;
        }

        public async Task<List<TvdbEpisode>> GetEpisodesForSeriesAsync(int seriesId, CancellationToken cancellationToken)
        {
            if (_episodeCache.TryGetValue(seriesId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < EpisodeCacheDuration)
            {
                _logger.LogDebug("TVDB cache hit for series {SeriesId}", seriesId);
                return new List<TvdbEpisode>(cached.Episodes);
            }

            var episodes = new List<TvdbEpisode>();
            var nextUrl = $"{BaseUrl}/series/{seriesId}/episodes/default?page=0";
            var pageGuard = 0;

            while (!string.IsNullOrEmpty(nextUrl) && pageGuard < 20)
            {
                pageGuard++;
                using var response = await SendAuthorizedAsync(nextUrl, cancellationToken).ConfigureAwait(false);
                if (response == null)
                {
                    break;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVDB request for {Url} failed with status {Status}", nextUrl, response.StatusCode);
                    break;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<TvdbEpisodeListResponse>(body, _jsonOptions);
                if (data?.Data?.Episodes != null)
                {
                    episodes.AddRange(data.Data.Episodes);
                    _logger.LogDebug("Fetched {Count} episodes from {Url}", data.Data.Episodes.Count, nextUrl);
                }

                nextUrl = data?.Links?.Next;
            }

            _episodeCache[seriesId] = new TvdbEpisodeCacheEntry(DateTimeOffset.UtcNow, new List<TvdbEpisode>(episodes));
            _logger.LogInformation("Cached {Count} TVDB episodes for series {SeriesId}", episodes.Count, seriesId);

            return episodes;
        }

        public async Task<HttpResponseMessage> GetImageAsync(string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        public async Task<TvdbTranslation?> GetEpisodeTranslationAsync(int episodeId, string language, CancellationToken cancellationToken)
        {
            var url = $"{BaseUrl}/episodes/{episodeId}/translations/{language}";
            using var response = await SendAuthorizedAsync(url, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "TVDB translation request failed for episode {EpisodeId} lang {Language} (status: {Status})",
                    episodeId,
                    language,
                    response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var translation = JsonSerializer.Deserialize<TvdbTranslationResponse>(json, _jsonOptions);
            return translation?.Data;
        }

        public async Task<TvdbEpisode?> GetEpisodeByIdAsync(int episodeId, CancellationToken cancellationToken)
        {
            var url = $"{BaseUrl}/episodes/{episodeId}";
            using var response = await SendAuthorizedAsync(url, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB episode lookup failed for id {EpisodeId} (status: {Status})", episodeId, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<TvdbEpisodeResponse>(json, _jsonOptions);
            return data?.Data;
        }

        public async Task<TvdbSeriesExtended?> GetSeriesExtendedAsync(int seriesId, CancellationToken cancellationToken)
        {
            if (_seriesCache.TryGetValue(seriesId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < SeriesCacheDuration)
            {
                return cached.Series;
            }

            var url = $"{BaseUrl}/series/{seriesId}/extended";
            using var response = await SendAuthorizedAsync(url, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB series extended request failed for id {SeriesId} (status: {Status})", seriesId, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<TvdbSeriesExtendedResponse>(json, _jsonOptions);
            if (data?.Data != null)
            {
                _seriesCache[seriesId] = new TvdbSeriesCacheEntry(DateTimeOffset.UtcNow, data.Data);
            }

            return data?.Data;
        }

        public async Task<TvdbMovieExtended?> GetMovieExtendedAsync(int movieId, CancellationToken cancellationToken)
        {
            if (_movieCache.TryGetValue(movieId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < MovieCacheDuration)
            {
                return cached.Movie;
            }

            var url = $"{BaseUrl}/movies/{movieId}/extended";
            using var response = await SendAuthorizedAsync(url, cancellationToken).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TVDB movie extended request failed for id {MovieId} (status: {Status})", movieId, response?.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<TvdbMovieExtendedResponse>(json, _jsonOptions);
            if (data?.Data != null)
            {
                _movieCache[movieId] = new TvdbMovieCacheEntry(DateTimeOffset.UtcNow, data.Data);
            }

            return data?.Data;
        }

        private async Task<HttpResponseMessage?> SendAuthorizedAsync(string url, CancellationToken cancellationToken)
        {
            var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("TVDB token unavailable; skipping request to {Url}", url);
                return null;
            }

            var response = await SendWithBearerAsync(url, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            _logger.LogInformation("TVDB token expired, refreshing token and retrying {Url}", url);
            _token = null;
            _tokenExpiry = DateTimeOffset.MinValue;

            token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                return response;
            }

            response.Dispose();
            return await SendWithBearerAsync(url, token, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendWithBearerAsync(string url, string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_token) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _token;
            }

            if (string.IsNullOrWhiteSpace(_projectApiKey))
            {
                _logger.LogWarning("TVDB project API key is missing; cannot authenticate");
                return null;
            }

            await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_token) && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    return _token;
                }

                var loginPayload = new Dictionary<string, string>
                {
                    ["apikey"] = _projectApiKey
                };

                if (!string.IsNullOrWhiteSpace(_subscriberPin))
                {
                    loginPayload["pin"] = _subscriberPin;
                }

                var payload = JsonSerializer.Serialize(loginPayload);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{BaseUrl}/login", content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TVDB login failed with status {Status}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var login = JsonSerializer.Deserialize<TvdbLoginResponse>(json, _jsonOptions);
                _token = login?.Data?.Token;
                _tokenExpiry = DateTimeOffset.UtcNow.AddDays(28);

                if (string.IsNullOrWhiteSpace(_token))
                {
                    _logger.LogWarning("TVDB login returned no token");
                }
                else
                {
                    _logger.LogDebug("Obtained TVDB token; expires at {Expiry}", _tokenExpiry.ToString("u"));
                }

                return _token;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring TVDB token");
                return null;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public void Dispose()
        {
            _tokenLock.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed record TvdbEpisodeCacheEntry(DateTimeOffset CachedAt, List<TvdbEpisode> Episodes);
        private sealed record TvdbSeriesCacheEntry(DateTimeOffset CachedAt, TvdbSeriesExtended Series);
        private sealed record TvdbMovieCacheEntry(DateTimeOffset CachedAt, TvdbMovieExtended Movie);
    }
}
