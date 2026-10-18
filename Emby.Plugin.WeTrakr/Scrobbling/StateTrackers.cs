using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Emby no tiene eventos de pausa: se deducen de la transicion de IsPaused en
    /// PlaybackProgress, por sesion de reproduccion.
    /// </summary>
    public class PauseStateTracker
    {
        private readonly ConcurrentDictionary<string, bool> _paused = new ConcurrentDictionary<string, bool>();

        /// <summary>Devuelve "PlaybackPause"/"PlaybackUnpause" si cambio, o null.</summary>
        public string Transition(string sessionKey, bool isPaused)
        {
            var had = _paused.TryGetValue(sessionKey, out var was);
            _paused[sessionKey] = isPaused;
            if (!had) return isPaused ? "PlaybackPause" : null;
            if (was == isPaused) return null;
            return isPaused ? "PlaybackPause" : "PlaybackUnpause";
        }

        public void Seed(string sessionKey, bool isPaused) => _paused[sessionKey] = isPaused;

        public void Clear(string sessionKey) => _paused.TryRemove(sessionKey, out _);
    }

    /// <summary>
    /// Deja pasar un "marcar visto / no visto" solo si el valor CAMBIA para ese
    /// (usuario, item). Algunos servidores reemiten TogglePlayed de toda la
    /// biblioteca en bucle (otros plugins, escaneos): con Jellyfin llegaron 35.958
    /// eventos en 24 h sobre 351 items. El valor se registra solo cuando la API
    /// acepta el envio (Commit), para no perder el cambio si el POST falla.
    /// </summary>
    public class PlayedStateTracker
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
        private const int SweepAbove = 10000;

        private readonly ConcurrentDictionary<string, Entry> _state = new ConcurrentDictionary<string, Entry>();

        private struct Entry
        {
            public bool Played;
            public DateTime At;
        }

        private static string Key(string userId, long itemId) => userId + ":" + itemId;

        public bool ShouldSend(string userId, long itemId, bool played)
        {
            if (_state.TryGetValue(Key(userId, itemId), out var e) && DateTime.UtcNow - e.At < Ttl)
            {
                return e.Played != played;
            }
            return true;
        }

        public void Commit(string userId, long itemId, bool played)
        {
            _state[Key(userId, itemId)] = new Entry { Played = played, At = DateTime.UtcNow };
            if (_state.Count > SweepAbove)
            {
                var cutoff = DateTime.UtcNow - Ttl;
                foreach (var k in _state.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                {
                    _state.TryRemove(k, out _);
                }
            }
        }
    }
}
