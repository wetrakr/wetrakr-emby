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
    /// Pasada COMPLETA de sync con WeTrakr (las dos direcciones, sin atajos): la
    /// reconciliacion diaria, en hora tranquila. El dia a dia lo lleva
    /// SyncScheduler con comprobaciones baratas; esto es la red de seguridad.
    /// Se puede lanzar a mano desde Tareas programadas o desde la pagina del plugin.
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

        public string Description => "Full daily reconciliation with WeTrakr: watched history, progress, ratings, favorites and the Planning playlist, both ways. Changes during the day are synced as they happen.";

        public string Category => "WeTrakr";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return _engine.RunAll(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Entre las 03:00 y las 05:00 locales, con minuto aleatorio por servidor
            // para que no pregunten todos a la vez. Emby guarda el disparador la
            // primera vez, asi que el minuto se queda fijo para ese servidor.
            var minute = new Random().Next(0, 120);
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(minute)).Ticks }
            };
        }
    }
}
