using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Scrobble en vivo: reproduccion (start / pausa / reanudar / stop), marcas
    /// manuales de visto y cambios de nota o favorito, de las cuentas de Emby
    /// enlazadas. No manda el progreso periodico: la API lo descarta y fue lo que
    /// tumbo la API con el plugin de Jellyfin (~2.000 req/min).
    /// </summary>
    public class ScrobbleManager : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;
        private readonly PauseStateTracker _pause = new PauseStateTracker();
        private readonly PlayedStateTracker _played = new PlayedStateTracker();

        public ScrobbleManager(ISessionManager sessionManager, IUserDataManager userDataManager, ILogManager logManager)
        {
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _logger = logManager.GetLogger("WeTrakr");
        }

        public void Run()
        {
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            _logger.Info("WeTrakr scrobbler started");
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }

        private static PluginConfiguration Config => Plugin.Instance?.Configuration;

        private static string SessionKey(PlaybackProgressEventArgs e)
        {
            return e.PlaySessionId ?? e.Session?.Id ?? e.DeviceId ?? string.Empty;
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (Config == null || !Config.ScrobblePlaying || !PayloadBuilder.IsSupported(e.Item)) return;
                _pause.Seed(SessionKey(e), e.IsPaused);
                foreach (var user in e.Users ?? Enumerable.Empty<User>().ToList())
                {
                    Send(user, PayloadBuilder.Build("PlaybackStart", user, e.Item, e.PlaybackPositionTicks ?? 0, e.IsPaused, false));
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr: PlaybackStart failed", ex);
            }
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (Config == null || !Config.ScrobblePlaying || !PayloadBuilder.IsSupported(e.Item)) return;
                var evt = _pause.Transition(SessionKey(e), e.IsPaused);
                if (evt == null) return;
                foreach (var user in e.Users ?? Enumerable.Empty<User>().ToList())
                {
                    Send(user, PayloadBuilder.Build(evt, user, e.Item, e.PlaybackPositionTicks ?? 0, e.IsPaused, false));
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr: PlaybackProgress failed", ex);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                _pause.Clear(SessionKey(e));
                if (Config == null || !Config.ScrobblePlaying || !PayloadBuilder.IsSupported(e.Item)) return;
                foreach (var user in e.Users ?? Enumerable.Empty<User>().ToList())
                {
                    var payload = PayloadBuilder.Build("PlaybackStop", user, e.Item, e.PlaybackPositionTicks ?? 0, false, e.PlayedToCompletion);
                    var userId = payload.UserId;
                    var itemId = e.Item.InternalId;
                    var played = e.PlayedToCompletion;
                    Send(user, payload, () =>
                    {
                        if (played)
                        {
                            LiveSentRegistry.MarkPlayed(userId, itemId);
                            _played.Commit(userId, itemId, true);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr: PlaybackStopped failed", ex);
            }
        }

        /// <summary>
        /// Corre en el hilo de la peticion HTTP de Emby: si lanza, la peticion del
        /// usuario devuelve 500. Todo va en try/catch y el envio es asincrono.
        /// </summary>
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                var cfg = Config;
                if (cfg == null || e.User == null || !PayloadBuilder.IsSupported(e.Item)) return;
                // Import = escrituras del propio sync (y de otros plugins): nunca
                // se reenvian, o el sync se devolveria a si mismo en bucle. La
                // reproduccion ya la cubren los eventos de playback.
                switch (e.SaveReason)
                {
                    case UserDataSaveReason.TogglePlayed:
                        if (!cfg.ScrobbleWatched) return;
                        var userId = PluginConfiguration.NormalizeId(e.User.Id.ToString("N"));
                        var itemId = e.Item.InternalId;
                        var played = e.UserData != null && e.UserData.Played;
                        if (!_played.ShouldSend(userId, itemId, played)) return;
                        var marked = PayloadBuilder.Build("ItemMarkedPlayed", e.User, e.Item, e.UserData?.PlaybackPositionTicks ?? 0, false, played);
                        marked.IsFavorite = e.UserData?.IsFavorite;
                        marked.UserRating = e.UserData?.Rating;
                        marked.SaveReason = "TogglePlayed";
                        Send(e.User, marked, () =>
                        {
                            _played.Commit(userId, itemId, played);
                            if (played) LiveSentRegistry.MarkPlayed(userId, itemId);
                        });
                        break;

                    case UserDataSaveReason.UpdateUserRating:
                        if (!cfg.ScrobbleRatings) return;
                        var rated = PayloadBuilder.Build("UserDataSaved", e.User, e.Item, 0, false, e.UserData != null && e.UserData.Played);
                        rated.IsFavorite = e.UserData?.IsFavorite ?? false;
                        rated.UserRating = e.UserData?.Rating;
                        rated.SaveReason = "UpdateUserRating";
                        Send(e.User, rated);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr: UserDataSaved failed", ex);
            }
        }

        private void Send(User user, ScrobblePayload payload, Action onSuccess = null)
        {
            var link = Config?.FindLink(user.Id.ToString("N"));
            if (link == null || string.IsNullOrEmpty(link.Token) || link.Invalid) return;
            var token = link.Token;

            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await WeTrakrApi.SendScrobble(token, payload, CancellationToken.None).ConfigureAwait(false);
                        link.LastScrobbleAt = DateTime.UtcNow;
                        onSuccess?.Invoke();
                        return;
                    }
                    catch (ApiException ex) when ((int)ex.Status < 500)
                    {
                        _logger.Warn("WeTrakr: {0} rejected ({1}): {2}", payload.Event, (int)ex.Status, ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 1) _logger.Warn("WeTrakr: {0} could not be sent: {1}", payload.Event, ex.Message);
                        else await Task.Delay(2000).ConfigureAwait(false);
                    }
                }
            });
        }
    }
}
