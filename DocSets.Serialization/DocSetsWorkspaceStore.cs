using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DocSets
{
    public sealed class DocSetsWorkspaceLocation
    {
        public DocSetsWorkspaceLocation(string filePath, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Необходимо указать файл рабочего пространства.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("Необходимо указать базовый каталог.", nameof(baseDirectory));
            FilePath = Path.GetFullPath(filePath);
            BaseDirectory = Path.GetFullPath(baseDirectory);
        }

        public string FilePath { get; }
        public string BaseDirectory { get; }

        public static DocSetsWorkspaceLocation ForSolution(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath))
                throw new ArgumentException("Необходимо указать путь solution.", nameof(solutionPath));
            var fullSolutionPath = Path.GetFullPath(solutionPath);
            var solutionDirectory = Path.GetDirectoryName(fullSolutionPath)
                ?? throw new InvalidOperationException("Не удалось определить каталог solution.");
            return new DocSetsWorkspaceLocation(
                Path.Combine(solutionDirectory, ".vs", "DocSets", "workspace.json"),
                solutionDirectory);
        }
    }

    public interface IDocSetsWorkspaceStore
    {
        Task<DocSetsWorkspace> LoadAsync(DocSetsWorkspaceLocation location,
            CancellationToken cancellationToken = default);
        Task SaveAsync(DocSetsWorkspaceLocation location, DocSetsWorkspace workspace,
            CancellationToken cancellationToken = default);
    }

    public sealed class JsonDocSetsWorkspaceStore : IDocSetsWorkspaceStore
    {
        private readonly IDocSetsLogger logger;

        public JsonDocSetsWorkspaceStore(IDocSetsLogger logger = null)
        {
            this.logger = logger ?? DocSetsLog.Current;
        }

        public async Task<DocSetsWorkspace> LoadAsync(DocSetsWorkspaceLocation location,
            CancellationToken cancellationToken = default)
        {
            if (location == null) throw new ArgumentNullException(nameof(location));
            if (!File.Exists(location.FilePath)) return new DocSetsWorkspace();

            string json;
            using (var reader = File.OpenText(location.FilePath))
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = JsonConvert.DeserializeObject<DocSetsWorkspace>(json)
                ?? throw new InvalidDataException("Файл рабочего пространства пуст.");
            Validate(workspace);
            logger.Info("Рабочее пространство", "Рабочее пространство загружено: " + location.FilePath);
            return workspace;
        }

        public async Task SaveAsync(DocSetsWorkspaceLocation location, DocSetsWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            if (location == null) throw new ArgumentNullException(nameof(location));
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));
            Validate(workspace);

            var directory = Path.GetDirectoryName(location.FilePath)
                ?? throw new InvalidOperationException("Не удалось определить каталог рабочего пространства.");
            Directory.CreateDirectory(directory);
            var temporaryPath = location.FilePath + ".tmp." + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(workspace, Formatting.Indented);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var writer = new StreamWriter(temporaryPath, false))
                    await writer.WriteAsync(json).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Validate(JsonConvert.DeserializeObject<DocSetsWorkspace>(File.ReadAllText(temporaryPath)));
                Replace(temporaryPath, location.FilePath);
                logger.Info("Рабочее пространство", "Рабочее пространство сохранено: " + location.FilePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        private static void Validate(DocSetsWorkspace workspace)
        {
            if (workspace == null) throw new InvalidDataException("Файл рабочего пространства пуст.");
            if (!string.Equals(workspace.Format, DocSetsWorkspace.CurrentFormat, StringComparison.Ordinal))
                throw new InvalidDataException("Формат рабочего пространства не поддерживается.");
            if (workspace.FormatVersion != DocSetsWorkspace.CurrentFormatVersion)
                throw new NotSupportedException("Версия рабочего пространства не поддерживается: " + workspace.FormatVersion + ".");
            workspace.OpenDocSets = workspace.OpenDocSets ?? new List<string>();
            workspace.OpenDocSets = workspace.OpenDocSets
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!workspace.OpenDocSets.Contains(workspace.ActiveDocSet ?? "", StringComparer.OrdinalIgnoreCase))
                workspace.ActiveDocSet = workspace.OpenDocSets.FirstOrDefault() ?? "";
            workspace.SourceRootOverrides = workspace.SourceRootOverrides
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            workspace.SourceRootOverrides = new Dictionary<string, string>(
                workspace.SourceRootOverrides, StringComparer.OrdinalIgnoreCase);
            workspace.Ui = workspace.Ui ?? new DocSetsWorkspaceUiState();
            workspace.Ui.SelectedItemIds = workspace.Ui.SelectedItemIds
                ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            workspace.Ui.SelectedItemIds = new Dictionary<string, List<string>>(
                workspace.Ui.SelectedItemIds, StringComparer.OrdinalIgnoreCase);
        }

        private static void Replace(string temporaryPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(temporaryPath, targetPath);
                return;
            }

            try
            {
                File.Replace(temporaryPath, targetPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByCopy(temporaryPath, targetPath);
            }
            catch (IOException)
            {
                ReplaceByCopy(temporaryPath, targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                ReplaceByCopy(temporaryPath, targetPath);
            }
        }

        private static void ReplaceByCopy(string temporaryPath, string targetPath)
        {
            File.Copy(temporaryPath, targetPath, true);
            File.Delete(temporaryPath);
        }
    }

    public sealed class DocSetsWorkspaceManager
    {
        private readonly DocSetsWorkspaceLocation location;

        public DocSetsWorkspaceManager(DocSetsWorkspaceLocation location, DocSetsWorkspace workspace)
        {
            this.location = location ?? throw new ArgumentNullException(nameof(location));
            Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        }

        public DocSetsWorkspace Workspace { get; }

        public bool Open(string docSetDirectory, bool makeActive = true)
        {
            var storedPath = ToStoredPath(docSetDirectory);
            var added = false;
            if (!Workspace.OpenDocSets.Contains(storedPath, StringComparer.OrdinalIgnoreCase))
            {
                Workspace.OpenDocSets.Add(storedPath);
                added = true;
            }
            var activeChanged = !string.Equals(Workspace.ActiveDocSet, storedPath, StringComparison.OrdinalIgnoreCase) && makeActive;
            if (makeActive) Workspace.ActiveDocSet = storedPath;
            return added || activeChanged;
        }

        public bool Close(string docSetDirectory)
        {
            var storedPath = ToStoredPath(docSetDirectory);
            var removed = Workspace.OpenDocSets.RemoveAll(x => string.Equals(x, storedPath, StringComparison.OrdinalIgnoreCase)) > 0;
            if (string.Equals(Workspace.ActiveDocSet, storedPath, StringComparison.OrdinalIgnoreCase))
                Workspace.ActiveDocSet = Workspace.OpenDocSets.FirstOrDefault() ?? "";
            return removed;
        }

        public bool Activate(string docSetDirectory)
        {
            var storedPath = ToStoredPath(docSetDirectory);
            if (!Workspace.OpenDocSets.Contains(storedPath, StringComparer.OrdinalIgnoreCase)) return false;
            if (string.Equals(Workspace.ActiveDocSet, storedPath, StringComparison.OrdinalIgnoreCase)) return false;
            Workspace.ActiveDocSet = storedPath;
            return true;
        }

        public IReadOnlyList<string> ResolveOpenDocSets()
            => Workspace.OpenDocSets.Select(ResolveStoredPath).ToList();

        public string ResolveActiveDocSet()
            => string.IsNullOrWhiteSpace(Workspace.ActiveDocSet) ? "" : ResolveStoredPath(Workspace.ActiveDocSet);

        public string GetSourceOverrideKey(string docSetId, string sourceId)
            => (docSetId ?? "").Trim() + "/" + (sourceId ?? "").Trim();

        private string ToStoredPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Необходимо указать каталог DocSet.", nameof(path));
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var basePath = location.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return fullPath;
            return MakeRelativePath(location.BaseDirectory, fullPath);
        }

        private string ResolveStoredPath(string storedPath)
            => Path.GetFullPath(Path.IsPathRooted(storedPath)
                ? storedPath
                : Path.Combine(location.BaseDirectory, storedPath));

        private static string MakeRelativePath(string baseDirectory, string targetPath)
        {
            var baseUri = new Uri(AppendSeparator(Path.GetFullPath(baseDirectory)));
            var targetUri = new Uri(Path.GetFullPath(targetPath));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSeparator(string path)
            => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
