using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DocSets
{
    public sealed class DocSetWorkspaceService : IDocSetWorkspaceService
    {
        private readonly ISolutionContextService _solutionContext;

        private string _solutionDirectory = "";
        private string _solutionFilePath = "";
        private string _storageDirectory = "";
        private string _stateFilePath = "";
        private bool _isSharedWorkspace;
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);
        private DateTime _lastKnownWriteTimeUtc = DateTime.MinValue;
        private long _lastKnownLength = -1;
        private string _lastSavedStateJson = "";
        private readonly DirectoryDocSetStore _directoryStore = new DirectoryDocSetStore();
        private readonly DocSetDocumentRepository _documentRepository = new DocSetDocumentRepository();
        private readonly JsonDocSetsWorkspaceStore _workspaceStore = new JsonDocSetsWorkspaceStore();
        private DocSetsWorkspaceLocation _workspaceLocation;
        private DocSetsWorkspaceManager _workspaceManager;
        private DocSetDocument _currentDocument;
        private string _activeDocSetDirectory = "";
        private IReadOnlyList<CodeSourceStatus> _sourceStatuses = Array.Empty<CodeSourceStatus>();
        private readonly CodeSourceLocator _sourceLocator = new CodeSourceLocator();
        private readonly AssetStorageService _assetStorage = new AssetStorageService();

        public DocSetWorkspaceService(ISolutionContextService solutionContext)
        {
            _solutionContext = solutionContext ?? throw new ArgumentNullException(nameof(solutionContext));
        }

        public string StorageDirectory => _storageDirectory;

        public bool IsSharedWorkspace => _isSharedWorkspace;

        public string StateFilePath => _stateFilePath;
        public bool HasOpenDocSet => _currentDocument != null;
        public string AssetDirectory => string.IsNullOrWhiteSpace(_activeDocSetDirectory)
            ? "" : Path.Combine(_activeDocSetDirectory, "assets");

        public string ActiveDocSetDirectory => _activeDocSetDirectory;

        public string ActiveDocSetName => _currentDocument?.Manifest?.Name ?? "";

        public string CurrentWorkspaceRelativePath => ToSolutionRelativePath(_activeDocSetDirectory);

        public SourceReferenceContext CurrentSourceContext
            => SourceReferenceContext.Create(_sourceStatuses, _sourceLocator);

        public Task<string> SaveImageAssetAsync(byte[] content, string mimeType, string originalName)
        {
            if (string.IsNullOrWhiteSpace(_activeDocSetDirectory))
                throw new InvalidOperationException("DocSet не открыт.");
            return _assetStorage.SaveImageAsync(_activeDocSetDirectory, content, mimeType, originalName);
        }

        public Task<string> SaveFileAssetAsync(byte[] content, string originalName)
        {
            if (string.IsNullOrWhiteSpace(_activeDocSetDirectory))
                throw new InvalidOperationException("DocSet не открыт.");
            return _assetStorage.SaveFileAsync(_activeDocSetDirectory, content, originalName);
        }

        public Task<string> NormalizeCommentAssetsAsync(string markdown,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_activeDocSetDirectory))
                throw new InvalidOperationException("DocSet не открыт.");
            return _assetStorage.ImportEmbeddedImagesAsync(
                _activeDocSetDirectory, markdown, cancellationToken);
        }

        public IReadOnlyList<string> FindAssetReferences(string markdown)
            => _assetStorage.FindReferences(markdown);

        public byte[] ReadAsset(string assetReference)
            => _assetStorage.Read(_activeDocSetDirectory, assetReference);

        public string GetAssetMimeType(string assetReference)
            => _assetStorage.GetMimeType(assetReference);

        public async Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync()
        {
            if (!await EnsureInitializedAsync()) return Array.Empty<WorkspaceInfo>();
            return _workspaceManager.ResolveOpenDocSets()
                .Select(CreateWorkspaceInfo)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<bool> SelectWorkspaceAsync(string relativePath)
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(relativePath)) return false;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(_solutionDirectory, relativePath));
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
            await _directoryStore.CreateAsync(fullPath, CreateReadableId(displayName), displayName);
            return await OpenDocSetCoreAsync(fullPath, true);
        }

        public async Task<bool> CloseActiveDocSetAsync()
        {
            if (!await EnsureInitializedAsync() || _workspaceManager == null ||
                string.IsNullOrWhiteSpace(_activeDocSetDirectory))
                return false;

            if (!_workspaceManager.Close(_activeDocSetDirectory)) return false;
            await _workspaceStore.SaveAsync(_workspaceLocation, _workspaceManager.Workspace);
            _currentDocument = null;
            _lastSavedStateJson = "";
            _activeDocSetDirectory = "";
            _stateFilePath = "";
            _storageDirectory = "";
            _sourceStatuses = Array.Empty<CodeSourceStatus>();

            var next = _workspaceManager.ResolveActiveDocSet();
            if (!string.IsNullOrWhiteSpace(next) && Directory.Exists(next))
                await OpenDocSetCoreAsync(next, false);
            return true;
        }

        public async Task<DocumentSetsState> LoadAsync(bool forceReload = false)
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_activeDocSetDirectory)) return null;

            // Восстановление рабочего пространства и команды открытия уже загрузили
            // документ в OpenDocSetCoreAsync. Повторно читаем его только при обнаружении
            // внешнего изменения на диске.
            if (_currentDocument != null && !forceReload)
            {
                return _currentDocument.State;
            }

            try
            {
                _currentDocument = await _documentRepository.OpenAsync(_activeDocSetDirectory);
                ConfigureActiveDocument(_currentDocument);
                _lastSavedStateJson = SerializeState(_currentDocument.State);
                RememberCurrentFileStamp();
                return _currentDocument.State;
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

            if (!await EnsureInitializedAsync() || _currentDocument == null)
            {
                return;
            }

            await _saveGate.WaitAsync();
            try
            {
                await NormalizeEmbeddedImagesAsync(state);
                var stateJson = SerializeState(state);
                if (string.Equals(stateJson, _lastSavedStateJson, StringComparison.Ordinal))
                {
                    return;
                }

                _currentDocument.ReplaceState(state);
                await _documentRepository.SaveAsync(_currentDocument);
                _lastSavedStateJson = SerializeState(state);
                RememberCurrentFileStamp();
            }
            finally
            {
                _saveGate.Release();
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
                return _lastKnownWriteTimeUtc != DateTime.MinValue || _lastKnownLength >= 0;
            }

            try
            {
                var info = new FileInfo(StateFilePath);
                return info.LastWriteTimeUtc != _lastKnownWriteTimeUtc ||
                       info.Length != _lastKnownLength;
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
                _lastKnownWriteTimeUtc = DateTime.MinValue;
                _lastKnownLength = -1;
                return;
            }

            try
            {
                var info = new FileInfo(StateFilePath);
                _lastKnownWriteTimeUtc = info.LastWriteTimeUtc;
                _lastKnownLength = info.Length;
            }
            catch
            {
                // Оставляем предыдущую метку и повторяем проверку на следующем тике.
            }
        }

        public async Task<bool> EnsureInitializedAsync()
        {
            var context = await _solutionContext.GetCurrentAsync();
            if (context == null || !context.IsAvailable) { ClearSolutionState(); return false; }

            var normalizedSolutionFile = Path.GetFullPath(context.FilePath);
            if (string.Equals(normalizedSolutionFile, _solutionFilePath, StringComparison.OrdinalIgnoreCase)) return true;

            _solutionFilePath = normalizedSolutionFile;
            _solutionDirectory = context.Directory;
            _lastKnownWriteTimeUtc = DateTime.MinValue;
            _lastKnownLength = -1;
            _workspaceLocation = DocSetsWorkspaceLocation.ForSolution(_solutionFilePath);
            var workspace = await _workspaceStore.LoadAsync(_workspaceLocation);
            _workspaceManager = new DocSetsWorkspaceManager(_workspaceLocation, workspace);
            _currentDocument = null;
            _lastSavedStateJson = "";
            _activeDocSetDirectory = "";
            _stateFilePath = "";
            _storageDirectory = "";
            _isSharedWorkspace = false;

            var activePath = _workspaceManager.ResolveActiveDocSet();
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
            var document = await _documentRepository.OpenAsync(fullPath);
            if (await NormalizeEmbeddedImagesAsync(document.State, fullPath))
                await _documentRepository.SaveAsync(document);
            _currentDocument = document;
            ConfigureActiveDocument(document);
            _lastSavedStateJson = SerializeState(document.State);
            _workspaceManager.Open(fullPath, true);
            if (saveWorkspace) await _workspaceStore.SaveAsync(_workspaceLocation, _workspaceManager.Workspace);
            RememberCurrentFileStamp();
            return true;
        }

        private async Task<bool> NormalizeEmbeddedImagesAsync(DocumentSetsState documentState,
            string docSetDirectory = null)
        {
            if (documentState == null) return false;
            var directory = string.IsNullOrWhiteSpace(docSetDirectory)
                ? _activeDocSetDirectory : docSetDirectory;
            if (string.IsNullOrWhiteSpace(directory)) return false;

            var changed = false;
            foreach (var item in EnumerateItems(documentState.Sets))
            {
                var normalized = await _assetStorage.ImportEmbeddedImagesAsync(directory, item.Content);
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
            _activeDocSetDirectory = document.DirectoryPath;
            _stateFilePath = Path.Combine(_activeDocSetDirectory, DirectoryDocSetStore.ManifestFileName);
            _sourceStatuses = _sourceLocator.LocateAll(_activeDocSetDirectory, document.Sources);
            var primarySource = _sourceStatuses.FirstOrDefault(x => x.RootExists);
            _storageDirectory = primarySource?.ResolvedRoot ?? _activeDocSetDirectory;
            _isSharedWorkspace = !IsPathInside(_solutionDirectory, _activeDocSetDirectory);
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
                var solutionName = Path.GetFileNameWithoutExtension(_solutionFilePath) ?? "solution";
                return Path.Combine(_solutionDirectory, ".vs", "DockSets", solutionName + ".json");
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
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(_solutionDirectory)) return "";
            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(_solutionDirectory));
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(Path.GetFullPath(fullPath))).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch { return fullPath; }
        }

        private void ClearSolutionState()
        {
            _solutionDirectory = "";
            _solutionFilePath = "";
            _storageDirectory = "";
            _stateFilePath = "";
            _activeDocSetDirectory = "";
            _currentDocument = null;
            _lastSavedStateJson = "";
            _workspaceLocation = null;
            _workspaceManager = null;
            _sourceStatuses = Array.Empty<CodeSourceStatus>();
            _isSharedWorkspace = false;
            _lastKnownWriteTimeUtc = DateTime.MinValue;
            _lastKnownLength = -1;
        }

        private string ToRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(_storageDirectory) ||
                string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath ?? "";
            }

            try
            {
                var storageUri = new Uri(AppendDirectorySeparator(_storageDirectory));
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

        public string MakeSourceRelativePath(string fullPath, out string sourceId)
        {
            var source = _sourceLocator.FindForFile(_sourceStatuses, fullPath);
            var defaultSource = _sourceLocator.GetDefault(_sourceStatuses);
            sourceId = source == null || ReferenceEquals(source, defaultSource)
                ? ""
                : source.Source.Id ?? "";
            return source == null
                ? ToRelativePath(fullPath)
                : _sourceLocator.MakeRelativePath(source, fullPath);
        }

        public string ToFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                string.IsNullOrWhiteSpace(_storageDirectory))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(_storageDirectory, path));
        }

        public string ResolvePath(DocumentItem item)
        {
            return item == null
                ? ""
                : _sourceLocator.ResolveItemPath(_sourceStatuses, item.SourceId, item.Path, _storageDirectory);
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
