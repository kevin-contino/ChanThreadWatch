using System;
using System.Threading.Tasks;
using JDP.Api;

namespace JDP {
    internal enum LocalApiState {
        Off,
        Starting,
        Listening,
        // The last start failed; the API is off until the settings are applied again
        Failed
    }

    // What the app's local API did last, in words for the Local API dialog's status line. Never holds the token or its
    // hash, and no path.
    internal sealed class LocalApiStatus {
        public static readonly LocalApiStatus Off = new LocalApiStatus(LocalApiState.Off, "Off");

        private LocalApiStatus(LocalApiState state, string text) {
            State = state;
            Text = text;
        }

        public LocalApiState State { get; }

        public string Text { get; }

        // ApiEnabled is set, but neither 0 nor 1 (most likely a typo, such as "true"), so the API stays off
        public static LocalApiStatus OffNotValid() {
            return new LocalApiStatus(LocalApiState.Off, "Off: ApiEnabled in " + Settings.SettingsFileName + " is neither 0 nor 1. Only 1 turns the local API on.");
        }

        public static LocalApiStatus Starting(int port) {
            return new LocalApiStatus(LocalApiState.Starting, "Starting on 127.0.0.1:" + port);
        }

        public static LocalApiStatus Listening(int port) {
            return new LocalApiStatus(LocalApiState.Listening, "Listening on 127.0.0.1:" + port);
        }

        public static LocalApiStatus Failed(int port, Exception ex) {
            return new LocalApiStatus(LocalApiState.Failed, "Not running: " + DescribeStartFailure(port, ex));
        }

        // The typed failures in plain words; any other failure points to the log
        internal static string DescribeStartFailure(int port, Exception ex) {
            ApiStartException start = ex as ApiStartException;
            if (start == null) return "the local API could not start (see " + Settings.LogFileName + ").";
            switch (start.Error) {
                case ApiStartError.TokenMissing:
                    return "there is no token, or its file can be read by others. Click \"New token...\" to make one.";
                case ApiStartError.PortInUse:
                    return "port " + port + " is in use by another program. Choose another port.";
                case ApiStartError.BindFailed:
                    return "it could not listen on port " + port + ". The port may be reserved by Windows; choose another port.";
                default:
                    return DescribeOtherStartFailure(start.Error);
            }
        }

        // Privileged is root on Linux and macOS, which the API refuses (see ApiServer)
        private static string DescribeOtherStartFailure(ApiStartError error) {
            return error == ApiStartError.Stopped ? "it was stopped while it started." : "the program runs as the system's administrator account, and the local API does not run that way.";
        }
    }

    // The local API (MP-7a) in the app, with the same settings as ctw watch: on only with ApiEnabled=1, on 127.0.0.1 at
    // ApiPort, with the token whose hash is in api-token.txt in the settings folder. The window starts it once the
    // thread list is loaded (OnThreadListLoaded), so no request sees a list that is still loading; applies changed
    // settings without a restart (Apply: a stop, then a start on the new port or settings folder); and stops it first
    // when it closes (StopForExit), before the watchers stop. A request's work runs on the UI thread through the post
    // delegate (the window's BeginInvoke). A start runs on the thread pool and never shows a message: a failure is
    // logged and kept as Status for the Local API dialog. Every method but Status is called on the UI thread.
    internal sealed class LocalApiHost {
        // How long a stop waits for the requests that are running; pending ones get 503 at once. The tests make it
        // longer. Declared first: static fields are set in order.
        internal static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(2);
        internal static TimeSpan StopWait { get; set; } = DefaultStopWait;

        private readonly WatchSession _session;
        private readonly Action<Action> _post;
        private readonly Func<string, ThreadInfo> _newThread;
        // Taken to set or read the server and the status, which a start sets on the thread pool
        private readonly object _sync = new object();
        // The server that starts or listens, and the port and folder it was made for; null when off
        private ApiServer _server;
        private ListenTarget _target;
        private LocalApiStatus _status = LocalApiStatus.Off;
        // Every stop so far: the port is free once it has ended. A start waits for it, so a restart on the same port
        // never finds it still in use.
        private Task _stopped = Task.CompletedTask;
        private Task _lastStart = Task.CompletedTask;
        private bool _loaded;
        private volatile bool _exiting;

        // newThread makes an API-added thread with the main window's choices; it runs on the UI thread
        public LocalApiHost(WatchSession session, Action<Action> post, Func<string, ThreadInfo> newThread) {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _newThread = newThread ?? throw new ArgumentNullException(nameof(newThread));
        }

        public LocalApiStatus Status {
            get {
                lock (_sync) {
                    return _status;
                }
            }
        }

        // Test only: the last start, which ends once its status is set
        internal Task LastStartForTesting {
            get { return _lastStart; }
        }

        // Test only: the server that starts or listens, or null
        internal ApiServer ServerForTesting {
            get {
                lock (_sync) {
                    return _server;
                }
            }
        }

        // Called once, when the thread list and the blacklist are loaded. A value in settings.txt that is not valid is
        // logged once (never a message at start), as ctw watch warns.
        public void OnThreadListLoaded() {
            _loaded = true;
            if (Settings.ApiEnabledIsNotValid) Logger.Log("Local API: ApiEnabled in " + Settings.SettingsFileName + " is neither 0 nor 1, so it stays off. Only ApiEnabled=1 turns it on.");
            if (Settings.ApiPortIsNotValid) {
                Logger.Log("Local API: ApiPort in " + Settings.SettingsFileName + " is not a port from " + Settings.MinimumApiPort + " to " + Settings.MaximumApiPort +
                    ", so port " + Settings.DefaultApiPort + " is used.");
            }
            Apply();
        }

        // Starts, stops or restarts the API to match the settings. A server that starts or listens for the same port
        // and settings folder is kept; one whose start failed is started again (the user may have freed the port or
        // made a token). With restartIfNotListening (a new token was made), a server that is still starting is
        // replaced too, so a start that read the token file before the new token was saved does not end without it.
        // Does nothing before the thread list is loaded or once the window is closing.
        public void Apply(bool restartIfNotListening = false) {
            if (!CanApply) return;
            ListenTarget wanted = ListenTarget.FromSettings();
            if (IsRunningFor(wanted, restartIfNotListening)) return;
            StopServer();
            StartOrStayOff(wanted);
        }

        private bool CanApply {
            get { return _loaded && !_exiting; }
        }

        private bool IsRunningFor(ListenTarget wanted, bool restartIfNotListening) {
            lock (_sync) {
                return wanted != null && wanted.Equals(_target) && IsKept(_status.State, restartIfNotListening);
            }
        }

        private static bool IsKept(LocalApiState state, bool restartIfNotListening) {
            return restartIfNotListening ? state == LocalApiState.Listening : state != LocalApiState.Failed;
        }

        // wanted is null when the API is off
        private void StartOrStayOff(ListenTarget wanted) {
            if (wanted != null) StartServer(wanted);
            else SetStatus(Settings.ApiEnabledIsNotValid ? LocalApiStatus.OffNotValid() : LocalApiStatus.Off);
        }

        // Closes the API's dispatcher at once, so no request adds a thread from here on: work that has not started on
        // the UI thread never runs (503). Returns without waiting; the task ends once the port is free. The window
        // waits for it after the final save, so the first save is not delayed. Never throws but for a bad StopWait.
        public Task StopForExit() {
            _exiting = true;
            StopServer();
            return _stopped;
        }

        private void StopServer() {
            ApiServer server;
            lock (_sync) {
                server = _server;
                _server = null;
                _target = null;
                _status = LocalApiStatus.Off;
            }
            if (server == null) return;
            _stopped = Task.WhenAll(_stopped, server.StopAsync(StopWait));
            Logger.Log("Local API: stopped.");
        }

        private void StartServer(ListenTarget target) {
            ApiPolicy policy = new ApiPolicy { IsExiting = () => _exiting };
            ApiThreadService threads = new ApiThreadService(_session, new OwnerThreadDispatcher(_post), _newThread, policy);
            ApiServer server = new ApiServer(threads, new ApiTokenStore(target.SettingsFolder));
            lock (_sync) {
                _server = server;
                _target = target;
                _status = LocalApiStatus.Starting(target.Port);
            }
            Task stopped = _stopped;
            // On the thread pool from the first step: the token file's check is file access
            _lastStart = Task.Run(() => StartAfterStopsAsync(server, target.Port, stopped));
        }

        // On the thread pool. A server that was stopped or replaced meanwhile does not change the status. Any failure,
        // of whatever type, ends as Failed, so the status never stays "Starting".
        private async Task StartAfterStopsAsync(ApiServer server, int port, Task stopped) {
            await stopped.ConfigureAwait(false);
            try {
                int boundPort = await server.StartAsync(port).ConfigureAwait(false);
                if (SetStatusOf(server, LocalApiStatus.Listening(boundPort))) Logger.Log("Local API: listening on http://127.0.0.1:" + boundPort + "/api/v1/");
            }
            catch (Exception ex) {
                LocalApiStatus failed = LocalApiStatus.Failed(port, ex);
                if (SetStatusOf(server, failed)) LogStartFailure(failed, ex);
            }
        }

        // The status text and the cause's type only: an exception's message can hold a path
        private static void LogStartFailure(LocalApiStatus failed, Exception ex) {
            string causeType = ApiStartFailure.GetCauseTypeName(ex);
            Logger.Log("Local API: " + failed.Text + (causeType != null ? " (" + causeType + ")" : ""));
        }

        private void SetStatus(LocalApiStatus status) {
            lock (_sync) {
                _status = status;
            }
        }

        private bool SetStatusOf(ApiServer server, LocalApiStatus status) {
            lock (_sync) {
                if (_server != server) return false;
                _status = status;
                return true;
            }
        }

        // The port and the settings folder (where api-token.txt is) that a server is made for
        private sealed class ListenTarget : IEquatable<ListenTarget> {
            private ListenTarget(int port, string settingsFolder) {
                Port = port;
                SettingsFolder = settingsFolder;
            }

            public int Port { get; }
            public string SettingsFolder { get; }

            // Null when the API is off. A port that is not valid gives the default one (Settings.ApiPort).
            public static ListenTarget FromSettings() {
                return Settings.ApiEnabled == true ? new ListenTarget(Settings.ApiPort.Value, Settings.GetSettingsDirectory()) : null;
            }

            public bool Equals(ListenTarget other) {
                return other != null && Port == other.Port && String.Equals(SettingsFolder, other.SettingsFolder, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj) {
                return Equals(obj as ListenTarget);
            }

            public override int GetHashCode() {
                return HashCode.Combine(Port, StringComparer.OrdinalIgnoreCase.GetHashCode(SettingsFolder));
            }
        }
    }
}
