using System;
using System.Collections.Concurrent;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Avisos de "en Emby cambio algo de este usuario" para el planificador del
    /// sync. Los deja el scrobble (un envio que fallo, una marca local que afecta
    /// a la playlist) y los recoge SyncScheduler en su siguiente tick. Estatico
    /// para no depender del orden en que Emby construye los entry points.
    /// </summary>
    public static class SyncSignals
    {
        private static readonly ConcurrentDictionary<string, DateTime> Dirty = new ConcurrentDictionary<string, DateTime>();

        /// <summary>Marca al usuario (id normalizado) como pendiente de subir.</summary>
        public static void MarkDirty(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            Dirty[userId] = DateTime.UtcNow;
        }

        /// <summary>Ultimo aviso del usuario, o null si no hay nada pendiente.</summary>
        public static DateTime? DirtySince(string userId)
        {
            return Dirty.TryGetValue(userId, out var at) ? at : (DateTime?)null;
        }

        /// <summary>Quita el aviso si no ha llegado otro despues de `seen`.</summary>
        public static void ClearDirty(string userId, DateTime seen)
        {
            if (Dirty.TryGetValue(userId, out var at) && at <= seen) Dirty.TryRemove(userId, out _);
        }
    }
}
