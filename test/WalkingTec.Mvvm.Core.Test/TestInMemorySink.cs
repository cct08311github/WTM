using System.Collections.Generic;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Minimal in-memory Serilog sink for unit tests.
    /// Replaces Serilog.Sinks.InMemory package which has .NET 10 compatibility issues.
    /// </summary>
    public sealed class TestInMemorySink : ILogEventSink
    {
        private static readonly TestInMemorySink _instance = new();
        public static TestInMemorySink Instance => _instance;

        public List<LogEvent> LogEvents { get; } = new();

        public void Emit(LogEvent logEvent) => LogEvents.Add(logEvent);

        public void Dispose() => LogEvents.Clear();
    }

    public static class TestInMemorySinkExtensions
    {
        public static LoggerConfiguration InMemory(this LoggerSinkConfiguration sinkConfig)
            => sinkConfig.Sink(TestInMemorySink.Instance);
    }
}
