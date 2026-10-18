using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Ultimo estado sincronizado de cada item, por usuario de Emby. Es lo que
    /// permite subir solo lo que cambio en Emby y no confundir con un cambio local
    /// lo que acaba de escribir el propio sync.
    /// </summary>
    public class ItemState
    {
        [JsonPropertyName("p")] public bool Played { get; set; }
        [JsonPropertyName("lp")] public long LastPlayedTicks { get; set; }
        [JsonPropertyName("pc")] public int PlayCount { get; set; }
        [JsonPropertyName("f")] public bool Favorite { get; set; }
        [JsonPropertyName("r")] public double? Rating { get; set; }
        [JsonPropertyName("pos")] public long Position { get; set; }

        /// <summary>El punto de reanudacion lo escribio el sync (viene de WeTrakr).</summary>
        [JsonPropertyName("wpos")] public bool PositionFromWeTrakr { get; set; }
    }

    public class UserSyncState
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        /// <summary>Primer import completo Emby → WeTrakr hecho.</summary>
        [JsonPropertyName("initial_done")] public bool InitialDone { get; set; }

        /// <summary>Snapshot WeTrakr → Emby hecho; desde entonces se lee `changes`.</summary>
        [JsonPropertyName("snapshot_done")] public bool SnapshotDone { get; set; }

        [JsonPropertyName("cursor_since")] public DateTime? CursorSince { get; set; }

        /// <summary>Ultima pasada de subida: el diff lee lo que cambio en Emby desde aqui.</summary>
        [JsonPropertyName("last_scan_at")] public DateTime? LastScanAt { get; set; }

        [JsonPropertyName("items")] public Dictionary<long, ItemState> Items { get; set; } = new Dictionary<long, ItemState>();

        /// <summary>Titulos en Planning de WeTrakr (clave "movie:tmdb:123").</summary>
        [JsonPropertyName("planning")] public HashSet<string> Planning { get; set; } = new HashSet<string>();

        [JsonPropertyName("playlist_id")] public long? PlaylistId { get; set; }

        /// <summary>Items que el sync metio en la playlist (solo esos se quitan).</summary>
        [JsonPropertyName("playlist_items")] public HashSet<long> PlaylistItems { get; set; } = new HashSet<long>();

        /// <summary>Ajustes de sync de la cuenta en WeTrakr, tal y como llegaron en
        /// la ultima respuesta de /status o /changes. null = aun no se han leido.</summary>
        [JsonPropertyName("settings")] public Api.SyncSettings Settings { get; set; }

        [JsonPropertyName("max_batch")] public int MaxBatch { get; set; } = 1000;

        public ItemState Get(long id)
        {
            if (!Items.TryGetValue(id, out var s))
            {
                s = new ItemState();
                Items[id] = s;
            }
            return s;
        }
    }

    public static class SyncStateStore
    {
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = false };

        private static string PathFor(string embyUserId)
        {
            var dir = Plugin.Instance.DataFolder;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state-" + embyUserId + ".json");
        }

        public static UserSyncState Load(string embyUserId)
        {
            lock (Gate)
            {
                var path = PathFor(embyUserId);
                if (!File.Exists(path)) return new UserSyncState();
                try
                {
                    return JsonSerializer.Deserialize<UserSyncState>(File.ReadAllText(path), Json) ?? new UserSyncState();
                }
                catch
                {
                    // Un estado ilegible equivale a empezar de cero: el import y el
                    // snapshot son idempotentes.
                    return new UserSyncState();
                }
            }
        }

        /// <summary>Escritura atomica: fichero temporal y renombrado.</summary>
        public static void Save(string embyUserId, UserSyncState state)
        {
            lock (Gate)
            {
                var path = PathFor(embyUserId);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public static void Reset(string embyUserId)
        {
            lock (Gate)
            {
                var path = PathFor(embyUserId);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
