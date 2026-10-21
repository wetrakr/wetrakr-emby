using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Sync;
using Emby.Plugin.WeTrakr.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Rest
{
    // Endpoints de la pagina de configuracion. Solo administradores.
    // OJO: el ServiceStack de Emby no ejecuta handlers que devuelven Task; todos
    // son sincronos y el sondeo del device code es de una peticion por llamada.

    [Route("/WeTrakr/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetWeTrakrStatus : IReturn<object> { }

    [Route("/WeTrakr/Connect/Start", "POST")]
    [Authenticated(Roles = "Admin")]
    public class StartWeTrakrConnect : IReturn<object>
    {
        public string UserId { get; set; }
    }

    [Route("/WeTrakr/Connect/Poll", "POST")]
    [Authenticated(Roles = "Admin")]
    public class PollWeTrakrConnect : IReturn<object>
    {
        public string UserId { get; set; }
    }

    [Route("/WeTrakr/Connect/Cancel", "POST")]
    [Authenticated(Roles = "Admin")]
    public class CancelWeTrakrConnect : IReturn<object>
    {
        public string UserId { get; set; }
    }

    [Route("/WeTrakr/Disconnect", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DisconnectWeTrakr : IReturn<object>
    {
        public string UserId { get; set; }
    }

    [Route("/WeTrakr/Settings", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SaveWeTrakrSettings : IReturn<object>
    {
        public bool ScrobblePlaying { get; set; }
        public bool ScrobbleWatched { get; set; }
        public bool ScrobbleUnwatched { get; set; }
        public bool ScrobbleRatings { get; set; }
        public bool SyncToWeTrakr { get; set; }
        public bool SyncFromWeTrakr { get; set; }
        public bool WatchlistPlaylist { get; set; }
        public string WatchlistPlaylistName { get; set; }
        public string[] ExcludedLibraryIds { get; set; }
    }

    [Route("/WeTrakr/Sync/Run", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RunWeTrakrSync : IReturn<object> { }

    [Route("/WeTrakr/Sync/Reset", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ResetWeTrakrSync : IReturn<object>
    {
        public string UserId { get; set; }
    }

    public class WeTrakrService : IService
    {
        private class Pending
        {
            public string DeviceCode;
            public string UserCode;
            public string VerificationUrl;
            public DateTime ExpiresAt;
        }

        // Emparejamientos en curso por usuario de Emby. En memoria: si Emby se
        // reinicia a mitad, se pide otro codigo.
        private static readonly ConcurrentDictionary<string, Pending> PendingByUser = new ConcurrentDictionary<string, Pending>();

        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly ITaskManager _tasks;
        private readonly ILogger _logger;

        public WeTrakrService(IUserManager users, ILibraryManager library, ITaskManager tasks, ILogManager logManager)
        {
            _users = users;
            _library = library;
            _tasks = tasks;
            _logger = logManager.GetLogger("WeTrakr");
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        public object Get(GetWeTrakrStatus request)
        {
            var cfg = Config;
            var users = _users.GetUserList(new UserQuery())
                .OrderBy(u => u.Name)
                .Select(u =>
                {
                    var id = PluginConfiguration.NormalizeId(u.Id.ToString("N"));
                    var link = cfg.FindLink(id);
                    PendingByUser.TryGetValue(id, out var pending);
                    if (pending != null && pending.ExpiresAt < DateTime.UtcNow) pending = null;
                    return new
                    {
                        UserId = id,
                        UserName = u.Name,
                        Linked = link != null && !string.IsNullOrEmpty(link.Token),
                        Invalid = link?.Invalid ?? false,
                        WeTrakrUsername = link?.WeTrakrUsername,
                        LinkedAt = link?.LinkedAt,
                        LastScrobbleAt = link?.LastScrobbleAt,
                        LastSyncAt = link?.LastSyncAt,
                        LastSyncResult = link?.LastSyncResult,
                        NextCheckAt = link != null ? SyncScheduler.Instance?.NextCheckFor(id) : null,
                        PendingCode = pending?.UserCode,
                        PendingUrl = pending?.VerificationUrl
                    };
                })
                .ToArray();

            var excluded = new HashSet<string>(cfg.ExcludedLibraryIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var libraries = LibraryFilter.SelectableLibraries(_library)
                .Select(v => new { Id = v.ItemId, v.Name, v.CollectionType, Excluded = excluded.Contains(v.ItemId) })
                .ToArray();

            return new
            {
                Version = Plugin.Instance.PluginVersion,
                Users = users,
                Libraries = libraries,
                cfg.ScrobblePlaying,
                cfg.ScrobbleWatched,
                cfg.ScrobbleUnwatched,
                cfg.ScrobbleRatings,
                SyncToWeTrakr = cfg.UploadEnabled,
                SyncFromWeTrakr = cfg.PullEnabled,
                cfg.WatchlistPlaylist,
                cfg.WatchlistPlaylistName
            };
        }

        public object Post(StartWeTrakrConnect request)
        {
            var id = PluginConfiguration.NormalizeId(request.UserId);
            var user = _users.GetUserById(id);
            if (user == null) return new { Ok = false, Error = "Emby user not found" };

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
                _logger.ErrorException("WeTrakr: could not start pairing", ex);
                return new { Ok = false, Error = "Could not reach WeTrakr: " + ex.Message };
            }
        }

        public object Post(PollWeTrakrConnect request)
        {
            var id = PluginConfiguration.NormalizeId(request.UserId);
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
                var user = _users.GetUserById(id);
                var cfg = Config;

                // Una cuenta de WeTrakr tiene un solo token de Emby y cada
                // emparejamiento lo renueva: el enlace de otra cuenta de Emby a la
                // misma cuenta de WeTrakr queda muerto y se quita.
                var stale = cfg.UserLinks.Where(l => l.EmbyUserId != id && string.Equals(l.WeTrakrUsername, token.Username, StringComparison.OrdinalIgnoreCase)).Select(l => l.EmbyUserName).ToArray();
                foreach (var l in cfg.UserLinks.Where(l => l.EmbyUserId != id && string.Equals(l.WeTrakrUsername, token.Username, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    cfg.Remove(l.EmbyUserId);
                }

                cfg.Upsert(new UserLink
                {
                    EmbyUserId = id,
                    EmbyUserName = user?.Name,
                    Token = token.AccessToken,
                    WeTrakrUsername = token.Username,
                    LinkedAt = DateTime.UtcNow
                });
                Plugin.Instance.SaveConfiguration();
                // Un enlace nuevo empieza el sync de cero (otra cuenta de WeTrakr).
                SyncStateStore.Reset(id);
                _tasks.QueueScheduledTask<WeTrakrSyncTask>();

                return new { Status = "linked", Username = token.Username, StaleLinksRemoved = stale };
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

        public object Post(CancelWeTrakrConnect request)
        {
            PendingByUser.TryRemove(PluginConfiguration.NormalizeId(request.UserId), out _);
            return new { Ok = true };
        }

        public object Post(DisconnectWeTrakr request)
        {
            var id = PluginConfiguration.NormalizeId(request.UserId);
            Config.Remove(id);
            Plugin.Instance.SaveConfiguration();
            SyncStateStore.Reset(id);
            return new { Ok = true };
        }

        public object Post(SaveWeTrakrSettings request)
        {
            var cfg = Config;
            cfg.ScrobblePlaying = request.ScrobblePlaying;
            cfg.ScrobbleWatched = request.ScrobbleWatched;
            cfg.ScrobbleUnwatched = request.ScrobbleUnwatched;
            cfg.ScrobbleRatings = request.ScrobbleRatings;
            cfg.SyncToWeTrakr = request.SyncToWeTrakr;
            cfg.SyncFromWeTrakr = request.SyncFromWeTrakr;
            cfg.SyncEnabled = request.SyncToWeTrakr || request.SyncFromWeTrakr;
            cfg.ExcludedLibraryIds = (request.ExcludedLibraryIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToArray();
            cfg.WatchlistPlaylist = request.WatchlistPlaylist;
            if (!string.IsNullOrWhiteSpace(request.WatchlistPlaylistName)) cfg.WatchlistPlaylistName = request.WatchlistPlaylistName.Trim();
            Plugin.Instance.SaveConfiguration();
            return new { Ok = true };
        }

        public object Post(RunWeTrakrSync request)
        {
            _tasks.QueueScheduledTask<WeTrakrSyncTask>();
            return new { Ok = true };
        }

        public object Post(ResetWeTrakrSync request)
        {
            SyncStateStore.Reset(PluginConfiguration.NormalizeId(request.UserId));
            _tasks.QueueScheduledTask<WeTrakrSyncTask>();
            return new { Ok = true };
        }
    }
}
