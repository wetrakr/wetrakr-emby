using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Bibliotecas excluidas en la configuracion: lo que cuelga de ellas no se
    /// scrobblea ni entra en el sync (ni sube, ni se le aplica nada, ni va a la
    /// playlist). Se resuelve una vez por configuracion guardada y se cachea.
    /// </summary>
    public class LibraryFilter
    {
        private static readonly string[] IgnoredCollectionTypes =
            { "music", "musicvideos", "photos", "books", "audiobooks", "games", "playlists", "boxsets", "livetv" };

        private static readonly object CacheLock = new object();
        private static string[] _cachedFor;
        private static LibraryFilter _cached;

        private readonly ILibraryManager _library;
        private readonly HashSet<long> _excluded;

        private LibraryFilter(ILibraryManager library, HashSet<long> excluded)
        {
            _library = library;
            _excluded = excluded;
        }

        public bool IsEmpty => _excluded.Count == 0;

        /// <summary>Filtro de la configuracion actual.</summary>
        public static LibraryFilter Current(ILibraryManager library)
        {
            var ids = Plugin.Instance?.Configuration?.ExcludedLibraryIds ?? Array.Empty<string>();
            lock (CacheLock)
            {
                if (_cached != null && ReferenceEquals(_cachedFor, ids)) return _cached;
            }
            var filter = new LibraryFilter(library, Resolve(library, ids));
            lock (CacheLock)
            {
                _cachedFor = ids;
                _cached = filter;
            }
            return filter;
        }

        private static HashSet<long> Resolve(ILibraryManager library, string[] ids)
        {
            var set = new HashSet<long>();
            foreach (var raw in ids)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    BaseItem folder = null;
                    if (long.TryParse(raw, out var internalId)) folder = library.GetItemById(internalId);
                    else if (Guid.TryParse(raw, out var guid)) folder = library.GetItemById(guid);
                    if (folder != null) set.Add(folder.InternalId);
                }
                catch { /* una biblioteca borrada: se ignora la entrada */ }
            }
            return set;
        }

        /// <summary>Bibliotecas que tiene sentido excluir (pelis, series, mixtas), para la pagina.</summary>
        public static List<VirtualFolderInfo> SelectableLibraries(ILibraryManager library)
        {
            try
            {
                return (library.GetVirtualFolders() ?? new List<VirtualFolderInfo>())
                    .Where(v => v != null && !string.IsNullOrEmpty(v.ItemId))
                    .Where(v => string.IsNullOrEmpty(v.CollectionType) || !IgnoredCollectionTypes.Contains(v.CollectionType.ToLowerInvariant()))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<VirtualFolderInfo>();
            }
        }

        /// <summary>
        /// Un item esta excluido si alguna de sus bibliotecas lo esta. OJO: en Emby
        /// la cadena de padres de un item va por la carpeta fisica hasta la raiz y
        /// NO pasa por la biblioteca virtual (GetTopParent da la carpeta fisica);
        /// la biblioteca se obtiene con GetCollectionFolders, como hace Emby.
        /// </summary>
        public bool IsExcluded(BaseItem item)
        {
            if (IsEmpty || item == null) return false;
            try
            {
                var folders = _library.GetCollectionFolders(item);
                if (folders == null) return false;
                foreach (var f in folders)
                {
                    if (f != null && _excluded.Contains(f.InternalId)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public List<BaseItem> Apply(IEnumerable<BaseItem> items)
        {
            return IsEmpty ? items.ToList() : items.Where(i => !IsExcluded(i)).ToList();
        }
    }
}
