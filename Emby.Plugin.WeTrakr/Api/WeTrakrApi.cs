using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>La API respondio 401: el token del enlace ya no vale.</summary>
    public class InvalidTokenException : Exception
    {
        public InvalidTokenException() : base("WeTrakr rejected the token (401). Link the account again.") { }
    }

    /// <summary>Respuesta de error de la API con su codigo HTTP.</summary>
    public class ApiException : Exception
    {
        public ApiException(HttpStatusCode status, string error, string message)
            : base(string.IsNullOrEmpty(message) ? (error ?? status.ToString()) : message)
        {
            Status = status;
            Error = error;
        }

        public HttpStatusCode Status { get; }
        public string Error { get; }
    }

    /// <summary>
    /// Cliente HTTP de la API de WeTrakr. Un HttpClient estatico para todo el
    /// plugin (el SDK de Emby no da IHttpClientFactory).
    /// </summary>
    public static class WeTrakrApi
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static string UserAgent => "WeTrakr-Emby-Official/" + (Plugin.Instance?.PluginVersion ?? "0");

        private static string BaseUrl
        {
            get
            {
                var url = Plugin.Instance?.Configuration?.ApiBaseUrl;
                if (string.IsNullOrWhiteSpace(url)) url = "https://api.wetrakr.com";
                return url.TrimEnd('/');
            }
        }

        // -----------------------------------------------------------
        // Device code
        // -----------------------------------------------------------

        public static Task<DeviceCodeResponse> RequestDeviceCode(CancellationToken ct)
        {
            return Send<DeviceCodeResponse>(HttpMethod.Post, "/oauth/device/code?platform=emby", null, null, ct);
        }

        /// <summary>
        /// Un sondeo. Devuelve el token cuando el usuario ya activo el codigo,
        /// null mientras sigue pendiente. Lanza ApiException si caduco o se denego.
        /// </summary>
        public static async Task<DeviceTokenResponse> PollDeviceToken(string deviceCode, CancellationToken ct)
        {
            try
            {
                return await Send<DeviceTokenResponse>(HttpMethod.Post, "/oauth/device/token", new { device_code = deviceCode }, null, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Error == "authorization_pending" || ex.Error == "slow_down")
            {
                return null;
            }
        }

        // -----------------------------------------------------------
        // Scrobble
        // -----------------------------------------------------------

        public static Task SendScrobble(string token, ScrobblePayload payload, CancellationToken ct)
        {
            return Send<object>(HttpMethod.Post, "/webhooks/emby/" + Uri.EscapeDataString(token), payload, null, ct);
        }

        // -----------------------------------------------------------
        // Sync
        // -----------------------------------------------------------

        public static Task<StatusResponse> GetStatus(SyncContext ctx, CancellationToken ct)
        {
            return Send<StatusResponse>(HttpMethod.Get, "/integrations/emby/status", null, ctx, ct);
        }

        public static Task<ImportAccepted> PostImport(SyncContext ctx, ImportBatch batch, CancellationToken ct)
        {
            return Send<ImportAccepted>(HttpMethod.Post, "/integrations/emby/import", batch, ctx, ct);
        }

        public static Task<ImportJobStatus> GetImportJob(SyncContext ctx, string jobId, CancellationToken ct)
        {
            return Send<ImportJobStatus>(HttpMethod.Get, "/integrations/emby/import/" + Uri.EscapeDataString(jobId), null, ctx, ct);
        }

        public static Task<SnapshotPage> GetSnapshot(SyncContext ctx, string section, string cursor, CancellationToken ct)
        {
            var path = "/integrations/emby/snapshot?section=" + section + (string.IsNullOrEmpty(cursor) ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            return Send<SnapshotPage>(HttpMethod.Get, path, null, ctx, ct);
        }

        /// <summary>
        /// Cambios desde `since`. Sin `full`, la API responde `unchanged` tras una
        /// comprobación barata cuando no hay nada: es la llamada con la que se
        /// sondea. Con `full` lee siempre (pasada completa).
        /// </summary>
        public static Task<ChangesPage> GetChanges(SyncContext ctx, DateTime since, bool full, CancellationToken ct)
        {
            var path = "/integrations/emby/changes?since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("o")) + (full ? "&full=1" : string.Empty);
            return Send<ChangesPage>(HttpMethod.Get, path, null, ctx, ct);
        }

        // -----------------------------------------------------------
        // Transporte
        // -----------------------------------------------------------

        private static async Task<T> Send<T>(HttpMethod method, string path, object body, SyncContext ctx, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(method, BaseUrl + path))
            {
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (ctx != null)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Token);
                    req.Headers.TryAddWithoutValidation("X-Emby-User-Id", ctx.EmbyUserId);
                    req.Headers.TryAddWithoutValidation("X-Emby-User-Name", ctx.EmbyUserName ?? string.Empty);
                    if (!string.IsNullOrEmpty(ctx.ServerId)) req.Headers.TryAddWithoutValidation("X-Emby-Server-Id", ctx.ServerId);
                    if (!string.IsNullOrEmpty(ctx.ServerName)) req.Headers.TryAddWithoutValidation("X-Emby-Server-Name", ctx.ServerName);
                    if (!string.IsNullOrEmpty(ctx.ServerVersion)) req.Headers.TryAddWithoutValidation("X-Emby-Server-Version", ctx.ServerVersion);
                    req.Headers.TryAddWithoutValidation("X-WeTrakr-Plugin-Version", Plugin.Instance?.PluginVersion ?? "0");
                }
                if (body != null)
                {
                    // Con Content-Length (no chunked): algunos proxies lo exigen.
                    var json = JsonSerializer.Serialize(body, body.GetType(), Json);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using (var res = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = res.Content == null ? string.Empty : await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (res.StatusCode == HttpStatusCode.Unauthorized && ctx != null) throw new InvalidTokenException();
                    if (!res.IsSuccessStatusCode)
                    {
                        ApiError err = null;
                        try { err = string.IsNullOrEmpty(text) ? null : JsonSerializer.Deserialize<ApiError>(text, Json); } catch { /* cuerpo no JSON */ }
                        throw new ApiException(res.StatusCode, err?.Error, err?.Message);
                    }
                    if (typeof(T) == typeof(object) || string.IsNullOrEmpty(text)) return default;
                    return JsonSerializer.Deserialize<T>(text, Json);
                }
            }
        }

        /// <summary>Item de import en el formato del API: los campos opcionales
        /// solo viajan cuando tienen algo que decir.</summary>
        public static Dictionary<string, object> ToPayload(ImportItem it)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = it.Type,
                ["ids"] = it.Ids,
                ["title"] = it.Title,
                ["played"] = it.Played,
                ["new_play"] = it.NewPlay,
                ["play_count"] = it.PlayCount,
                ["position_ticks"] = it.PositionTicks,
                ["runtime_ticks"] = it.RuntimeTicks
            };
            if (it.SeriesIds != null) d["series_ids"] = it.SeriesIds;
            if (it.Season != null) d["season"] = it.Season;
            if (it.Episode != null) d["episode"] = it.Episode;
            if (it.SeriesTitle != null) d["series_title"] = it.SeriesTitle;
            if (it.Year != null) d["year"] = it.Year;
            if (it.LastPlayedAt != null) d["last_played_at"] = it.LastPlayedAt.Value.ToUniversalTime().ToString("o");
            if (it.IsFavorite != null) d["is_favorite"] = it.IsFavorite;
            if (it.RatingSet) d["rating"] = it.Rating;
            return d;
        }
    }

    /// <summary>Credenciales y datos del servidor para las llamadas de sync.</summary>
    public class SyncContext
    {
        public string Token { get; set; }
        public string EmbyUserId { get; set; }
        public string EmbyUserName { get; set; }
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public string ServerVersion { get; set; }

        public static SyncContext From(UserLink link)
        {
            return new SyncContext { Token = link.Token, EmbyUserId = link.EmbyUserId, EmbyUserName = link.EmbyUserName };
        }
    }
}
