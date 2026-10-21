using System;
using System.Collections.Generic;
using System.Linq;
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
                .Select(LinkFlow.Status)
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
            if (_users.GetUserById(id) == null) return new { Ok = false, Error = "Emby user not found" };
            return LinkFlow.Start(id, _logger);
        }

        public object Post(PollWeTrakrConnect request)
        {
            var user = _users.GetUserById(PluginConfiguration.NormalizeId(request.UserId));
            if (user == null) return new { Status = "none" };
            return LinkFlow.Poll(user, _tasks);
        }

        public object Post(CancelWeTrakrConnect request)
        {
            LinkFlow.Cancel(request.UserId);
            return new { Ok = true };
        }

        public object Post(DisconnectWeTrakr request)
        {
            LinkFlow.Disconnect(request.UserId);
            return new { Ok = true };
        }

        public object Post(SaveWeTrakrSettings request)
        {
            var cfg = Config;
            cfg.ScrobblePlaying = request.ScrobblePlaying;
            cfg.ScrobbleWatched = request.ScrobbleWatched;
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
            LinkFlow.Resync(request.UserId, _tasks);
            return new { Ok = true };
        }
    }
}
