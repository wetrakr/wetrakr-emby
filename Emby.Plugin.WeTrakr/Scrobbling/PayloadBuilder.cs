using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>Arma el payload del webhook (mismo contrato que el plugin de Jellyfin).</summary>
    public static class PayloadBuilder
    {
        public static bool IsSupported(BaseItem item)
        {
            return item is Movie || item is Episode;
        }

        public static ScrobblePayload Build(string eventName, User user, BaseItem item, long positionTicks, bool isPaused, bool played)
        {
            var p = new ScrobblePayload
            {
                Event = eventName,
                UserId = PluginConfiguration.NormalizeId(user.Id.ToString("N")),
                UserName = user.Name,
                IsOwner = true,
                ItemId = item.Id.ToString("N"),
                ItemType = item is Episode ? "Episode" : "Movie",
                Name = item.Name,
                ProviderIds = ItemIds.Payload(item),
                PositionTicks = positionTicks,
                RuntimeTicks = item.RunTimeTicks ?? 0,
                IsPaused = isPaused,
                Played = played
            };

            if (item is Episode ep)
            {
                var series = ep.Series;
                p.SeriesName = series?.Name ?? ep.SeriesName;
                p.SeriesProviderIds = series != null ? ItemIds.Payload(series) : new ProviderIdsPayload();
                p.Season = ep.ParentIndexNumber;
                p.Episode = ep.IndexNumber;
            }
            return p;
        }
    }
}
