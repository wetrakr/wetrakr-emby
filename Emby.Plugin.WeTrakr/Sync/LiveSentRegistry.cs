using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Items cuyo visionado ya llego a WeTrakr por el scrobble en vivo. El diff
    /// periodico no los vuelve a subir como play nuevo (la API lo deduplicaria
    /// por dia, pero un visionado que cruza la medianoche contaria dos veces).
    /// </summary>
    public static class LiveSentRegistry
    {
        private static readonly TimeSpan Window = TimeSpan.FromHours(24);
        private static readonly ConcurrentDictionary<string, DateTime> Sent = new ConcurrentDictionary<string, DateTime>();

        private static string Key(string userId, long itemId) => userId + ":" + itemId;

        public static void MarkPlayed(string userId, long itemId)
        {
            Sent[Key(userId, itemId)] = DateTime.UtcNow;
            if (Sent.Count > 20000)
            {
                var cutoff = DateTime.UtcNow - Window;
                foreach (var k in Sent.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) Sent.TryRemove(k, out _);
            }
        }

        /// <summary>¿Se mando en vivo un visto de este item despues de `since`?</summary>
        public static bool PlayedSince(string userId, long itemId, DateTime since)
        {
            return Sent.TryGetValue(Key(userId, itemId), out var at) && at >= since && DateTime.UtcNow - at < Window;
        }
    }
}
