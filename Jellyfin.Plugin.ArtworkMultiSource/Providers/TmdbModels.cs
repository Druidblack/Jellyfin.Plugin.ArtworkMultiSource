using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArtworkMultiSource.Providers
{
    public sealed class TmdbTvImagesResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("backdrops")]
        public List<TmdbImage> Backdrops { get; set; } = new();

        [JsonPropertyName("posters")]
        public List<TmdbImage> Posters { get; set; } = new();

        [JsonPropertyName("logos")]
        public List<TmdbImage> Logos { get; set; } = new();
    }

    
    public sealed class TmdbMovieImagesResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("backdrops")]
        public List<TmdbImage> Backdrops { get; set; } = new();

        [JsonPropertyName("posters")]
        public List<TmdbImage> Posters { get; set; } = new();

        [JsonPropertyName("logos")]
        public List<TmdbImage> Logos { get; set; } = new();
    }

public sealed class TmdbSeasonImagesResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("posters")]
        public List<TmdbImage> Posters { get; set; } = new();
    }

    public sealed class TmdbImage
    {
        [JsonPropertyName("aspect_ratio")]
        public double? AspectRatio { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("iso_639_1")]
        public string? Language { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; set; }
    }

    public sealed class TmdbFindResponse
    {
        [JsonPropertyName("tv_results")]
        public List<TmdbFindTvResult> TvResults { get; set; } = new();

        [JsonPropertyName("movie_results")]
        public List<TmdbFindMovieResult> MovieResults { get; set; } = new();
    }

    public sealed class TmdbFindTvResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }


    public sealed class TmdbFindMovieResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    public sealed class TmdbTvExternalIds
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }

    public sealed class TmdbMovieExternalIds
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }

}
