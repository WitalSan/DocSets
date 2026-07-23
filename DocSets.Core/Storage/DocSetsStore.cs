using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;

namespace DocSets
{
    internal sealed class DocSetsStore
    {
        private readonly AsyncPackage package;
        private readonly RoslynBookmarkResolver roslyn;

        private string solutionDirectory = "";
        private string solutionFilePath = "";
        private string storageDirectory = "";
        private string stateFilePath = "";
        private bool isSharedWorkspace;
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        private DateTime lastKnownWriteTimeUtc = DateTime.MinValue;
        private long lastKnownLength = -1;
        private string lastSavedStateJson = "";
        private readonly DirectoryDocSetStore directoryStore = new DirectoryDocSetStore();
        private readonly DocSetDocumentRepository documentRepository = new DocSetDocumentRepository();
        private readonly JsonDocSetsWorkspaceStore workspaceStore = new JsonDocSetsWorkspaceStore();
        private DocSetsWorkspaceLocation workspaceLocation;
        private DocSetsWorkspaceManager workspaceManager;
        private DocSetDocument currentDocument;
        private string activeDocSetDirectory = "";
        private IReadOnlyList<CodeSourceStatus> sourceStatuses = Array.Empty<CodeSourceStatus>();
        private readonly CodeSourceLocator sourceLocator = new CodeSourceLocator();
        private readonly AssetStorageService assetStorage = new AssetStorageService();

        public DocSetsStore(AsyncPackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            roslyn = new RoslynBookmarkResolver(package);
        }

        public string SolutionDirectory => solutionDirectory;

        public string SolutionFilePath => solutionFilePath;

        public string CurrentSolutionName => Path.GetFileNameWithoutExtension(solutionFilePath) ?? string.Empty;

        public string StorageDirectory => storageDirectory;

        public bool IsSharedWorkspace => isSharedWorkspace;

        public string StateFilePath => stateFilePath;
        public bool HasOpenDocument => currentDocument != null;
        public string AssetDirectory => string.IsNullOrWhiteSpace(activeDocSetDirectory)
            ? "" : Path.Combine(activeDocSetDirectory, "assets");

        public string CurrentWorkspaceRelativePath => ToSolutionRelativePath(activeDocSetDirectory);

        internal SourceReferenceContext CurrentSourceContext
            => SourceReferenceContext.Create(sourceStatuses, sourceLocator);

        public Task<string> SaveImageAssetAsync(byte[] content, string mimeType, string originalName)
        {
            if (string.IsNullOrWhiteSpace(activeDocSetDirectory))
                throw new InvalidOperationException("DocSet не открыт.");
            return assetStorage.SaveImageAsync(activeDocSetDirectory, content, mimeType, originalName);
        }

        public Task<string> NormalizeCommentAssetsAsync(string markdown,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(activeDocSetDirectory))
                throw new InvalidOperationException("DocSet не открыт.");
            return assetStorage.ImportEmbeddedImagesAsync(
                activeDocSetDirectory, markdown, cancellationToken);
        }

        internal IReadOnlyList<string> FindAssetReferences(string markdown)
            => assetStorage.FindReferences(markdown);

        internal byte[] ReadAsset(string assetReference)
            => assetStorage.Read(activeDocSetDirectory, assetReference);

        internal string GetAssetMimeType(string assetReference)
            => assetStorage.GetMimeType(assetReference);

        public async Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync()
        {
            if (!await EnsureInitializedAsync()) return Array.Empty<WorkspaceInfo>();
            return workspaceManager.ResolveOpenDocSets()
                .Select(CreateWorkspaceInfo)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<bool> SelectWorkspaceAsync(string relativePath)
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(relativePath)) return false;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(solutionDirectory, relativePath));
            return await OpenDocSetCoreAsync(fullPath, true);
        }

        public async Task<bool> OpenDocSetAsync(string directoryPath)
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(directoryPath)) return false;
            return await OpenDocSetCoreAsync(directoryPath, true);
        }

        public async Task<bool> CreateDocSetAsync(string directoryPath, string name)
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(directoryPath)) return false;
            var fullPath = Path.GetFullPath(directoryPath);
            var displayName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : name.Trim();
            await directoryStore.CreateAsync(fullPath, CreateReadableId(displayName), displayName);
            return await OpenDocSetCoreAsync(fullPath, true);
        }

        public async Task<DocumentSetsState> LoadAsync(bool forceReload = false)
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(activeDocSetDirectory)) return null;

            // Восстановление рабочего пространства и команды открытия уже загрузили
            // документ в OpenDocSetCoreAsync. Повторно читаем его только при обнаружении
            // внешнего изменения на диске.
            if (currentDocument != null && !forceReload)
            {
                return currentDocument.State;
            }

            try
            {
                currentDocument = await documentRepository.OpenAsync(activeDocSetDirectory);
                ConfigureActiveDocument(currentDocument);
                lastSavedStateJson = SerializeState(currentDocument.State);
                RememberCurrentFileStamp();
                return currentDocument.State;
            }
            catch
            {
                // Не запоминаем метку повреждённого/недописанного файла:
                // следующая проверка таймера попробует прочитать его ещё раз.
                return null;
            }
        }

        public async Task SaveAsync(DocumentSetsState state)
        {
            if (state == null)
            {
                return;
            }

            if (!await EnsureInitializedAsync() || currentDocument == null)
            {
                VsShellUtilities.ShowMessageBox(
                    package,
                    "Откройте или создайте DocSet, чтобы сохранить изменения.",
                    "DocSets",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return;
            }

            await saveGate.WaitAsync();
            try
            {
                await NormalizeEmbeddedImagesAsync(state);
                var stateJson = SerializeState(state);
                if (string.Equals(stateJson, lastSavedStateJson, StringComparison.Ordinal))
                {
                    return;
                }

                currentDocument.ReplaceState(state);
                await documentRepository.SaveAsync(currentDocument);
                lastSavedStateJson = SerializeState(state);
                RememberCurrentFileStamp();
            }
            finally
            {
                saveGate.Release();
            }
        }

        public async Task<bool> HasExternalChangesAsync()
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(StateFilePath))
            {
                return false;
            }

            if (!File.Exists(StateFilePath))
            {
                return lastKnownWriteTimeUtc != DateTime.MinValue || lastKnownLength >= 0;
            }

            try
            {
                var info = new FileInfo(StateFilePath);
                return info.LastWriteTimeUtc != lastKnownWriteTimeUtc ||
                       info.Length != lastKnownLength;
            }
            catch
            {
                return false;
            }
        }

        private void RememberCurrentFileStamp()
        {
            if (string.IsNullOrWhiteSpace(StateFilePath) || !File.Exists(StateFilePath))
            {
                lastKnownWriteTimeUtc = DateTime.MinValue;
                lastKnownLength = -1;
                return;
            }

            try
            {
                var info = new FileInfo(StateFilePath);
                lastKnownWriteTimeUtc = info.LastWriteTimeUtc;
                lastKnownLength = info.Length;
            }
            catch
            {
                // Оставляем предыдущую метку и повторяем проверку на следующем тике.
            }
        }

        public async Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            var sourceId = "";
            var item = await roslyn.CreateBookmarkFromActiveDocumentAsync(
                StorageDirectory,
                path => ToSourceRelativePath(path, out sourceId));
            if (item != null) item.SourceId = sourceId;
            return item;
        }


        public async Task<DocumentItem> CreateClassBookmarkFromActiveDocumentAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            var sourceId = "";
            var item = await roslyn.CreateClassBookmarkFromActiveDocumentAsync(
                StorageDirectory,
                path => ToSourceRelativePath(path, out sourceId));
            if (item != null) item.SourceId = sourceId;
            return item;
        }


        public async Task<ActiveDocumentContext> GetActiveDocumentContextAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            var context = await roslyn.GetActiveDocumentContextAsync();
            if (context == null)
            {
                return null;
            }

            context.SolutionName = Path.GetFileNameWithoutExtension(solutionFilePath) ?? "";
            return context;
        }

        public async Task<ActiveSymbolReference> GetActiveSymbolReferenceAsync(string draggedText)
        {
            var reference = await roslyn.GetActiveSymbolReferenceAsync(draggedText);
            if (reference == null) return null;
            var source = sourceLocator.FindForFile(sourceStatuses, reference.Path);
            var defaultSource = sourceLocator.GetDefault(sourceStatuses);
            reference.SourceId = source == null || ReferenceEquals(source, defaultSource)
                ? ""
                : source.Source.Id ?? "";
            return reference;
        }

        public async Task OpenBookmarkAsync(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (item.Type != BookmarkType.File && await roslyn.TryOpenBookmarkBySymbolAsync(item))
            {
                return;
            }

            var fullPath = ToFullPath(item);

            if (!File.Exists(fullPath))
            {
                VsShellUtilities.ShowMessageBox(
                    package,
                    $"Файл не найден:{Environment.NewLine}{fullPath}",
                    "DocSets",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return;
            }

            await roslyn.OpenFileAtAsync(
                fullPath,
                Math.Max(1, item.Line),
                Math.Max(1, item.Column));
            await roslyn.RestoreEditorStateAsync(item, Math.Max(1, item.Line));
        }

        public async Task<string> GetLivePreviewAsync(DocumentItem item, CancellationToken cancellationToken)
        {
            if (item == null || item.NodeType == NodeType.Folder || string.IsNullOrWhiteSpace(item.Path))
            {
                return string.Empty;
            }

            if (!await EnsureInitializedAsync())
            {
                return string.Empty;
            }

            return await roslyn.GetLivePreviewAsync(
                ToFullPath(item),
                Math.Max(1, item.Line),
                Math.Max(1, item.Column),
                cancellationToken);
        }

        public Task<bool> OpenSymbolAsync(string symbol, string project)
        {
            return roslyn.TryOpenSymbolAsync(symbol, project);
        }

        private async Task<bool> EnsureInitializedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var solution = await package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) { ClearSolutionState(); return false; }

            solution.GetSolutionInfo(out var directory, out var solutionFile, out _);
            if (string.IsNullOrWhiteSpace(solutionFile)) { ClearSolutionState(); return false; }

            var normalizedSolutionFile = Path.GetFullPath(solutionFile);
            if (string.Equals(normalizedSolutionFile, solutionFilePath, StringComparison.OrdinalIgnoreCase)) return true;

            solutionFilePath = normalizedSolutionFile;
            solutionDirectory = !string.IsNullOrWhiteSpace(directory) ? directory : Path.GetDirectoryName(normalizedSolutionFile);
            lastKnownWriteTimeUtc = DateTime.MinValue;
            lastKnownLength = -1;
            workspaceLocation = DocSetsWorkspaceLocation.ForSolution(solutionFilePath);
            var workspace = await workspaceStore.LoadAsync(workspaceLocation);
            workspaceManager = new DocSetsWorkspaceManager(workspaceLocation, workspace);
            currentDocument = null;
            lastSavedStateJson = "";
            activeDocSetDirectory = "";
            stateFilePath = "";
            storageDirectory = "";
            isSharedWorkspace = false;

            var activePath = workspaceManager.ResolveActiveDocSet();
            if (!string.IsNullOrWhiteSpace(activePath) && Directory.Exists(activePath))
            {
                try
                {
                    await OpenDocSetCoreAsync(activePath, false);
                }
                catch (Exception exception)
                {
                    DocSetsLog.Current.Error("Хранилище", "Не удалось открыть активный DocSet: " + activePath, exception);
                }
            }
            return true;
        }

        private async Task<bool> OpenDocSetCoreAsync(string directoryPath, bool saveWorkspace)
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var document = await documentRepository.OpenAsync(fullPath);
            if (await NormalizeEmbeddedImagesAsync(document.State, fullPath))
                await documentRepository.SaveAsync(document);
            currentDocument = document;
            ConfigureActiveDocument(document);
            lastSavedStateJson = SerializeState(document.State);
            workspaceManager.Open(fullPath, true);
            if (saveWorkspace) await workspaceStore.SaveAsync(workspaceLocation, workspaceManager.Workspace);
            RememberCurrentFileStamp();
            return true;
        }

        private async Task<bool> NormalizeEmbeddedImagesAsync(DocumentSetsState documentState,
            string docSetDirectory = null)
        {
            if (documentState == null) return false;
            var directory = string.IsNullOrWhiteSpace(docSetDirectory)
                ? activeDocSetDirectory : docSetDirectory;
            if (string.IsNullOrWhiteSpace(directory)) return false;

            var changed = false;
            foreach (var item in EnumerateItems(documentState.Sets))
            {
                var normalized = await assetStorage.ImportEmbeddedImagesAsync(directory, item.Content);
                if (string.Equals(item.Content ?? string.Empty, normalized, StringComparison.Ordinal)) continue;
                item.Content = normalized;
                changed = true;
            }
            return changed;
        }

        private static IEnumerable<DocumentItem> EnumerateItems(IEnumerable<DocumentItem> items)
        {
            foreach (var item in items ?? Enumerable.Empty<DocumentItem>())
            {
                if (item == null) continue;
                yield return item;
                foreach (var child in EnumerateItems(item.Children)) yield return child;
            }
        }

        private void ConfigureActiveDocument(DocSetDocument document)
        {
            activeDocSetDirectory = document.DirectoryPath;
            stateFilePath = Path.Combine(activeDocSetDirectory, DirectoryDocSetStore.ManifestFileName);
            sourceStatuses = sourceLocator.LocateAll(activeDocSetDirectory, document.Sources);
            var primarySource = sourceStatuses.FirstOrDefault(x => x.RootExists);
            storageDirectory = primarySource?.ResolvedRoot ?? activeDocSetDirectory;
            isSharedWorkspace = !IsPathInside(solutionDirectory, activeDocSetDirectory);
        }

        private WorkspaceInfo CreateWorkspaceInfo(string directoryPath)
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var directoryName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var name = directoryName.EndsWith(DirectoryDocSetStore.DirectorySuffix, StringComparison.OrdinalIgnoreCase)
                ? directoryName.Substring(0, directoryName.Length - DirectoryDocSetStore.DirectorySuffix.Length)
                : directoryName;
            return new WorkspaceInfo { Name = name, FullPath = fullPath, RelativePath = ToSolutionRelativePath(fullPath) };
        }

        private static bool IsPathInside(string parentPath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(candidatePath)) return false;
            var parent = AppendDirectorySeparator(Path.GetFullPath(parentPath));
            var candidate = AppendDirectorySeparator(Path.GetFullPath(candidatePath));
            return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateReadableId(string value)
        {
            var source = (value ?? "docset").Trim().ToLowerInvariant();
            var result = new System.Text.StringBuilder();
            var separator = false;
            foreach (var character in source)
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (separator && result.Length > 0) result.Append('-');
                    result.Append(character);
                    separator = false;
                }
                else separator = true;
            }
            return result.Length == 0 ? "docset" : result.ToString();
        }

        private string SolutionSettingsFilePath
        {
            get
            {
                var solutionName = Path.GetFileNameWithoutExtension(solutionFilePath) ?? "solution";
                return Path.Combine(solutionDirectory, ".vs", "DockSets", solutionName + ".json");
            }
        }

        public SolutionLocalState LoadSolutionState()
        {
            try
            {
                if (File.Exists(SolutionSettingsFilePath))
                {
                    var json = File.ReadAllText(SolutionSettingsFilePath);
                    return JsonConvert.DeserializeObject<SolutionLocalState>(json) ?? new SolutionLocalState();
                }

                var legacyPath = Path.ChangeExtension(SolutionSettingsFilePath, ".workspace");
                if (File.Exists(legacyPath))
                {
                    return new SolutionLocalState { Workspace = File.ReadAllText(legacyPath).Trim() };
                }
            }
            catch { }
            return new SolutionLocalState();
        }

        public void SaveSolutionState(SolutionLocalState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SolutionSettingsFilePath));
                File.WriteAllText(SolutionSettingsFilePath, JsonConvert.SerializeObject(state ?? new SolutionLocalState(), Formatting.Indented));
            }
            catch { }
        }

        private string ToSolutionRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(solutionDirectory)) return "";
            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(solutionDirectory));
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(Path.GetFullPath(fullPath))).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return fullPath; }
        }

        private void ClearSolutionState()
        {
            solutionDirectory = "";
            solutionFilePath = "";
            storageDirectory = "";
            stateFilePath = "";
            activeDocSetDirectory = "";
            currentDocument = null;
            lastSavedStateJson = "";
            workspaceLocation = null;
            workspaceManager = null;
            sourceStatuses = Array.Empty<CodeSourceStatus>();
            isSharedWorkspace = false;
            lastKnownWriteTimeUtc = DateTime.MinValue;
            lastKnownLength = -1;
        }

        private string ToRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(storageDirectory) ||
                string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath ?? "";
            }

            try
            {
                var storageUri = new Uri(AppendDirectorySeparator(storageDirectory));
                var fileUri = new Uri(Path.GetFullPath(fullPath));

                return Uri.UnescapeDataString(
                        storageUri
                            .MakeRelativeUri(fileUri)
                            .ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return fullPath ?? "";
            }
        }

        private string ToSourceRelativePath(string fullPath, out string sourceId)
        {
            var source = sourceLocator.FindForFile(sourceStatuses, fullPath);
            var defaultSource = sourceLocator.GetDefault(sourceStatuses);
            sourceId = source == null || ReferenceEquals(source, defaultSource)
                ? ""
                : source.Source.Id ?? "";
            return source == null
                ? ToRelativePath(fullPath)
                : sourceLocator.MakeRelativePath(source, fullPath);
        }

        public string ToFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                string.IsNullOrWhiteSpace(storageDirectory))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(storageDirectory, path));
        }

        internal string ToFullPath(DocumentItem item)
        {
            return item == null
                ? ""
                : sourceLocator.ResolveItemPath(sourceStatuses, item.SourceId, item.Path, storageDirectory);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private static string SerializeState(DocumentSetsState state)
        {
            return state == null ? "" : JsonConvert.SerializeObject(state, Formatting.None);
        }
    }
}
