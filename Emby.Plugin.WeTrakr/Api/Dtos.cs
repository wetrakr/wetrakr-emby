using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugin.WeTrakr.Api
{
    // ---------------------------------------------------------------
    // Device code (POST /oauth/device/code?platform=emby, /oauth/device/token)
    // ---------------------------------------------------------------

    public class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string UserCode { get; set; }
        [JsonPropertyName("verification_url")] public string VerificationUrl { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    public class DeviceTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; }
        [JsonPropertyName("username")] public string Username { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
    }

    // ---------------------------------------------------------------
    // Scrobble (POST /webhooks/emby/{token}). Mismo contrato que el plugin de
    // Jellyfin: la API reutiliza su logica.
    // ---------------------------------------------------------------

    public class ProviderIdsPayload
    {
        [JsonPropertyName("imdb")] public string Imdb { get; set; } = string.Empty;
        [JsonPropertyName("tmdb")] public string Tmdb { get; set; } = string.Empty;
        [JsonPropertyName("tvdb")] public string Tvdb { get; set; } = string.Empty;
    }

    public class ScrobblePayload
    {
        [JsonPropertyName("event")] public string Event { get; set; }
        [JsonPropertyName("user_id")] public string UserId { get; set; }
        [JsonPropertyName("user_name")] public string UserName { get; set; }
        [JsonPropertyName("is_owner")] public bool IsOwner { get; set; } = true;
        [JsonPropertyName("item_id")] public string ItemId { get; set; }
        [JsonPropertyName("item_type")] public string ItemType { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("provider_ids")] public ProviderIdsPayload ProviderIds { get; set; } = new ProviderIdsPayload();
        [JsonPropertyName("series_name")] public string SeriesName { get; set; }
        [JsonPropertyName("series_provider_ids")] public ProviderIdsPayload SeriesProviderIds { get; set; }
        [JsonPropertyName("season")] public int? Season { get; set; }
        [JsonPropertyName("episode")] public int? Episode { get; set; }
        [JsonPropertyName("position_ticks")] public long PositionTicks { get; set; }
        [JsonPropertyName("runtime_ticks")] public long RuntimeTicks { get; set; }
        [JsonPropertyName("is_paused")] public bool IsPaused { get; set; }
        [JsonPropertyName("played")] public bool Played { get; set; }

        [JsonPropertyName("is_favorite")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsFavorite { get; set; }

        [JsonPropertyName("user_rating")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? UserRating { get; set; }

        [JsonPropertyName("save_reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SaveReason { get; set; }
    }

    // ---------------------------------------------------------------
    // Sync (/integrations/emby/*)
    // ---------------------------------------------------------------

    public class ExtIds
    {
        [JsonPropertyName("tmdb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Tmdb { get; set; }

        [JsonPropertyName("imdb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Imdb { get; set; }

        [JsonPropertyName("tvdb")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Tvdb { get; set; }

        public bool IsEmpty => Tmdb == null && string.IsNullOrEmpty(Imdb) && Tvdb == null;
    }

    public class SyncSettings
    {
        [JsonPropertyName("import_watched")] public bool ImportWatched { get; set; } = true;
        [JsonPropertyName("import_progress")] public bool ImportProgress { get; set; } = true;
        [JsonPropertyName("import_ratings")] public bool ImportRatings { get; set; } = true;
        [JsonPropertyName("import_favorites")] public bool ImportFavorites { get; set; } = true;
        [JsonPropertyName("push_watched")] public bool PushWatched { get; set; } = true;
        [JsonPropertyName("push_progress")] public bool PushProgress { get; set; } = true;
        [JsonPropertyName("push_ratings")] public bool PushRatings { get; set; } = true;
        [JsonPropertyName("push_favorites")] public bool PushFavorites { get; set; } = true;
        [JsonPropertyName("push_planning")] public bool PushPlanning { get; set; } = true;
    }

    public class StatusSettings
    {
        [JsonPropertyName("sync")] public SyncSettings Sync { get; set; } = new SyncSettings();
    }

    public class StatusResponse
    {
        [JsonPropertyName("username")] public string Username { get; set; }
        [JsonPropertyName("settings")] public StatusSettings Settings { get; set; } = new StatusSettings();
        [JsonPropertyName("server_time")] public DateTime ServerTime { get; set; }
        [JsonPropertyName("journal_oldest")] public DateTime JournalOldest { get; set; }
        [JsonPropertyName("max_batch")] public int MaxBatch { get; set; } = 1000;
    }

    /// <summary>Un item del UserData de Emby que se sube a WeTrakr.</summary>
    public class ImportItem
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("ids")] public ExtIds Ids { get; set; }

        [JsonPropertyName("series_ids")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ExtIds SeriesIds { get; set; }

        [JsonPropertyName("season")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Season { get; set; }

        [JsonPropertyName("episode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Episode { get; set; }

        [JsonPropertyName("title")] public string Title { get; set; }

        [JsonPropertyName("series_title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SeriesTitle { get; set; }

        [JsonPropertyName("year")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Year { get; set; }

        [JsonPropertyName("played")] public bool Played { get; set; }
        [JsonPropertyName("new_play")] public bool NewPlay { get; set; }
        [JsonPropertyName("play_count")] public int PlayCount { get; set; }

        [JsonPropertyName("last_played_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastPlayedAt { get; set; }

        [JsonPropertyName("position_ticks")] public long PositionTicks { get; set; }
        [JsonPropertyName("runtime_ticks")] public long RuntimeTicks { get; set; }

        /// <summary>null = no se manda (en incremental: no ha cambiado).</summary>
        [JsonPropertyName("is_favorite")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsFavorite { get; set; }

        /// <summary>Se manda solo si RatingSet: null = nota borrada.</summary>
        [JsonPropertyName("rating")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public double? Rating { get; set; }

        [JsonIgnore] public bool RatingSet { get; set; }

        /// <summary>InternalId del item en Emby, para actualizar el estado local.</summary>
        [JsonIgnore] public long InternalId { get; set; }
    }

    public class ServerInfoPayload
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
    }

    public class ImportBatch
    {
        [JsonPropertyName("session_id")] public string SessionId { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; }
        [JsonPropertyName("final")] public bool Final { get; set; }
        [JsonPropertyName("server")] public ServerInfoPayload Server { get; set; }
        [JsonPropertyName("items")] public List<Dictionary<string, object>> Items { get; set; } = new List<Dictionary<string, object>>();
    }

    public class ImportAccepted
    {
        [JsonPropertyName("job_id")] public string JobId { get; set; }
        [JsonPropertyName("batch")] public int Batch { get; set; }
        [JsonPropertyName("accepted")] public int Accepted { get; set; }
        [JsonPropertyName("rejected")] public int Rejected { get; set; }

        /// <summary>Solo cuando la API esperó a procesar el lote (200): estado del
        /// job y lotes hechos. Con 202 vienen vacíos y hay que sondear.</summary>
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("batches_done")] public int BatchesDone { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
    }

    public class ImportSection
    {
        [JsonPropertyName("inserted")] public int Inserted { get; set; }
        [JsonPropertyName("updated")] public int Updated { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("removed")] public int Removed { get; set; }
    }

    public class ImportSummary
    {
        [JsonPropertyName("received")] public int Received { get; set; }
        [JsonPropertyName("resolved")] public int Resolved { get; set; }
        [JsonPropertyName("unresolved")] public int Unresolved { get; set; }
        [JsonPropertyName("progress")] public int Progress { get; set; }
        [JsonPropertyName("watched")] public ImportSection Watched { get; set; }
        [JsonPropertyName("favorites")] public ImportSection Favorites { get; set; }
        [JsonPropertyName("ratings")] public ImportSection Ratings { get; set; }
    }

    public class ImportJobStatus
    {
        [JsonPropertyName("job_id")] public string JobId { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("batches_received")] public int BatchesReceived { get; set; }
        [JsonPropertyName("batches_done")] public int BatchesDone { get; set; }
        [JsonPropertyName("summary")] public ImportSummary Summary { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
    }

    public class WatchedState
    {
        [JsonPropertyName("is_watched")] public bool IsWatched { get; set; }
        [JsonPropertyName("play_count")] public int PlayCount { get; set; }
        [JsonPropertyName("last_watched_at")] public DateTime? LastWatchedAt { get; set; }
        [JsonPropertyName("unknown")] public bool Unknown { get; set; }
    }

    /// <summary>Estado actual de un titulo en WeTrakr (snapshot / changes).</summary>
    public class TitleRow
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("ids")] public ExtIds Ids { get; set; }
        [JsonPropertyName("series_ids")] public ExtIds SeriesIds { get; set; }
        [JsonPropertyName("season")] public int? Season { get; set; }
        [JsonPropertyName("episode")] public int? Episode { get; set; }
        [JsonPropertyName("watched")] public WatchedState Watched { get; set; }
        [JsonPropertyName("explicit_unwatch")] public bool ExplicitUnwatch { get; set; }
        [JsonPropertyName("planning")] public bool Planning { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    }

    public class InProgressRow
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("ids")] public ExtIds Ids { get; set; }
        [JsonPropertyName("series_ids")] public ExtIds SeriesIds { get; set; }
        [JsonPropertyName("season")] public int? Season { get; set; }
        [JsonPropertyName("episode")] public int? Episode { get; set; }
        [JsonPropertyName("percent")] public double Percent { get; set; }
        [JsonPropertyName("position_ms")] public long? PositionMs { get; set; }
        [JsonPropertyName("runtime_s")] public long? RuntimeS { get; set; }
        [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
    }

    public class SnapshotPage
    {
        [JsonPropertyName("section")] public string Section { get; set; }
        [JsonPropertyName("titles")] public List<TitleRow> Titles { get; set; } = new List<TitleRow>();
        [JsonPropertyName("next_cursor")] public string NextCursor { get; set; }
        [JsonPropertyName("cursor_since")] public DateTime? CursorSince { get; set; }
    }

    public class ChangesPage
    {
        [JsonPropertyName("since")] public DateTime Since { get; set; }
        [JsonPropertyName("next_since")] public DateTime NextSince { get; set; }
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }

        /// <summary>Nada cambió desde `since`: titles e in_progress vienen vacíos y
        /// NO significan "ya no hay nada en curso". Solo se avanza el cursor.</summary>
        [JsonPropertyName("unchanged")] public bool Unchanged { get; set; }

        [JsonPropertyName("titles")] public List<TitleRow> Titles { get; set; } = new List<TitleRow>();
        [JsonPropertyName("in_progress")] public List<InProgressRow> InProgress { get; set; } = new List<InProgressRow>();

        /// <summary>Ajustes de sync de la cuenta, en cada respuesta: así el plugin
        /// no llama a /status en cada pasada.</summary>
        [JsonPropertyName("settings")] public StatusSettings Settings { get; set; }
        [JsonPropertyName("max_batch")] public int? MaxBatch { get; set; }
    }

    public class ApiError
    {
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
    }
}
