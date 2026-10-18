using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Tasks
{
    /// <summary>
    /// Sync periodico de la biblioteca con WeTrakr (las dos direcciones). Se puede
    /// lanzar a mano desde Tareas programadas o desde la pagina del plugin.
    /// </summary>
    public class WeTrakrSyncTask : IScheduledTask
    {
        private readonly SyncEngine _engine;

        public WeTrakrSyncTask(ILibraryManager library, IUserManager users, IUserDataManager userData, IPlaylistManager playlists,
            ISessionManager sessions, IServerApplicationHost host, ILogManager logManager)
        {
            _engine = new SyncEngine(library, users, userData, playlists, sessions, host, logManager.GetLogger("WeTrakr"));
        }

        public string Name => "Sync library with WeTrakr";

        public string Key => "WeTrakrSync";

        public string Description => "Sends your watched history, progress, ratings and favorites to WeTrakr and brings back what you watched elsewhere.";

        public string Category => "WeTrakr";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return _engine.RunAll(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var minutes = Math.Max(5, Plugin.Instance?.Configuration?.SyncIntervalMinutes ?? 15);
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks },
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup }
            };
        }
    }
}
