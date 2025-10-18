using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PlayWright
{
    // Simple file logger that writes formatted log lines to a file stream.
    // The provider opens the file using FileMode.Create so it overwrites on each run.
    public class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerProvider _provider;

        public FileLogger(string categoryName, FileLoggerProvider provider)
        {
            _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinLogLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null) return;

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var level = logLevel.ToString();
            var line = $"{timestamp} [{level}] {_categoryName}: {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            try
            {
                lock (_provider.Lock)
                {
                    using var writer = new StreamWriter(_provider.Stream, _provider.Encoding, 1024, leaveOpen: true);
                    writer.WriteLine(line);
                    writer.Flush();
                }
            }
            catch
            {
                // Swallow exceptions - logging should not throw.
            }
        }
    }

    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        internal readonly Stream Stream;
        internal readonly object Lock = new object();
        internal readonly System.Text.Encoding Encoding = System.Text.Encoding.UTF8;
        public LogLevel MinLogLevel { get; }

        public FileLoggerProvider(string path, LogLevel minLogLevel = LogLevel.Trace)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            MinLogLevel = minLogLevel;

            // Ensure directory exists
            var dir = Path.GetDirectoryName(_path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Open file with Create so it overwrites on each run.
            Stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

        public void Dispose()
        {
            try
            {
                Stream?.Dispose();
            }
            catch { }
        }
    }
}
