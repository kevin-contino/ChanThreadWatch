using System;
using Microsoft.Extensions.Logging;

namespace JDP.Api {
    // The server's only log output: warnings and errors go to the app's log (Logger) as the category, the event and
    // the exception's type. The formatted message and the exception's text are never written, since they can hold a
    // request's path, query string or headers.
    internal sealed class CoreLoggerProvider : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) {
            return new CoreLogger(categoryName);
        }

        public void Dispose() {
        }

        private sealed class CoreLogger : ILogger {
            private readonly string _category;

            public CoreLogger(string category) {
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return logLevel >= LogLevel.Warning && logLevel != LogLevel.None;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) {
                if (!IsEnabled(logLevel)) return;
                Logger.Log(FormatEntry(logLevel, _category, eventId, exception));
            }
        }

        internal static string FormatEntry(LogLevel logLevel, string category, EventId eventId, Exception exception) {
            string text = "Local API " + logLevel + ": " + category + ", event " + eventId.Id + (String.IsNullOrEmpty(eventId.Name) ? "" : " (" + eventId.Name + ")");
            return exception != null ? text + ", " + exception.GetType().FullName : text;
        }
    }
}
