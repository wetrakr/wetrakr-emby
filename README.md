# WeTrakr for Emby

Official [WeTrakr](https://wetrakr.com) plugin for Emby Server.

- **Live scrobbling**: what you play shows up in WeTrakr's Now Playing, and finished movies and episodes are marked as watched. Marking items watched or unwatched, rating them and adding favorites in Emby is sent too.
- **Two-way library sync**:
  - **Emby → WeTrakr**: your watched history (with dates), resume points, favorites and ratings. The first sync imports everything; after that only what changed.
  - **WeTrakr → Emby**: what you watched elsewhere (Kodi, Plex, Stremio, the WeTrakr web…) is marked as played in Emby, titles in progress appear in Continue Watching, and favorites and ratings come back too.
  - **Planning list** as a "WeTrakr Watchlist" playlist with the titles you have in your library (for shows, the next unwatched episode).
- **One link per Emby user**: each person on a shared server syncs to their own WeTrakr account.
- No Emby Premiere needed.

A sync never removes a watch on its own: only an explicit "unwatched" on either side does, and a watch made in Emby after the last sync always wins.

## Requirements

Emby Server **4.8 or newer** (built against the 4.8 SDK, tested on 4.10).

## Install

1. Download `Emby.Plugin.WeTrakr.dll` from the [latest release](https://github.com/wetrakr/wetrakr-emby/releases/latest).
2. Stop Emby and copy the file into the `plugins` folder of your server data:
   - Docker: `/config/plugins`
   - Linux: `/var/lib/emby/plugins`
   - Windows: `%AppData%\Emby-Server\programdata\plugins`
   - On Linux/NAS, keep the same file owner as the other plugins.
3. Start Emby again.

## Link your account

1. In Emby open **Settings → Plugins → WeTrakr**.
2. Press **Link WeTrakr** next to your Emby user.
3. Go to [wetrakr.com/activate](https://wetrakr.com/activate?platform=emby) and enter the code.

The first sync starts right away. What goes each way is set in WeTrakr: **Settings → Emby**.

## When it syncs

The plugin is designed to talk to WeTrakr as little as possible:

- **Emby → WeTrakr** goes out as it happens: plays, marks, ratings and favorites are scrobbled live. Anything that could not be sent is picked up by the next upload, a few minutes later.
- **WeTrakr → Emby** is checked with a single cheap request when someone starts using Emby, every 30 minutes while it is in use, and with a slow heartbeat when idle (30 minutes, doubling up to 6 hours while nothing changes). Nothing is downloaded unless WeTrakr reports a change.
- A **full reconciliation** of both directions runs once a day, at night (Emby **Scheduled Tasks → WeTrakr**). You can run it any time with **Sync now**.

An idle server makes about 4 requests a day per linked user.

## Build

```sh
dotnet build Emby.Plugin.WeTrakr/Emby.Plugin.WeTrakr.csproj -c Release
```

The DLL is in `Emby.Plugin.WeTrakr/bin/Release/netstandard2.0/`. It is a single file: the Emby SDK and System.Text.Json are provided by the server at runtime.

## License

MIT
