using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JDP.Api {
    internal enum ApiStartError {
        // The API is on but api-token.txt is missing or not valid; the user makes a token first
        TokenMissing,
        // Another program listens on the port; the API stays off (no other port is tried)
        PortInUse,
        // Any other failure to listen
        BindFailed,
        // StopAsync was called while the server started; it does not listen
        Stopped,
        // Root on Linux or macOS: the token file's access cannot be proved, so the API stays off
        Privileged
    }

    // The server did not start; the host reports the message and keeps running without the API
    internal sealed class ApiStartException : Exception {
        public ApiStartException(ApiStartError error, string message, Exception innerException = null)
            : base(message, innerException) {
            Error = error;
        }

        public ApiStartError Error { get; }
    }

    // The local API (MP-7a): Kestrel on 127.0.0.1 only, HTTP/1.1, no configuration from files or the environment, and
    // no log output but CoreLoggerProvider's. A host starts it after its thread list is loaded and stops it first
    // when it exits.
    internal sealed class ApiServer : IAsyncDisposable {
        private readonly ApiThreadService _threads;
        private readonly ApiTokenStore _tokens;
        private WebApplication _app;
        private RateLimiter _requests;
        private RateLimiter _adds;
        private volatile int _boundPort;
        // Set by StartAsync, and cleared again by a start that failed; a server starts once
        private int _startCalled;
        // The last start, which a stop waits for, so a stop returns only once the port is free. A start and a stop take
        // _sync to check or close the dispatcher and to set or read this, so a stop never misses a start.
        private readonly object _sync = new object();
        private Task _startTask = Task.CompletedTask;

        public ApiServer(ApiThreadService threads, ApiTokenStore tokens) {
            _threads = threads ?? throw new ArgumentNullException(nameof(threads));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        // Test only: one more log provider, given every level
        internal ILoggerProvider LoggerProviderForTesting { get; set; }

        // Test only: runs once Kestrel listens, before the start is complete
        internal Func<Task> ListeningForTesting { get; set; }

        // The port it listens on, or 0 when it does not listen
        public int BoundPort {
            get { return _boundPort; }
        }

        // Returns the port it listens on (port 0, for the tests, picks a free one). Throws ApiStartException; after a
        // failure that is not Stopped, the same server can be started again. A stop that comes while this runs wins:
        // the server stops listening and this throws ApiStartException (Stopped). The work runs on the thread pool,
        // so no await inside Kestrel or the host comes back to the caller's SynchronizationContext (the app's UI
        // thread).
        public Task<int> StartAsync(int port) {
            if (port < 0 || port > Settings.MaximumApiPort) throw new ArgumentOutOfRangeException(nameof(port));
            ThrowIfCannotRunHere();
            if (!_tokens.IsConfigured()) throw new ApiStartException(ApiStartError.TokenMissing, "The local API is on, but it has no token, or its token file can be read by others. Make a new token.");
            lock (_sync) {
                ThrowIfCannotStart();
                Task<int> start = Task.Run(() => StartCoreAsync(port));
                _startTask = start;
                return start;
            }
        }

        // Root can read every file, so the token file's mode proves nothing (see OwnerOnlyFile)
        private static void ThrowIfCannotRunHere() {
            if (OwnerOnlyFile.IsRootOnUnix()) throw new ApiStartException(ApiStartError.Privileged, "The local API does not run as root. Run the program as a normal user to use it.");
        }

        // A server starts once: its stop closes the dispatcher for good, so a host that turns the API on again makes a
        // new dispatcher, service and server
        private void ThrowIfCannotStart() {
            if (Interlocked.Exchange(ref _startCalled, 1) != 0 || _threads.Dispatcher.IsClosed) throw new InvalidOperationException("The server was already started, or was stopped; make a new one.");
        }

        // Any failure, at any step, leaves nothing listening and throws ApiStartException
        private async Task<int> StartCoreAsync(int port) {
            WebApplication app = null;
            try {
                app = Build(port);
                await app.StartAsync().ConfigureAwait(false);
                if (ListeningForTesting != null) await ListeningForTesting().ConfigureAwait(false);
                return Publish(app);
            }
            catch (Exception ex) {
                await AbandonStartAsync(app).ConfigureAwait(false);
                throw ex as ApiStartException ?? ToStartException(ex, port);
            }
        }

        // AddressInUseException comes wrapped in an IOException; any other failure (an excluded or privileged port:
        // access denied, or a failure while setting up) is BindFailed
        internal static ApiStartException ToStartException(Exception ex, int port) {
            return ex.InnerException is AddressInUseException || ex is AddressInUseException ?
                new ApiStartException(ApiStartError.PortInUse, "The local API could not start: port " + port + " is in use by another program.", ex) :
                new ApiStartException(ApiStartError.BindFailed, "The local API could not listen on port " + port + ".", ex);
        }

        // The port is published only if no stop came during the start
        private int Publish(WebApplication app) {
            int port = ReadBoundPort(app);
            Volatile.Write(ref _app, app);
            _boundPort = port;
            if (_threads.Dispatcher.IsClosed) throw new ApiStartException(ApiStartError.Stopped, "The local API was stopped while it started.");
            return port;
        }

        private async Task AbandonStartAsync(WebApplication app) {
            _boundPort = 0;
            if (app != null) {
                Interlocked.CompareExchange(ref _app, null, app);
                await StopAndDisposeAsync(app, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            Volatile.Write(ref _startCalled, 0);
        }

        // Pending and new requests get 503 at once, then the server stops, waiting for running requests up to the
        // timeout; it returns once the port is free, also when a start was running. Safe to call more than once, and
        // never throws but for a bad timeout. Hosts call it off their owner thread (it never needs that thread, and
        // Close never runs request code on the caller). The stop runs on the thread pool, so it never waits for the
        // caller's SynchronizationContext.
        public Task StopAsync(TimeSpan timeout) {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
            Task start;
            lock (_sync) {
                _threads.Dispatcher.Close();
                start = _startTask;
            }
            _boundPort = 0;
            return Task.Run(() => StopCoreAsync(start, timeout));
        }

        // A start that sees the closed dispatcher stops its own app; then whatever is published is stopped here
        private async Task StopCoreAsync(Task start, TimeSpan timeout) {
            try {
                await start.ConfigureAwait(false);
            }
            catch (Exception) {
            }
            WebApplication app = Interlocked.Exchange(ref _app, null);
            if (app != null) await StopAndDisposeAsync(app, timeout).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() {
            return new ValueTask(StopAsync(TimeSpan.FromSeconds(5)));
        }

        // Never throws: a failure is logged as its type only
        private async Task StopAndDisposeAsync(WebApplication app, TimeSpan timeout) {
            try {
                using (CancellationTokenSource cancel = new CancellationTokenSource(timeout)) {
                    await app.StopAsync(cancel.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) {
                LogFailure("stop", ex);
            }
            catch (OperationCanceledException) {
            }
            await DisposeAppAsync(app).ConfigureAwait(false);
        }

        private async Task DisposeAppAsync(WebApplication app) {
            try {
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) {
                LogFailure("dispose", ex);
            }
            _requests?.Dispose();
            _adds?.Dispose();
        }

        private static void LogFailure(string step, Exception ex) {
            Logger.Log("Local API: the server's " + step + " failed: " + ex.GetType().FullName);
        }
        // The routes the server serves, for the contract test
        // Test only: the running host's services, or null when it does not listen
        internal IServiceProvider ServicesForTesting {
            get { return Volatile.Read(ref _app)?.Services; }
        }

        internal IReadOnlyList<RouteEndpoint> GetRouteEndpoints() {
            WebApplication app = _app ?? throw new InvalidOperationException("The server is not started.");
            return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
        }

        private WebApplication Build(int port) {
            WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
                ApplicationName = "ChanThreadWatch.Api",
                EnvironmentName = "Production",
                ContentRootPath = AppContext.BaseDirectory,
                Args = Array.Empty<string>()
            });
            builder.WebHost.UseKestrelCore().ConfigureKestrel(options => ConfigureKestrel(options, port));
            builder.Services.AddRoutingCore();
            // In place of ConsoleLifetime, whose handlers would cancel Ctrl+C, SIGQUIT and SIGTERM while the server runs
            builder.Services.AddSingleton<IHostLifetime, ApiHostLifetime>();
            ConfigureLogging(builder.Logging);
            WebApplication app = builder.Build();
            ConfigurePipeline(app);
            return app;
        }

        private void ConfigureKestrel(KestrelServerOptions options, int port) {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = _threads.Policy.MaxBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        }

        // Warnings and errors only. The categories that write request lines or paths (hosting diagnostics, Kestrel's
        // bad-request texts, routing's matcher) are off at every level, so not even a test's all-level log sees a token
        // that a client put in a path or query string.
        private void ConfigureLogging(ILoggingBuilder logging) {
            logging.ClearProviders();
            logging.AddProvider(new CoreLoggerProvider());
            logging.SetMinimumLevel(LogLevel.Warning);
            if (LoggerProviderForTesting != null) {
                logging.AddProvider(LoggerProviderForTesting);
                logging.SetMinimumLevel(LogLevel.Trace);
            }
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.None);
        }

        private void ConfigurePipeline(WebApplication app) {
            _requests = CreateLimiter(_threads.Policy.RequestsPerMinute, _threads.Policy.RateWindow);
            _adds = CreateLimiter(_threads.Policy.AddsPerMinute, _threads.Policy.RateWindow);
            ApiSecurity security = new ApiSecurity(_tokens, () => _boundPort, _requests);
            app.Use(security.InvokeAsync);
            app.Use(WriteEmptyErrorAsProblemAsync);
            app.UseRouting();
            new ThreadsEndpoints(_threads, _adds).Map(app);
        }

        private static RateLimiter CreateLimiter(int permits, TimeSpan window) {
            return new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
        }

        // Routing answers an unknown path (404) or method (405, with Allow) without a body; they get the problem body
        private static async Task WriteEmptyErrorAsProblemAsync(HttpContext context, RequestDelegate next) {
            await next(context).ConfigureAwait(false);
            if (context.Response.HasStarted) return;
            ApiError error = context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed ? ApiError.MethodNotAllowed :
                context.Response.StatusCode == StatusCodes.Status404NotFound ? ApiError.NotFound : null;
            if (error != null) await error.WriteAsync(context).ConfigureAwait(false);
        }

        private static int ReadBoundPort(WebApplication app) {
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            string address = addresses?.Addresses.FirstOrDefault() ?? throw new InvalidOperationException("The server has no address.");
            return new Uri(address).Port;
        }
    }

    // The server's host waits for no signal: the program that runs it (the app, ctw watch) handles Ctrl+C and the
    // other stop signals, and stops the server itself
    internal sealed class ApiHostLifetime : IHostLifetime {
        public Task WaitForStartAsync(CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }
    }
}
