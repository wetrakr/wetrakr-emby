using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Decide CUANDO sincronizar cada cuenta enlazada, gastando las menos
    /// peticiones posibles a WeTrakr:
    ///
    ///   - Emby → WeTrakr no se sondea nunca: el plugin ya se entera de los
    ///     cambios locales por los eventos. Cuando el scrobble deja un aviso
    ///     (SyncSignals), a los LocalDebounce se sube el diff (modo Local; cero
    ///     peticiones si no hay nada que subir).
    ///   - WeTrakr → Emby hay que preguntarlo (WeTrakr no puede llamar a casa del
    ///     usuario), asi que se pregunta cuando importa: al volverse activo un
    ///     usuario en Emby (si la ultima comprobacion tiene mas de ActiveStale),
    ///     cada ActiveInterval mientras siga activo, y en reposo con un latido
    ///     que dobla el intervalo con cada comprobacion vacia, desde MinInterval
    ///     hasta MaxCheckIntervalHours. Una comprobacion es UNA peticion
    ///     (/changes responde `unchanged`).
    ///   - La pasada completa (reconciliacion) la hace WeTrakrSyncTask una vez al
    ///     dia; aqui solo se asegura de que sus disparadores no sean los viejos.
    /// </summary>
    public class SyncScheduler : IServerEntryPoint
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ActiveStale = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ActiveCoalesce = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan LocalDebounce = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StartupJitterMax = TimeSpan.FromMinutes(5);

        private class UserClock
        {
            public DateTime LastCheckAt;
            public DateTime NextCheckAt;
            public TimeSpan Interval = MinInterval;
            public DateTime ActiveUntil;
        }

        private readonly ConcurrentDictionary<string, UserClock> _clocks = new ConcurrentDictionary<string, UserClock>();
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly SyncEngine _engine;
        private readonly ILogger _logger;
        private readonly Random _random = new Random();
        private Timer _timer;
        private int _ticking;

        public static SyncScheduler Instance { get; private set; }

        public SyncScheduler(ILibraryManager library, IUserManager users, IUserDataManager userData, IPlaylistManager playlists,
            ISessionManager sessions, IServerApplicationHost host, ITaskManager tasks, ILogManager logManager)
        {
            _sessions = sessions;
            _tasks = tasks;
            _logger = logManager.GetLogger("WeTrakr");
            _engine = new SyncEngine(library, users, userData, playlists, sessions, host, _logger);
            Instance = this;
        }

        public void Run()
        {
            UpgradeTaskTriggers();

            // Al arrancar: una comprobacion con jitter (que todos los servidores no
            // pregunten a la vez tras una actualizacion), y solo si hace rato de
            // la ultima; si no, el reloj sigue desde donde estaba.
            var now = DateTime.UtcNow;
            foreach (var link in Config.ActiveLinks())
            {
                var clock = ClockFor(link.EmbyUserId);
                var last = link.LastSyncAt ?? DateTime.MinValue;
                clock.LastCheckAt = last;
                clock.NextCheckAt = now - last >= MinInterval
                    ? now + TimeSpan.FromSeconds(_random.Next(30, (int)StartupJitterMax.TotalSeconds))
                    : last + MinInterval;
            }

            _sessions.SessionStarted += OnSessionActivity;
            _sessions.SessionActivity += OnSessionActivity;
            _sessions.PlaybackStart += OnPlaybackStart;
            _timer = new Timer(OnTick, null, Tick, Tick);
            _logger.Info("WeTrakr sync scheduler started");
        }

        public void Dispose()
        {
            _sessions.SessionStarted -= OnSessionActivity;
            _sessions.SessionActivity -= OnSessionActivity;
            _sessions.PlaybackStart -= OnPlaybackStart;
            _timer?.Dispose();
            _timer = null;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        private UserClock ClockFor(string userId)
        {
            return _clocks.GetOrAdd(PluginConfiguration.NormalizeId(userId), _ => new UserClock());
        }

        /// <summary>Proxima comprobacion prevista de un usuario (para la pagina del plugin).</summary>
        public DateTime? NextCheckFor(string userId)
        {
            return _clocks.TryGetValue(PluginConfiguration.NormalizeId(userId), out var c) ? c.NextCheckAt : (DateTime?)null;
        }

        /// <summary>Un usuario enlazado acaba de empezar a usar Emby, o lo esta usando.</summary>
        public void NoteActive(string userId)
        {
            var id = PluginConfiguration.NormalizeId(userId);
            if (string.IsNullOrEmpty(id) || Config.FindLink(id) == null) return;
            var clock = ClockFor(id);
            var now = DateTime.UtcNow;
            clock.ActiveUntil = now + ActiveWindow;
            // Al volverse activo con una comprobacion vieja: pronto, pero no ya,
            // para juntar los avisos de una misma sesion (login + navegacion).
            if (now - clock.LastCheckAt >= ActiveStale && clock.NextCheckAt > now + ActiveCoalesce)
            {
                clock.NextCheckAt = now + ActiveCoalesce;
            }
        }

        private void OnSessionActivity(object sender, SessionEventArgs e)
        {
            try
            {
                var userId = e?.SessionInfo?.UserId;
                if (!string.IsNullOrEmpty(userId)) NoteActive(userId);
            }
            catch (Exception ex)
            {
                _logger.Debug("WeTrakr: session activity ignored: {0}", ex.Message);
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                foreach (var u in e.Users ?? Enumerable.Empty<MediaBrowser.Controller.Entities.User>().ToList())
                {
                    NoteActive(u.Id.ToString("N"));
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("WeTrakr: playback start ignored: {0}", ex.Message);
            }
        }

        private void OnTick(object state)
        {
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            Task.Run(async () =>
            {
                try { await TickAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.ErrorException("WeTrakr: scheduler tick failed", ex); }
                finally { Interlocked.Exchange(ref _ticking, 0); }
            });
        }

        private async Task TickAsync()
        {
            var cfg = Config;
            if (!cfg.SyncEnabled) return;
            var maxInterval = TimeSpan.FromHours(Math.Max(1, cfg.MaxCheckIntervalHours));

            foreach (var link in cfg.ActiveLinks().ToList())
            {
                var now = DateTime.UtcNow;
                var clock = ClockFor(link.EmbyUserId);

                // Cambios locales pendientes: subida (y playlist) sin preguntar nada.
                var dirtySince = SyncSignals.DirtySince(link.EmbyUserId);
                if (dirtySince != null && now - dirtySince.Value >= LocalDebounce)
                {
                    var seen = dirtySince.Value;
                    var local = await _engine.Run(link, SyncEngine.Mode.Local, CancellationToken.None).ConfigureAwait(false);
                    if (local != null) SyncSignals.ClearDirty(link.EmbyUserId, seen);
                }

                if (clock.NextCheckAt == default(DateTime)) clock.NextCheckAt = now;
                if (now < clock.NextCheckAt) continue;

                var result = await _engine.Run(link, SyncEngine.Mode.Check, CancellationToken.None).ConfigureAwait(false);
                if (result == null)
                {
                    // Habia otro sync en marcha: se vuelve a intentar en el siguiente tick.
                    clock.NextCheckAt = now + Tick;
                    continue;
                }

                clock.LastCheckAt = now;
                if (result.Error != null)
                {
                    // Error de red o de la API: ni se castiga ni se insiste.
                    clock.Interval = TimeSpan.FromTicks(Math.Min(Math.Max(clock.Interval.Ticks, MinInterval.Ticks) * 2, maxInterval.Ticks));
                }
                else if (result.Changed)
                {
                    clock.Interval = MinInterval;
                }
                else
                {
                    clock.Interval = TimeSpan.FromTicks(Math.Min(clock.Interval.Ticks * 2, maxInterval.Ticks));
                }

                var active = clock.ActiveUntil > now;
                var wait = active && clock.Interval > ActiveInterval ? ActiveInterval : clock.Interval;
                clock.NextCheckAt = now + wait;
            }
        }

        /// <summary>
        /// La v0.1 registraba la tarea con un disparador de intervalo (15 min) y
        /// Emby guarda los disparadores de cada tarea la primera vez que la ve:
        /// cambiar GetDefaultTriggers no toca los ya guardados. Si la tarea sigue
        /// con un disparador de intervalo, se le ponen los nuevos (una vez al dia).
        /// </summary>
        private void UpgradeTaskTriggers()
        {
            try
            {
                var worker = _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is WeTrakrSyncTask);
                if (worker == null) return;
                var triggers = worker.Triggers ?? Array.Empty<TaskTriggerInfo>();
                if (!triggers.Any(t => t.Type == TaskTriggerInfo.TriggerInterval)) return;
                worker.Triggers = worker.ScheduledTask.GetDefaultTriggers().ToArray();
                _logger.Info("WeTrakr: sync task moved from a 15-minute interval to a daily run");
            }
            catch (Exception ex)
            {
                _logger.Warn("WeTrakr: could not update the sync task triggers: {0}", ex.Message);
            }
        }
    }
}
