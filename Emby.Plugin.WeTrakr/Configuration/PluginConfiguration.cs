using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.WeTrakr.Configuration
{
    /// <summary>
    /// Configuracion persistida (XML en plugins/configurations). El estado del
    /// sync va aparte (Sync/SyncStateStore) para no reescribir este fichero en
    /// cada pasada.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiBaseUrl { get; set; } = "https://api.wetrakr.com";

        /// <summary>Una cuenta de Emby enlazada con una cuenta de WeTrakr.</summary>
        public UserLink[] UserLinks { get; set; } = Array.Empty<UserLink>();

        public bool ScrobblePlaying { get; set; } = true;
        public bool ScrobbleWatched { get; set; } = true;
        public bool ScrobbleRatings { get; set; } = true;

        /// <summary>Sync de biblioteca (las dos direcciones). Lo que se importa o
        /// se devuelve en concreto lo deciden los ajustes de la cuenta en WeTrakr.</summary>
        public bool SyncEnabled { get; set; } = true;

        /// <summary>Crea y mantiene la playlist "WeTrakr Watchlist" con lo de Planning.</summary>
        public bool WatchlistPlaylist { get; set; } = true;

        public string WatchlistPlaylistName { get; set; } = "WeTrakr Watchlist";

        /// <summary>
        /// Tope del intervalo entre comprobaciones de cambios en WeTrakr cuando no
        /// pasa nada (horas). El plugin empieza en 30 min y dobla el intervalo con
        /// cada comprobacion vacia hasta aqui; vuelve a 30 min en cuanto hay
        /// cambios o alguien usa Emby. Ver Sync/SyncScheduler.
        /// </summary>
        public int MaxCheckIntervalHours { get; set; } = 6;

        public UserLink FindLink(string embyUserId)
        {
            var id = NormalizeId(embyUserId);
            return UserLinks?.FirstOrDefault(l => l != null && l.EmbyUserId == id);
        }

        public void Upsert(UserLink link)
        {
            var list = (UserLinks ?? Array.Empty<UserLink>()).Where(l => l != null && l.EmbyUserId != link.EmbyUserId).ToList();
            list.Add(link);
            UserLinks = list.ToArray();
        }

        public void Remove(string embyUserId)
        {
            var id = NormalizeId(embyUserId);
            UserLinks = (UserLinks ?? Array.Empty<UserLink>()).Where(l => l != null && l.EmbyUserId != id).ToArray();
        }

        public IEnumerable<UserLink> ActiveLinks()
        {
            return (UserLinks ?? Array.Empty<UserLink>()).Where(l => l != null && !string.IsNullOrEmpty(l.Token) && !l.Invalid);
        }

        /// <summary>Guid de Emby en formato "N" (32 hex, minusculas, sin guiones).</summary>
        public static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            return Guid.TryParse(id, out var g) ? g.ToString("N") : id.Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    public class UserLink
    {
        public string EmbyUserId { get; set; }
        public string EmbyUserName { get; set; }
        public string Token { get; set; }
        public string WeTrakrUsername { get; set; }
        public DateTime LinkedAt { get; set; }

        /// <summary>La API respondio 401: el token ya no vale (se emparejo otra vez
        /// o se desconecto desde la web). Hay que volver a enlazar.</summary>
        public bool Invalid { get; set; }

        public DateTime? LastScrobbleAt { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string LastSyncResult { get; set; }
    }
}
