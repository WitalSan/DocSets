using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DocSets
{
    public enum DocSetsLogLevel
    {
        Trace,
        Info,
        Warning,
        Error
    }

    public sealed class DocSetsLogEntry : EventArgs
    {
        public DateTimeOffset Timestamp { get; internal set; }
        public DocSetsLogLevel Level { get; internal set; }
        public string Category { get; internal set; } = "";
        public string Message { get; internal set; } = "";
        public Exception Exception { get; internal set; }
    }

    public interface IDocSetsLogger
    {
        void Trace(string category, string message);
        void Info(string category, string message);
        void Warning(string category, string message);
        void Error(string category, string message, Exception exception = null);
    }

    public sealed class DocSetsLog : IDocSetsLogger
    {
        private const int MaximumEntries = 5000;
        private const int RetentionDays = 14;
        private readonly object syncRoot = new object();
        private readonly List<DocSetsLogEntry> entries = new List<DocSetsLogEntry>();
        private readonly string logsDirectory;

        public static DocSetsLog Current { get; } = new DocSetsLog();

        public event EventHandler<DocSetsLogEntry> EntryAdded;
        public event EventHandler Cleared;

        public DocSetsLog(string logsDirectory = null)
        {
            this.logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocSets", "Logs")
                : Path.GetFullPath(logsDirectory);
            TryPrepareDirectory();
        }

        public string CurrentFilePath => Path.Combine(logsDirectory,
            "DocSets-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        public IReadOnlyList<DocSetsLogEntry> Snapshot()
        {
            lock (syncRoot) return entries.ToArray();
        }

        public void Clear()
        {
            lock (syncRoot) entries.Clear();
            try { Cleared?.Invoke(this, EventArgs.Empty); }
            catch { }
        }

        public void Trace(string category, string message) => Write(DocSetsLogLevel.Trace, category, message, null);
        public void Info(string category, string message) => Write(DocSetsLogLevel.Info, category, message, null);
        public void Warning(string category, string message) => Write(DocSetsLogLevel.Warning, category, message, null);
        public void Error(string category, string message, Exception exception = null) => Write(DocSetsLogLevel.Error, category, message, exception);

        private void Write(DocSetsLogLevel level, string category, string message, Exception exception)
        {
            var entry = new DocSetsLogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Level = level,
                Category = string.IsNullOrWhiteSpace(category) ? "Общее" : category.Trim(),
                Message = message ?? "",
                Exception = exception
            };

            lock (syncRoot)
            {
                entries.Add(entry);
                if (entries.Count > MaximumEntries) entries.RemoveRange(0, entries.Count - MaximumEntries);
                TryAppend(entry);
            }

            try { EntryAdded?.Invoke(this, entry); }
            catch { }
        }

        private void TryPrepareDirectory()
        {
            try
            {
                Directory.CreateDirectory(logsDirectory);
                var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(logsDirectory, "DocSets-*.log")
                    .Where(x => File.GetLastWriteTime(x) < cutoff))
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
            catch
            {
                // Ошибка журнала не должна влиять на работу приложения.
            }
        }

        private void TryAppend(DocSetsLogEntry entry)
        {
            try
            {
                Directory.CreateDirectory(logsDirectory);
                var line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff zzz} | {1,-7} | {2} | {3}",
                    entry.Timestamp, entry.Level.ToString().ToUpperInvariant(), entry.Category,
                    NormalizeLine(entry.Message));
                var builder = new StringBuilder(line).AppendLine();
                if (entry.Exception != null) builder.AppendLine(entry.Exception.ToString());
                File.AppendAllText(CurrentFilePath, builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Ошибка записи журнала игнорируется.
            }
        }

        private static string NormalizeLine(string value)
            => (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }
}
