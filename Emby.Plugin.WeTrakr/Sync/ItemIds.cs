using System;
using System.Collections.Generic;
using Emby.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Entities;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>Lectura de los ids externos (TMDB / IMDb / TVDB) de un item de Emby.</summary>
    public static class ItemIds
    {
        public static string Get(BaseItem item, string provider)
        {
            if (item?.ProviderIds == null) return null;
            foreach (KeyValuePair<string, string> kv in item.ProviderIds)
            {
                if (string.Equals(kv.Key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    return kv.Value.Trim();
                }
            }
            return null;
        }

        public static ExtIds Ext(BaseItem item)
        {
            var ids = new ExtIds();
            if (item == null) return ids;
            if (long.TryParse(Get(item, "Tmdb"), out var tmdb) && tmdb > 0) ids.Tmdb = tmdb;
            var imdb = Get(item, "Imdb");
            if (!string.IsNullOrEmpty(imdb) && imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) ids.Imdb = imdb.ToLowerInvariant();
            if (long.TryParse(Get(item, "Tvdb"), out var tvdb) && tvdb > 0) ids.Tvdb = tvdb;
            return ids;
        }

        public static ProviderIdsPayload Payload(BaseItem item)
        {
            return new ProviderIdsPayload
            {
                Imdb = Get(item, "Imdb") ?? string.Empty,
                Tmdb = Get(item, "Tmdb") ?? string.Empty,
                Tvdb = Get(item, "Tvdb") ?? string.Empty
            };
        }
    }
}
