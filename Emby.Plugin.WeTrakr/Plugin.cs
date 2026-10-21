using System;
using System.Collections.Generic;
using System.IO;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.WeTrakr
{
    /// <summary>
    /// Plugin oficial de WeTrakr para Emby: scrobble en vivo y sync de la
    /// biblioteca en las dos direcciones.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginGuid = new Guid("893a5960-e35b-4362-b2cb-94aaa96e1e09");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "WeTrakr";

        public override string Description => "Scrobble your plays to WeTrakr and keep your watched history, progress, ratings, favorites and watchlist in sync both ways.";

        public override Guid Id => PluginGuid;

        /// <summary>Carpeta para el estado del sync (un JSON por usuario de Emby).</summary>
        public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "WeTrakr");

        public string PluginVersion => GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "WeTrakr",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "wetrakrjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
                    IsMainConfigPage = false
                },
                // Pagina de CADA usuario (Settings > WeTrakr): enlazar su propia
                // cuenta sin pasar por el administrador.
                new PluginPageInfo
                {
                    Name = "WeTrakrUser",
                    DisplayName = "WeTrakr",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.userPage.html",
                    EnableInUserMenu = true,
                    MenuIcon = "sync",
                    // El cliente web pide las paginas del menu de usuario con
                    // PageType=PluginConfiguration, y Emby solo da ese tipo a las
                    // paginas marcadas como "main": sin esto no aparece.
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "wetrakruserjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.userPage.js",
                    IsMainConfigPage = false
                }
            };
        }

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
