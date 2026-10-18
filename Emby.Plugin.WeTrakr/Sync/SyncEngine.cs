using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Sync
{
    /// <summary>
    /// Una pasada de sync de una cuenta de Emby enlazada:
    ///
    ///   1. Subida (Emby → WeTrakr). La primera vez, todo lo visto, en curso o
    ///      favorito; despues, solo lo que cambio desde la pasada anterior
    ///      comparado con el estado local. Nunca manda "no visto": eso solo lo
    ///      hace el evento en vivo.
    ///   2. Bajada (WeTrakr → Emby). La primera vez el snapshot completo; despues
    ///      los cambios del diario desde el cursor. Se aplican ESTADOS, con
    ///      SaveUserData(..., Import): los listeners del scrobble ignoran Import,
    ///      asi que nada de esto vuelve a WeTrakr.
    ///   3. Puntos de reanudacion y playlist de Planning.
    ///
    /// La subida va antes que la bajada: si no, un cambio hecho en Emby desde la
    /// ultima pasada se veria pisado por el estado viejo de WeTrakr.
    ///
    /// Tres modos, para gastar las menos peticiones posibles (ver SyncScheduler):
    ///   Check  una sola llamada a /changes; si WeTrakr dice `unchanged`, aqui
    ///          acaba. Es lo que corre el sondeo.
    ///   Local  cambio algo en Emby (lo avisan los eventos): sube el diff y
    ///          refresca la playlist. Cero peticiones si el diff esta vacio.
    ///   Full   todo, sin atajos: primer sync, "Sync now" y la pasada diaria.
    /// </summary>
    public class SyncEngine
    {
        public enum Mode { Check, Local, Full }

        private static readonly SemaphoreSlim RunLock = new SemaphoreSlim(1, 1);
        private static readonly string[] SnapshotSections = { "tracks", "ratings", "favorites" };

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly IPlaylistManager _playlists;
        private readonly ISessionManager _sessions;
        private readonly IServerApplicationHost _host;
        private readonly ILogger _logger;

        public SyncEngine(ILibraryManager library, IUserManager users, IUserDataManager userData, IPlaylistManager playlists,
            ISessionManager sessions, IServerApplicationHost host, ILogger logger)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _playlists = playlists;
            _sessions = sessions;
            _host = host;
            _logger = logger;
        }

        public class Result
        {
            public int Uploaded { get; set; }
            public int Applied { get; set; }
            public int Progress { get; set; }
            public bool Unchanged { get; set; }
            public string Error { get; set; }

            /// <summary>Hubo algo que subir o que aplicar (para el backoff del sondeo).</summary>
            public bool Changed => Uploaded > 0 || Applied > 0 || Progress > 0;

            public override string ToString()
            {
                if (Error != null) return Error;
                if (Unchanged && Uploaded == 0) return "nothing changed";
                return string.Format("{0} sent to WeTrakr, {1} updated in Emby, {2} resume points", Uploaded, Applied, Progress);
            }
        }

        /// <summary>Pasada completa de todas las cuentas enlazadas (tarea programada, "Sync now").</summary>
        public async Task RunAll(IProgress<double> progress, CancellationToken ct)
        {
            var cfg = Plugin.Instance.Configuration;
            if (!cfg.SyncEnabled) return;
            if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                _logger.Info("WeTrakr: a sync is already running");
                return;
            }
            try
            {
                var links = cfg.ActiveLinks().ToList();
                for (var i = 0; i < links.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await RunOne(links[i], Mode.Full, ct).ConfigureAwait(false);
                    progress?.Report(100.0 * (i + 1) / links.Count);
                }
                Plugin.Instance.SaveConfiguration();
            }
            finally
            {
                RunLock.Release();
            }
        }

        /// <summary>
        /// Una cuenta, en el modo pedido. Devuelve null si ya habia un sync en
        /// marcha (no espera: el sondeo volvera a pasar).
        /// </summary>
        public async Task<Result> Run(UserLink link, Mode mode, CancellationToken ct)
        {
            if (!Plugin.Instance.Configuration.SyncEnabled) return null;
            if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false)) return null;
            try
            {
                var result = await RunOne(link, mode, ct).ConfigureAwait(false);
                Plugin.Instance.SaveConfiguration();
                return result;
            }
            finally
            {
                RunLock.Release();
            }
        }

        private async Task<Result> RunOne(UserLink link, Mode mode, CancellationToken ct)
        {
            var result = new Result();
            var user = _users.GetUserById(link.EmbyUserId);
            if (user == null)
            {
                result.Error = link.LastSyncResult = "Emby user not found";
                return result;
            }

            var ctx = SyncContext.From(link);
            try
            {
                ctx.ServerId = _host.SystemId;
                ctx.ServerName = _host.FriendlyName;
                ctx.ServerVersion = _host.ApplicationVersion?.ToString();
            }
            catch { /* informativo */ }

            try
            {
                var state = SyncStateStore.Load(link.EmbyUserId);

                // Los ajustes llegan en cada /changes; /status solo hace falta la
                // primera vez, antes del snapshot (y de la primera subida).
                if (state.Settings == null)
                {
                    var status = await WeTrakrApi.GetStatus(ctx, ct).ConfigureAwait(false);
                    RememberSettings(state, status?.Settings, status?.MaxBatch);
                }

                // Subida: siempre en Full y Local (cero peticiones si el diff esta
                // vacio); en Check solo la primera vez, que es la que mas pesa y
                // debe ir antes de que el snapshot pise el estado local.
                if (mode != Mode.Check || !state.InitialDone)
                {
                    result.Uploaded = await Upload(ctx, user, state, Math.Max(1, Math.Min(state.MaxBatch, 1000)), ct).ConfigureAwait(false);
                    SyncStateStore.Save(link.EmbyUserId, state);
                }

                LibraryIndex index = null;
                var sync = state.Settings ?? new SyncSettings();

                if (mode != Mode.Local)
                {
                    index = LibraryIndex.Build(_library, user);
                    var pull = await Pull(ctx, user, state, index, mode == Mode.Full, ct).ConfigureAwait(false);
                    sync = state.Settings ?? sync;
                    result.Unchanged = pull.Unchanged;
                    result.Applied = pull.Applied;
                    // Con `unchanged` la lista de en curso viene vacia y no significa
                    // "ya no hay nada": no se tocan los puntos de reanudacion.
                    if (!pull.Unchanged) result.Progress = ApplyProgress(user, state, index, sync, pull.InProgress);
                }

                // Playlist: cuando pudo cambiar Planning (bajada con cambios) o lo
                // visto en Emby (modo Local: el siguiente episodio sin ver cambia).
                var playlistDue = mode == Mode.Full || mode == Mode.Local || result.Applied > 0;
                if (playlistDue && Plugin.Instance.Configuration.WatchlistPlaylist && sync.PushPlanning)
                {
                    if (index == null) index = LibraryIndex.Build(_library, user);
                    await SyncPlaylist(user, state, index, ct).ConfigureAwait(false);
                }

                SyncStateStore.Save(link.EmbyUserId, state);
                link.LastSyncAt = DateTime.UtcNow;
                link.LastSyncResult = result.ToString();
                if (mode == Mode.Full || result.Changed) _logger.Info("WeTrakr sync for {0} ({1}): {2}", user.Name, mode, result);
                else _logger.Debug("WeTrakr sync for {0} ({1}): {2}", user.Name, mode, result);
            }
            catch (InvalidTokenException)
            {
                link.Invalid = true;
                result.Error = link.LastSyncResult = "WeTrakr rejected the link. Link the account again.";
                _logger.Warn("WeTrakr: token for {0} is no longer valid", user.Name);
            }
            catch (ApiException ex) when (ex.Error == "account_excluded")
            {
                result.Error = link.LastSyncResult = "This Emby account is disabled in WeTrakr (Settings > Emby).";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = link.LastSyncResult = "Sync failed: " + ex.Message;
                _logger.ErrorException("WeTrakr sync for {0} failed", ex, user.Name);
            }
            return result;
        }

        private static void RememberSettings(UserSyncState state, StatusSettings settings, int? maxBatch)
        {
            if (settings?.Sync != null) state.Settings = settings.Sync;
            else if (state.Settings == null) state.Settings = new SyncSettings();
            if (maxBatch != null && maxBatch > 0) state.MaxBatch = maxBatch.Value;
        }

        // =============================================================
        // SUBIDA (Emby → WeTrakr)
        // =============================================================

        private async Task<int> Upload(SyncContext ctx, User user, UserSyncState state, int maxBatch, CancellationToken ct)
        {
            var scanStarted = DateTime.UtcNow;
            var initial = !state.InitialDone;
            var candidates = initial ? ScanInitial(user) : ScanChanged(user, state.LastScanAt ?? DateTime.UtcNow.AddDays(-1));
            var userId = PluginConfiguration.NormalizeId(user.Id.ToString("N"));

            var items = new List<ImportItem>();
            foreach (var item in candidates)
            {
                var data = _userData.GetUserData(user, item);
                if (data == null) continue;
                var it = initial ? InitialItem(item, data) : DiffItem(userId, item, data, state);
                if (it != null) items.Add(it);
            }

            if (items.Count > 0)
            {
                await SendBatches(ctx, items, initial ? "initial" : "incremental", maxBatch, ct).ConfigureAwait(false);
                foreach (var it in items)
                {
                    var item = _library.GetItemById(it.InternalId);
                    var data = item == null ? null : _userData.GetUserData(user, item);
                    if (data != null) Remember(state.Get(it.InternalId), data);
                }
            }

            if (initial)
            {
                // Lo que no se subio tambien cuenta como estado conocido: si no, la
                // primera pasada incremental lo veria como cambio.
                foreach (var item in candidates)
                {
                    if (state.Items.ContainsKey(item.InternalId)) continue;
                    var data = _userData.GetUserData(user, item);
                    if (data != null) Remember(state.Get(item.InternalId), data);
                }
                state.InitialDone = true;
            }
            state.LastScanAt = scanStarted;
            return items.Count;
        }

        /// <summary>Primer import: lo visto, lo favorito y lo que esta a medias.</summary>
        private List<BaseItem> ScanInitial(User user)
        {
            var byId = new Dictionary<long, BaseItem>();
            void Add(InternalItemsQuery q)
            {
                foreach (var it in _library.GetItemList(q)) byId[it.InternalId] = it;
            }
            Add(new InternalItemsQuery { User = user, IncludeItemTypes = new[] { "Movie", "Episode" }, Recursive = true, IsVirtualItem = false, IsPlayed = true });
            Add(new InternalItemsQuery { User = user, IncludeItemTypes = new[] { "Movie", "Episode", "Series" }, Recursive = true, IsVirtualItem = false, IsFavorite = true });
            Add(new InternalItemsQuery { User = user, IncludeItemTypes = new[] { "Movie", "Episode" }, Recursive = true, IsVirtualItem = false, IsResumable = true });
            return byId.Values.ToList();
        }

        /// <summary>Items cuyo UserData cambio desde `since`.</summary>
        private List<BaseItem> ScanChanged(User user, DateTime since)
        {
            try
            {
                return _library.GetItemList(new InternalItemsQuery
                {
                    User = user,
                    IncludeItemTypes = new[] { "Movie", "Episode", "Series" },
                    Recursive = true,
                    IsVirtualItem = false,
                    // Margen por relojes y por escrituras durante la pasada anterior.
                    MinDateLastSavedForUser = new DateTimeOffset(since.AddMinutes(-5), TimeSpan.Zero)
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn("WeTrakr: changed-items query failed ({0}); falling back to a full scan", ex.Message);
                return ScanInitial(user);
            }
        }

        private static ImportItem BaseImportItem(BaseItem item, UserItemData data)
        {
            var it = new ImportItem
            {
                InternalId = item.InternalId,
                Ids = ItemIds.Ext(item),
                Title = item.Name,
                Year = item.ProductionYear,
                Played = data.Played,
                PlayCount = data.PlayCount,
                LastPlayedAt = data.LastPlayedDate?.UtcDateTime,
                PositionTicks = data.PlaybackPositionTicks,
                RuntimeTicks = item.RunTimeTicks ?? 0
            };
            if (item is Movie) it.Type = "movie";
            else if (item is Series) it.Type = "show";
            else if (item is Episode ep)
            {
                it.Type = "episode";
                var series = ep.Series;
                it.SeriesIds = series != null ? ItemIds.Ext(series) : new ExtIds();
                it.SeriesTitle = series?.Name ?? ep.SeriesName;
                it.Season = ep.ParentIndexNumber;
                it.Episode = ep.IndexNumber;
                it.Year = null;
            }
            else return null;

            if (it.Type == "episode" ? (it.SeriesIds.IsEmpty && it.Ids.IsEmpty) : it.Ids.IsEmpty) return null;
            return it;
        }

        private static ImportItem InitialItem(BaseItem item, UserItemData data)
        {
            var it = BaseImportItem(item, data);
            if (it == null) return null;
            it.IsFavorite = data.IsFavorite;
            if (data.Rating != null)
            {
                it.Rating = data.Rating;
                it.RatingSet = true;
            }
            var interesting = data.Played || data.IsFavorite || data.Rating != null || data.PlaybackPositionTicks > 0;
            return interesting ? it : null;
        }

        /// <summary>Lo que cambio respecto al ultimo estado sincronizado, o null.</summary>
        private static ImportItem DiffItem(string userId, BaseItem item, UserItemData data, UserSyncState state)
        {
            var it = BaseImportItem(item, data);
            if (it == null) return null;
            state.Items.TryGetValue(item.InternalId, out var st);
            st = st ?? new ItemState();
            var changed = false;

            var lp = data.LastPlayedDate?.UtcTicks ?? 0;
            if (data.Played && it.Type != "show")
            {
                // Play nuevo: pasa a visto, o se volvio a ver (fecha posterior).
                var replay = st.Played && lp > st.LastPlayedTicks + TimeSpan.FromMinutes(1).Ticks;
                if (!st.Played || replay)
                {
                    var since = st.LastPlayedTicks > 0 ? new DateTime(st.LastPlayedTicks, DateTimeKind.Utc) : DateTime.MinValue;
                    if (!LiveSentRegistry.PlayedSince(userId, item.InternalId, since))
                    {
                        it.NewPlay = true;
                        changed = true;
                    }
                }
            }
            if (data.IsFavorite != st.Favorite)
            {
                it.IsFavorite = data.IsFavorite;
                changed = true;
            }
            if (!SameRating(data.Rating, st.Rating))
            {
                it.Rating = data.Rating;
                it.RatingSet = true;
                changed = true;
            }
            if (!data.Played && it.Type != "show" && data.PlaybackPositionTicks > 0 && data.PlaybackPositionTicks != st.Position)
            {
                changed = true;
            }
            return changed ? it : null;
        }

        private static bool SameRating(double? a, double? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return Math.Abs(a.Value - b.Value) < 0.01;
        }

        private static void Remember(ItemState st, UserItemData data)
        {
            st.Played = data.Played;
            st.LastPlayedTicks = data.LastPlayedDate?.UtcTicks ?? 0;
            st.PlayCount = data.PlayCount;
            st.Favorite = data.IsFavorite;
            st.Rating = data.Rating;
            if (st.Position != data.PlaybackPositionTicks) st.PositionFromWeTrakr = false;
            st.Position = data.PlaybackPositionTicks;
        }

        private async Task SendBatches(SyncContext ctx, List<ImportItem> items, string mode, int maxBatch, CancellationToken ct)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var server = new ServerInfoPayload { Id = ctx.ServerId, Name = ctx.ServerName };
            var batches = items.Select((it, i) => new { it, i }).GroupBy(x => x.i / maxBatch).Select(g => g.Select(x => x.it).ToList()).ToList();

            for (var b = 0; b < batches.Count; b++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new ImportBatch
                {
                    SessionId = sessionId,
                    Mode = mode,
                    Final = b == batches.Count - 1,
                    Server = server,
                    Items = batches[b].Select(WeTrakrApi.ToPayload).ToList()
                };
                var accepted = await WeTrakrApi.PostImport(ctx, batch, ct).ConfigureAwait(false);
                await WaitForBatch(ctx, accepted, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Espera a que la API termine el lote antes de mandar el siguiente.</summary>
        private static async Task WaitForBatch(SyncContext ctx, ImportAccepted accepted, CancellationToken ct)
        {
            // La API espera unos segundos antes de responder: lo normal es que el
            // lote ya venga hecho y no haya que sondear.
            if (accepted.Status == "failed" || accepted.Status == "canceled") throw new Exception("WeTrakr import failed: " + (accepted.Error ?? accepted.Status));
            if (accepted.BatchesDone >= accepted.Batch) return;

            var deadline = DateTime.UtcNow.AddMinutes(15);
            var delay = 1000;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 5000);
                var job = await WeTrakrApi.GetImportJob(ctx, accepted.JobId, ct).ConfigureAwait(false);
                if (job == null) continue;
                if (job.Status == "failed" || job.Status == "canceled") throw new Exception("WeTrakr import failed: " + (job.Error ?? job.Status));
                if (job.BatchesDone >= accepted.Batch) return;
            }
            throw new TimeoutException("WeTrakr import took too long");
        }

        // =============================================================
        // BAJADA (WeTrakr → Emby)
        // =============================================================

        private class PullResult
        {
            public int Applied { get; set; }
            public bool Unchanged { get; set; }
            public List<InProgressRow> InProgress { get; set; }
        }

        private async Task<PullResult> Pull(SyncContext ctx, User user, UserSyncState state, LibraryIndex index, bool full, CancellationToken ct)
        {
            var res = new PullResult();
            var sync = state.Settings ?? new SyncSettings();

            if (!state.SnapshotDone || state.CursorSince == null)
            {
                DateTime? cursorSince = null;
                var planning = new HashSet<string>();
                foreach (var section in SnapshotSections)
                {
                    string cursor = null;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = await WeTrakrApi.GetSnapshot(ctx, section, cursor, ct).ConfigureAwait(false);
                        if (cursorSince == null && page.CursorSince != null) cursorSince = page.CursorSince;
                        foreach (var row in page.Titles)
                        {
                            res.Applied += ApplyRow(user, state, index, sync, row, false);
                            if (row.Planning)
                            {
                                var key = LibraryIndex.TitleKey(row.Type, row.Ids);
                                if (key != null) planning.Add(key);
                            }
                        }
                        cursor = page.NextCursor;
                    } while (!string.IsNullOrEmpty(cursor));
                }
                state.Planning = planning;
                state.CursorSince = cursorSince ?? DateTime.UtcNow;
                state.SnapshotDone = true;
                // Tras un snapshot el diario se lee entero: lo que cambio mientras
                // tanto debe llegar, y la lista de en curso aun no se ha aplicado.
                full = true;
            }

            ChangesPage changes;
            var guard = 0;
            do
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    changes = await WeTrakrApi.GetChanges(ctx, state.CursorSince.Value, full, ct).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.Error == "SNAPSHOT_REQUIRED")
                {
                    // Mas de 30 dias sin sync: el diario ya no llega tan atras.
                    state.SnapshotDone = false;
                    state.CursorSince = null;
                    return await Pull(ctx, user, state, index, true, ct).ConfigureAwait(false);
                }
                RememberSettings(state, changes.Settings, changes.MaxBatch);
                sync = state.Settings;
                if (changes.Unchanged)
                {
                    state.CursorSince = changes.NextSince;
                    res.Unchanged = true;
                    res.InProgress = new List<InProgressRow>();
                    return res;
                }
                foreach (var row in changes.Titles)
                {
                    res.Applied += ApplyRow(user, state, index, sync, row, true);
                    var key = LibraryIndex.TitleKey(row.Type, row.Ids);
                    if (key == null) continue;
                    if (row.Planning) state.Planning.Add(key);
                    else state.Planning.Remove(key);
                }
                state.CursorSince = changes.NextSince;
                res.InProgress = changes.InProgress ?? new List<InProgressRow>();
                // Las paginas siguientes de una lectura con cambios se leen enteras.
                full = true;
            } while (changes.HasMore && ++guard < 100);

            return res;
        }

        /// <summary>Aplica el estado de un titulo de WeTrakr a sus items de Emby.</summary>
        private int ApplyRow(User user, UserSyncState state, LibraryIndex index, SyncSettings sync, TitleRow row, bool fromChanges)
        {
            var applied = 0;
            foreach (var item in index.Find(row.Type, row.Ids, row.SeriesIds, row.Season, row.Episode))
            {
                var data = _userData.GetUserData(user, item);
                if (data == null) continue;
                var st = state.Get(item.InternalId);
                var dirty = false;

                if (sync.PushWatched && row.Watched != null && !(item is Series))
                {
                    if (row.Watched.IsWatched && !data.Played)
                    {
                        data.Played = true;
                        data.PlayCount = Math.Max(Math.Max(data.PlayCount, 1), row.Watched.PlayCount);
                        if (!row.Watched.Unknown && row.Watched.LastWatchedAt != null)
                        {
                            data.LastPlayedDate = new DateTimeOffset(DateTime.SpecifyKind(row.Watched.LastWatchedAt.Value.ToUniversalTime(), DateTimeKind.Utc));
                        }
                        else if (data.LastPlayedDate == null)
                        {
                            data.LastPlayedDate = DateTimeOffset.UtcNow;
                        }
                        data.PlaybackPositionTicks = 0;
                        dirty = true;
                    }
                    else if (fromChanges && row.ExplicitUnwatch && data.Played)
                    {
                        // Solo si en Emby no se ha vuelto a ver desde la ultima
                        // pasada: el visto local mas reciente gana.
                        var unchangedLocally = st.Played && (data.LastPlayedDate?.UtcTicks ?? 0) <= st.LastPlayedTicks;
                        if (unchangedLocally)
                        {
                            data.Played = false;
                            dirty = true;
                        }
                    }
                }

                if (sync.PushFavorites && data.IsFavorite != row.Favorite)
                {
                    // Quitar un favorito solo si estaba sincronizado como tal y en
                    // Emby no se ha tocado desde entonces.
                    if (row.Favorite || (fromChanges && st.Favorite && data.IsFavorite))
                    {
                        data.IsFavorite = row.Favorite;
                        dirty = true;
                    }
                }

                if (sync.PushRatings)
                {
                    if (row.Rating != null && (data.Rating == null || Math.Abs(data.Rating.Value - row.Rating.Value) >= 0.5))
                    {
                        data.Rating = row.Rating;
                        dirty = true;
                    }
                    else if (fromChanges && row.Rating == null && data.Rating != null && SameRating(st.Rating, data.Rating))
                    {
                        data.Rating = null;
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                    applied++;
                }
                Remember(st, data);
            }
            return applied;
        }

        // =============================================================
        // REANUDACION
        // =============================================================

        private int ApplyProgress(User user, UserSyncState state, LibraryIndex index, SyncSettings sync, List<InProgressRow> rows)
        {
            if (!sync.PushProgress || rows == null) return 0;
            var written = 0;
            var playing = PlayingItemIds();
            var wanted = new HashSet<long>();

            foreach (var row in rows)
            {
                foreach (var item in index.Find(row.Type, row.Ids, row.SeriesIds, row.Season, row.Episode))
                {
                    wanted.Add(item.InternalId);
                    if (playing.Contains(item.InternalId)) continue;
                    var data = _userData.GetUserData(user, item);
                    if (data == null || data.Played) continue;
                    var st = state.Get(item.InternalId);

                    long ticks;
                    if (row.PositionMs != null && row.PositionMs > 0) ticks = row.PositionMs.Value * 10000;
                    else if (item.RunTimeTicks != null) ticks = (long)(item.RunTimeTicks.Value * row.Percent / 100.0);
                    else continue;
                    if (ticks <= 0) continue;

                    // Un punto de reanudacion propio de Emby no se pisa.
                    var ours = st.PositionFromWeTrakr && data.PlaybackPositionTicks == st.Position;
                    if (data.PlaybackPositionTicks > 0 && !ours) continue;
                    if (Math.Abs(data.PlaybackPositionTicks - ticks) < TimeSpan.FromSeconds(30).Ticks) continue;

                    data.PlaybackPositionTicks = ticks;
                    if (data.LastPlayedDate == null && row.UpdatedAt != null) data.LastPlayedDate = new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt.Value.ToUniversalTime(), DateTimeKind.Utc));
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                    Remember(st, data);
                    st.PositionFromWeTrakr = true;
                    written++;
                }
            }

            // Lo que puso el sync y ya no esta en curso en WeTrakr sale de
            // "Continuar viendo" (si en Emby no se ha tocado).
            foreach (var kv in state.Items.Where(kv => kv.Value.PositionFromWeTrakr && !wanted.Contains(kv.Key)).ToList())
            {
                var item = index.GetById(kv.Key);
                if (item == null || playing.Contains(kv.Key)) continue;
                var data = _userData.GetUserData(user, item);
                if (data == null) continue;
                if (data.PlaybackPositionTicks == kv.Value.Position && !data.Played)
                {
                    data.PlaybackPositionTicks = 0;
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                }
                Remember(kv.Value, data);
                kv.Value.PositionFromWeTrakr = false;
            }
            return written;
        }

        private HashSet<long> PlayingItemIds()
        {
            var set = new HashSet<long>();
            try
            {
                foreach (var s in _sessions.Sessions)
                {
                    var id = s?.FullNowPlayingItem?.InternalId ?? 0;
                    if (id > 0) set.Add(id);
                }
            }
            catch { /* sin sesiones: nada que proteger */ }
            return set;
        }

        // =============================================================
        // PLAYLIST DE PLANNING
        // =============================================================

        private async Task SyncPlaylist(User user, UserSyncState state, LibraryIndex index, CancellationToken ct)
        {
            var desired = new List<long>();
            foreach (var key in state.Planning)
            {
                var ids = LibraryIndex.ParseTitleKey(key, out var type);
                if (ids == null) continue;
                if (type == "movie")
                {
                    var movie = index.Find("movie", ids, null, null, null).FirstOrDefault();
                    if (movie != null) desired.Add(movie.InternalId);
                }
                else if (type == "show")
                {
                    // De una serie, el primer episodio sin ver (Emby expandiria la
                    // serie entera en la playlist).
                    var series = index.Find("show", ids, null, null, null).FirstOrDefault();
                    if (series == null) continue;
                    var next = index.EpisodesOf(series.InternalId)
                        .Where(e => (e.ParentIndexNumber ?? 0) > 0)
                        .OrderBy(e => e.ParentIndexNumber).ThenBy(e => e.IndexNumber)
                        .FirstOrDefault(e => !(_userData.GetUserData(user, e)?.Played ?? false));
                    if (next != null) desired.Add(next.InternalId);
                }
            }
            var desiredSet = new HashSet<long>(desired);
            var name = Plugin.Instance.Configuration.WatchlistPlaylistName;
            if (string.IsNullOrWhiteSpace(name)) name = "WeTrakr Watchlist";

            var playlist = state.PlaylistId != null ? _library.GetItemById(state.PlaylistId.Value) as Playlist : null;
            if (playlist == null)
            {
                state.PlaylistItems.Clear();
                if (desired.Count == 0) return;
                var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = name,
                    User = user,
                    MediaType = "Video",
                    ItemIdList = desired.ToArray()
                }).ConfigureAwait(false);
                if (created != null && long.TryParse(created.Id, out var newId)) state.PlaylistId = newId;
                else if (created != null && Guid.TryParse(created.Id, out var g)) state.PlaylistId = _library.GetItemById(g)?.InternalId;
                state.PlaylistItems = desiredSet;
                return;
            }

            var entries = _library.GetItemList(new InternalItemsQuery { User = user, ListIds = new[] { playlist.InternalId } });
            var present = new HashSet<long>(entries.Select(e => e.InternalId));

            var toAdd = desired.Where(id => !present.Contains(id)).Distinct().ToArray();
            if (toAdd.Length > 0) _playlists.AddToPlaylist(playlist.InternalId, toAdd, user);

            var toRemove = entries
                .Where(e => state.PlaylistItems.Contains(e.InternalId) && !desiredSet.Contains(e.InternalId))
                .Select(e => e.ListItemEntryId)
                .Where(id => id > 0)
                .ToArray();
            if (toRemove.Length > 0) await _playlists.RemoveFromPlaylist(playlist.InternalId, toRemove).ConfigureAwait(false);

            state.PlaylistItems = desiredSet;
        }
    }
}
