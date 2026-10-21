using Emby.Plugin.WeTrakr.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Rest
{
    // Endpoints de la pagina de usuario (Settings > WeTrakr). Cualquier usuario
    // autenticado, y SIEMPRE sobre su propia cuenta: el usuario sale de la sesion
    // que hace la peticion, nunca de un parametro.

    [Route("/WeTrakr/Me", "GET")]
    [Authenticated]
    public class GetWeTrakrMe : IReturn<object> { }

    [Route("/WeTrakr/Me/Connect/Start", "POST")]
    [Authenticated]
    public class StartWeTrakrMeConnect : IReturn<object> { }

    [Route("/WeTrakr/Me/Connect/Poll", "POST")]
    [Authenticated]
    public class PollWeTrakrMeConnect : IReturn<object> { }

    [Route("/WeTrakr/Me/Connect/Cancel", "POST")]
    [Authenticated]
    public class CancelWeTrakrMeConnect : IReturn<object> { }

    [Route("/WeTrakr/Me/Disconnect", "POST")]
    [Authenticated]
    public class DisconnectWeTrakrMe : IReturn<object> { }

    [Route("/WeTrakr/Me/Sync/Run", "POST")]
    [Authenticated]
    public class RunWeTrakrMeSync : IReturn<object> { }

    public class WeTrakrUserService : IService, IRequiresRequest
    {
        private readonly ISessionContext _session;
        private readonly ITaskManager _tasks;
        private readonly ILogger _logger;

        public IRequest Request { get; set; }

        public WeTrakrUserService(ISessionContext session, ITaskManager tasks, ILogManager logManager)
        {
            _session = session;
            _tasks = tasks;
            _logger = logManager.GetLogger("WeTrakr");
        }

        private User Caller()
        {
            return _session.GetUser(Request);
        }

        public object Get(GetWeTrakrMe request)
        {
            var user = Caller();
            if (user == null) return new { Ok = false, Error = "No user session" };
            return new
            {
                Ok = true,
                Version = Plugin.Instance.PluginVersion,
                SyncEnabled = Plugin.Instance.Configuration.SyncEnabled,
                User = LinkFlow.Status(user)
            };
        }

        public object Post(StartWeTrakrMeConnect request)
        {
            var user = Caller();
            if (user == null) return new { Ok = false, Error = "No user session" };
            return LinkFlow.Start(user.Id.ToString("N"), _logger);
        }

        public object Post(PollWeTrakrMeConnect request)
        {
            var user = Caller();
            if (user == null) return new { Status = "none" };
            return LinkFlow.Poll(user, _tasks);
        }

        public object Post(CancelWeTrakrMeConnect request)
        {
            var user = Caller();
            if (user != null) LinkFlow.Cancel(user.Id.ToString("N"));
            return new { Ok = true };
        }

        public object Post(DisconnectWeTrakrMe request)
        {
            var user = Caller();
            if (user == null) return new { Ok = false };
            LinkFlow.Disconnect(user.Id.ToString("N"));
            return new { Ok = true };
        }

        public object Post(RunWeTrakrMeSync request)
        {
            var user = Caller();
            if (user == null) return new { Ok = false };
            // La tarea sincroniza todas las cuentas enlazadas; es lo mismo que
            // "Sync now" del administrador.
            _tasks.QueueScheduledTask<WeTrakrSyncTask>();
            return new { Ok = true };
        }
    }
}
