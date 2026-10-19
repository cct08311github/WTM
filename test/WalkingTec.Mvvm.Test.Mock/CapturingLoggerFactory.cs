using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Test.Mock
{
    /// <summary>
    /// An <see cref="ILoggerFactory"/> whose loggers record every entry, for asserting on log output.
    /// </summary>
    public class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(Entries);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private class CapturingLogger : ILogger
        {
            private readonly List<(LogLevel Level, string Message)> _entries;

            public CapturingLogger(List<(LogLevel Level, string Message)> entries)
            {
                _entries = entries;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
