using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Sync;
using Emby.Plugin.WeTrakr.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Rest
{
    /// <summary>
    /// Emparejamiento de una cuenta de Emby con WeTrakr (device code), compartido
    /// por la pagina del administrador (cualquier usuario) y la de cada usuario
    /// (solo el suyo). Los emparejamientos en curso van en memoria: si Emby se
    /// reinicia a mitad, se pide otro codigo.
    /// </summary>
    public static class LinkFlow
    {
        private class Pending
        {
            public string DeviceCode;
            public string UserCode;
            public string VerificationUrl;
            public DateTime ExpiresAt;
        }

        private static readonly ConcurrentDictionary<string, Pending> PendingByUser = new ConcurrentDictionary<string, Pending>();

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        /// <summary>Estado de un usuario de Emby para las paginas.</summary>
        public static object Status(User user)
        {
            var cfg = Config;
            var id = PluginConfiguration.NormalizeId(user.Id.ToString("N"));
            var link = cfg.FindLink(id);
            PendingByUser.TryGetValue(id, out var pending);
            if (pending != null && pending.ExpiresAt < DateTime.UtcNow) pending = null;
            return new
            {
                UserId = id,
                UserName = user.Name,
                Linked = link != null && !string.IsNullOrEmpty(link.Token),
                Invalid = link?.Invalid ?? false,
                WeTrakrUsername = link?.WeTrakrUsername,
                LinkedAt = link?.LinkedAt,
                LastScrobbleAt = link?.LastScrobbleAt,
                LastSyncAt = link?.LastSyncAt,
                LastSyncResult = link?.LastSyncResult,
                NextCheckAt = link != null && cfg.PullEnabled ? SyncScheduler.Instance?.NextCheckFor(id) : null,
                PendingCode = pending?.UserCode,
                PendingUrl = pending?.VerificationUrl
            };
        }

        public static object Start(string userId, ILogger logger)
        {
            var id = PluginConfiguration.NormalizeId(userId);
            try
            {
                var code = WeTrakrApi.RequestDeviceCode(CancellationToken.None).GetAwaiter().GetResult();
                var pending = new Pending
                {
                    DeviceCode = code.DeviceCode,
                    UserCode = code.UserCode,
                    VerificationUrl = code.VerificationUrl,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 600)
                };
                PendingByUser[id] = pending;
                return new { Ok = true, UserCode = pending.UserCode, VerificationUrl = pending.VerificationUrl, Interval = code.Interval > 0 ? code.Interval : 5 };
            }
            catch (Exception ex)
            {
                logger.ErrorException("WeTrakr: could not start pairing", ex);
                return new { Ok = false, Error = "Could not reach WeTrakr: " + ex.Message };
            }
        }

        /// <summary>Un sondeo del device code por llamada (los handlers de Emby son sincronos).</summary>
        public static object Poll(User user, ITaskManager tasks)
        {
            var id = PluginConfiguration.NormalizeId(user.Id.ToString("N"));
            if (!PendingByUser.TryGetValue(id, out var pending)) return new { Status = "none" };
            if (pending.ExpiresAt < DateTime.UtcNow)
            {
                PendingByUser.TryRemove(id, out _);
                return new { Status = "expired" };
            }

            try
            {
                var token = WeTrakrApi.PollDeviceToken(pending.DeviceCode, CancellationToken.None).GetAwaiter().GetResult();
                if (token == null || string.IsNullOrEmpty(token.AccessToken)) return new { Status = "pending" };

                PendingByUser.TryRemove(id, out _);
                var cfg = Config;

                // Una cuenta de WeTrakr tiene un solo token de Emby y cada
                // emparejamiento lo renueva: el enlace de otra cuenta de Emby a la
                // misma cuenta de WeTrakr queda muerto y se quita.
                var stale = cfg.UserLinks.Where(l => l.EmbyUserId != id && string.Equals(l.WeTrakrUsername, token.Username, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var l in stale) cfg.Remove(l.EmbyUserId);

                cfg.Upsert(new UserLink
                {
                    EmbyUserId = id,
                    EmbyUserName = user.Name,
                    Token = token.AccessToken,
                    WeTrakrUsername = token.Username,
                    LinkedAt = DateTime.UtcNow
                });
                Plugin.Instance.SaveConfiguration();
                // Un enlace nuevo empieza el sync de cero (otra cuenta de WeTrakr).
                SyncStateStore.Reset(id);
                tasks.QueueScheduledTask<WeTrakrSyncTask>();

                return new { Status = "linked", Username = token.Username, StaleLinksRemoved = stale.Select(l => l.EmbyUserName).ToArray() };
            }
            catch (ApiException ex)
            {
                PendingByUser.TryRemove(id, out _);
                return new { Status = ex.Error == "expired_token" ? "expired" : "denied", Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new { Status = "pending", Error = ex.Message };
            }
        }

        public static void Cancel(string userId)
        {
            PendingByUser.TryRemove(PluginConfiguration.NormalizeId(userId), out _);
        }

        public static void Disconnect(string userId)
        {
            var id = PluginConfiguration.NormalizeId(userId);
            Config.Remove(id);
            Plugin.Instance.SaveConfiguration();
            SyncStateStore.Reset(id);
        }

        public static void Resync(string userId, ITaskManager tasks)
        {
            SyncStateStore.Reset(PluginConfiguration.NormalizeId(userId));
            tasks.QueueScheduledTask<WeTrakrSyncTask>();
        }
    }
}
