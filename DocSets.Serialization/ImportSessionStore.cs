using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DocSets
{
    public interface IImportSessionStore
    {
        Task<IReadOnlyList<ImportSessionState>> LoadAllAsync(string docSetDirectory,
            CancellationToken cancellationToken = default);
        Task SaveAsync(string docSetDirectory, ImportSessionState session,
            CancellationToken cancellationToken = default);
        Task DeleteAsync(string docSetDirectory, string sessionId,
            CancellationToken cancellationToken = default);
    }

    public sealed class ImportSessionStore : IImportSessionStore
    {
        public const string DirectoryName = "Imports";
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public async Task<IReadOnlyList<ImportSessionState>> LoadAllAsync(string docSetDirectory,
            CancellationToken cancellationToken = default)
        {
            var directory = GetDirectory(docSetDirectory);
            if (!Directory.Exists(directory)) return Array.Empty<ImportSessionState>();
            var result = new List<ImportSessionState>();
            foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var json = await ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    var session = JsonConvert.DeserializeObject<ImportSessionState>(json);
                    if (session == null || string.IsNullOrWhiteSpace(session.Id)) continue;
                    if (session.Status == ImportSessionStatus.Running || session.Status == ImportSessionStatus.Pausing)
                        session.Status = ImportSessionStatus.Interrupted;
                    result.Add(session);
                }
                catch (JsonException exception)
                {
                    DocSetsLog.Current.Error("Импорт", "Повреждён файл сессии импорта: " + path, exception);
                }
            }
            return result.OrderByDescending(x => x.CreatedAtUtc).ToList();
        }

        public async Task SaveAsync(string docSetDirectory, ImportSessionState session,
            CancellationToken cancellationToken = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var directory = GetDirectory(docSetDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, GetFileName(session));
            var temporaryPath = path + ".tmp";
            var json = JsonConvert.SerializeObject(session, Formatting.Indented);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    try { File.Replace(temporaryPath, path, backup, true); }
                    finally { if (File.Exists(backup)) File.Delete(backup); }
                }
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                gate.Release();
            }
        }

        public async Task DeleteAsync(string docSetDirectory, string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            var directory = GetDirectory(docSetDirectory);
            if (!Directory.Exists(directory)) return;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var path in Directory.GetFiles(directory, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var json = await ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                        var state = JsonConvert.DeserializeObject<ImportSessionState>(json);
                        if (string.Equals(state?.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                            File.Delete(path);
                    }
                    catch (JsonException) { }
                }
            }
            finally { gate.Release(); }
        }

        private static string GetDirectory(string docSetDirectory)
        {
            if (string.IsNullOrWhiteSpace(docSetDirectory))
                throw new ArgumentException("Каталог DocSet не задан.", nameof(docSetDirectory));
            return Path.Combine(Path.GetFullPath(docSetDirectory), DirectoryName);
        }

        private static string GetFileName(ImportSessionState session)
        {
            var source = string.IsNullOrWhiteSpace(session.Name) ? session.Id : session.Name;
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(source.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (safe.Length > 80) safe = safe.Substring(0, 80);
            return (string.IsNullOrWhiteSpace(safe) ? "Import" : safe) + "_" + session.Id + ".json";
        }

        private static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.ReadAllText(path, Encoding.UTF8), cancellationToken);
        }

        private static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => File.WriteAllText(path, content, new UTF8Encoding(false)), cancellationToken);
        }
    }
}
