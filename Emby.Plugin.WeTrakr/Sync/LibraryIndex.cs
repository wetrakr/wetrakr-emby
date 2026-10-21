using System.Collections.Generic;
using System.Linq;
using Emby.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Indice de la biblioteca de UN usuario de Emby por ids externos, para
    /// encontrar el item local de lo que llega de WeTrakr. Se construye en cada
    /// pasada (una consulta) con lo que ese usuario puede ver.
    /// </summary>
    public class LibraryIndex
    {
        private readonly Dictionary<string, List<BaseItem>> _movies = new Dictionary<string, List<BaseItem>>();
        private readonly Dictionary<string, List<BaseItem>> _series = new Dictionary<string, List<BaseItem>>();
        private readonly Dictionary<string, List<BaseItem>> _episodesById = new Dictionary<string, List<BaseItem>>();
        private readonly Dictionary<string, List<BaseItem>> _episodesBySe = new Dictionary<string, List<BaseItem>>();
        private readonly Dictionary<long, List<BaseItem>> _episodesBySeries = new Dictionary<long, List<BaseItem>>();
        private readonly Dictionary<long, BaseItem> _byId = new Dictionary<long, BaseItem>();

        public int Count => _byId.Count;

        public static LibraryIndex Build(ILibraryManager library, User user)
        {
            var idx = new LibraryIndex();
            var items = LibraryFilter.Current(library).Apply(library.GetItemList(new InternalItemsQuery
            {
                User = user,
                IncludeItemTypes = new[] { "Movie", "Series", "Episode" },
                Recursive = true,
                IsVirtualItem = false
            }));

            foreach (var item in items)
            {
                idx._byId[item.InternalId] = item;
                var ids = ItemIds.Ext(item);
                if (item is Movie) AddIds(idx._movies, ids, item);
                else if (item is Series) AddIds(idx._series, ids, item);
            }
            foreach (var item in items.OfType<Episode>())
            {
                AddIds(idx._episodesById, ItemIds.Ext(item), item);
                var seriesId = item.SeriesId;
                if (seriesId <= 0) continue;
                if (!idx._episodesBySeries.TryGetValue(seriesId, out var list)) idx._episodesBySeries[seriesId] = list = new List<BaseItem>();
                list.Add(item);
                if (item.ParentIndexNumber != null && item.IndexNumber != null)
                {
                    Add(idx._episodesBySe, seriesId + ":" + item.ParentIndexNumber + ":" + item.IndexNumber, item);
                }
            }
            return idx;
        }

        private static void Add(Dictionary<string, List<BaseItem>> map, string key, BaseItem item)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<BaseItem>();
            if (!list.Contains(item)) list.Add(item);
        }

        private static void AddIds(Dictionary<string, List<BaseItem>> map, ExtIds ids, BaseItem item)
        {
            if (ids.Tmdb != null) Add(map, "tmdb:" + ids.Tmdb, item);
            if (!string.IsNullOrEmpty(ids.Imdb)) Add(map, "imdb:" + ids.Imdb, item);
            if (ids.Tvdb != null) Add(map, "tvdb:" + ids.Tvdb, item);
        }

        private static List<BaseItem> Lookup(Dictionary<string, List<BaseItem>> map, ExtIds ids)
        {
            if (ids == null) return new List<BaseItem>();
            // TMDB primero (como la API), luego IMDb y TVDB.
            if (ids.Tmdb != null && map.TryGetValue("tmdb:" + ids.Tmdb, out var a)) return a;
            if (!string.IsNullOrEmpty(ids.Imdb) && map.TryGetValue("imdb:" + ids.Imdb.ToLowerInvariant(), out var b)) return b;
            if (ids.Tvdb != null && map.TryGetValue("tvdb:" + ids.Tvdb, out var c)) return c;
            return new List<BaseItem>();
        }

        public BaseItem GetById(long id) => _byId.TryGetValue(id, out var it) ? it : null;

        /// <summary>Items locales de un titulo de WeTrakr (puede haber varias versiones).</summary>
        public List<BaseItem> Find(string type, ExtIds ids, ExtIds seriesIds, int? season, int? episode)
        {
            switch (type)
            {
                case "movie":
                    return Lookup(_movies, ids);
                case "show":
                    return Lookup(_series, ids);
                case "episode":
                    // Por la serie + numero primero: el id de episodio de TMDB y el
                    // de TVDB numeran distinto el anime, y la API ya resolvio S/E.
                    var result = new List<BaseItem>();
                    if (season != null && episode != null)
                    {
                        foreach (var s in Lookup(_series, seriesIds))
                        {
                            if (_episodesBySe.TryGetValue(s.InternalId + ":" + season + ":" + episode, out var eps)) result.AddRange(eps);
                        }
                    }
                    if (result.Count == 0) result.AddRange(Lookup(_episodesById, ids));
                    return result;
            }
            return new List<BaseItem>();
        }

        public List<BaseItem> EpisodesOf(long seriesInternalId)
        {
            return _episodesBySeries.TryGetValue(seriesInternalId, out var list) ? list : new List<BaseItem>();
        }

        /// <summary>Clave canonica de un titulo de WeTrakr para el set de Planning.</summary>
        public static string TitleKey(string type, ExtIds ids)
        {
            if (ids == null) return null;
            if (ids.Tmdb != null) return type + ":tmdb:" + ids.Tmdb;
            if (!string.IsNullOrEmpty(ids.Imdb)) return type + ":imdb:" + ids.Imdb.ToLowerInvariant();
            if (ids.Tvdb != null) return type + ":tvdb:" + ids.Tvdb;
            return null;
        }

        public static ExtIds ParseTitleKey(string key, out string type)
        {
            type = null;
            var parts = key?.Split(':');
            if (parts == null || parts.Length != 3) return null;
            type = parts[0];
            var ids = new ExtIds();
            if (parts[1] == "tmdb" && long.TryParse(parts[2], out var t)) ids.Tmdb = t;
            else if (parts[1] == "imdb") ids.Imdb = parts[2];
            else if (parts[1] == "tvdb" && long.TryParse(parts[2], out var v)) ids.Tvdb = v;
            return ids;
        }
    }
}
