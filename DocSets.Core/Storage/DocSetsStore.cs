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

        private const string LegacyWorkspaceFileName = "DocSets.workspace.json";
        private const string WorkspaceSearchPattern = "*.docsets.json";

        private string solutionDirectory = "";
        private string solutionFilePath = "";
        private string storageDirectory = "";
        private string stateFilePath = "";
        private bool isSharedWorkspace;
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        private DateTime lastKnownWriteTimeUtc = DateTime.MinValue;
        private long lastKnownLength = -1;

        public DocSetsStore(AsyncPackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            roslyn = new RoslynBookmarkResolver(package);
        }

        public string SolutionDirectory => solutionDirectory;

        public string SolutionFilePath => solutionFilePath;

        public string StorageDirectory => storageDirectory;

        public bool IsSharedWorkspace => isSharedWorkspace;

        public string StateFilePath => stateFilePath;

        public string CurrentWorkspaceRelativePath => ToSolutionRelativePath(stateFilePath);

        public async Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync()
        {
            if (!await EnsureInitializedAsync()) return Array.Empty<WorkspaceInfo>();
            return DiscoverWorkspaces();
        }

        public async Task<bool> SelectWorkspaceAsync(string relativePath)
        {
            if (!await EnsureInitializedAsync() || string.IsNullOrWhiteSpace(relativePath)) return false;
            var fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, relativePath));
            if (!IsAllowedWorkspacePath(fullPath)) return false;
            stateFilePath = fullPath;
            storageDirectory = Path.GetDirectoryName(fullPath);
            isSharedWorkspace = !string.Equals(storageDirectory, solutionDirectory, StringComparison.OrdinalIgnoreCase);
            lastKnownWriteTimeUtc = DateTime.MinValue;
            lastKnownLength = -1;
            SaveActiveWorkspaceName(relativePath);
            return true;
        }

        public async Task<DocumentSetsState> LoadAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            if (!File.Exists(StateFilePath))
            {
                return new DocumentSetsState();
            }

            try
            {
                var json = File.ReadAllText(StateFilePath);
                var loaded = JsonConvert.DeserializeObject<DocumentSetsState>(json)
                             ?? new DocumentSetsState();
                RememberCurrentFileStamp();
                return loaded;
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

            if (!await EnsureInitializedAsync())
            {
                VsShellUtilities.ShowMessageBox(
                    package,
                    "Откройте solution (.sln), чтобы сохранить настройки DocSets.",
                    "DocSets",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return;
            }

            await saveGate.WaitAsync();
            try
            {
                Directory.CreateDirectory(storageDirectory);

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var tempFilePath = StateFilePath + ".tmp." + Guid.NewGuid().ToString("N");

                try
                {
                    File.WriteAllText(tempFilePath, json);

                    if (File.Exists(StateFilePath))
                    {
                        try
                        {
                            File.Replace(tempFilePath, StateFilePath, null);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Copy(tempFilePath, StateFilePath, true);
                            File.Delete(tempFilePath);
                        }
                        catch (IOException)
                        {
                            File.Copy(tempFilePath, StateFilePath, true);
                            File.Delete(tempFilePath);
                        }
                    }
                    else
                    {
                        File.Move(tempFilePath, StateFilePath);
                    }

                    RememberCurrentFileStamp();
                }
                finally
                {
                    if (File.Exists(tempFilePath))
                    {
                        try { File.Delete(tempFilePath); } catch { }
                    }
                }
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

            return await roslyn.CreateBookmarkFromActiveDocumentAsync(
                StorageDirectory,
                ToRelativePath);
        }


        public async Task<DocumentItem> CreateClassBookmarkFromActiveDocumentAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            return await roslyn.CreateClassBookmarkFromActiveDocumentAsync(
                StorageDirectory,
                ToRelativePath);
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

            var fullPath = ToFullPath(item.Path);

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
                ToFullPath(item.Path),
                Math.Max(1, item.Line),
                Math.Max(1, item.Column),
                cancellationToken);
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

            var savedRelativePath = LoadActiveWorkspaceName();
            if (!string.IsNullOrWhiteSpace(savedRelativePath))
            {
                var savedFullPath = Path.GetFullPath(Path.Combine(solutionDirectory, savedRelativePath));
                if (IsAllowedWorkspacePath(savedFullPath) && File.Exists(savedFullPath))
                {
                    SetWorkspacePath(savedFullPath);
                    return true;
                }
            }

            var workspaces = DiscoverWorkspaces();
            var solutionName = Path.GetFileNameWithoutExtension(normalizedSolutionFile);
            var preferred = workspaces.FirstOrDefault(x => string.Equals(x.Name, solutionName, StringComparison.OrdinalIgnoreCase))
                            ?? workspaces.FirstOrDefault();
            var fullPath = preferred?.FullPath ?? Path.Combine(solutionDirectory, solutionName + ".docsets.json");
            SetWorkspacePath(fullPath);
            SaveActiveWorkspaceName(ToSolutionRelativePath(fullPath));
            return true;
        }

        private void SetWorkspacePath(string fullPath)
        {
            stateFilePath = Path.GetFullPath(fullPath);
            storageDirectory = Path.GetDirectoryName(stateFilePath);
            isSharedWorkspace = !string.Equals(storageDirectory, solutionDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<WorkspaceInfo> DiscoverWorkspaces()
        {
            var result = new List<WorkspaceInfo>();
            for (var __dir = solutionDirectory; !string.IsNullOrEmpty(__dir); __dir = Directory.GetParent(__dir)?.FullName)
            {
                AddWorkspacesFromDirectory(result, __dir);
            }
            //AddWorkspacesFromDirectory(result, solutionDirectory);
            //AddWorkspacesFromDirectory(result, Directory.GetParent(solutionDirectory)?.FullName);
            return result
                .GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void AddWorkspacesFromDirectory(ICollection<WorkspaceInfo> result, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            IEnumerable<string> files = Directory.EnumerateFiles(directory, WorkspaceSearchPattern, SearchOption.TopDirectoryOnly);
            var legacy = Path.Combine(directory, LegacyWorkspaceFileName);
            if (File.Exists(legacy)) files = files.Concat(new[] { legacy });
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var name = fileName.EndsWith(".docsets.json", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - ".docsets.json".Length)
                    : Path.GetFileNameWithoutExtension(fileName);
                result.Add(new WorkspaceInfo { Name = name, FullPath = Path.GetFullPath(file), RelativePath = ToSolutionRelativePath(file) });
            }
        }

        private bool IsAllowedWorkspacePath(string fullPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            var parent = Directory.GetParent(solutionDirectory)?.FullName;
            return solutionDirectory.StartsWith(directory);
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

        private string LoadActiveWorkspaceName() => LoadSolutionState().Workspace ?? "";

        private void SaveActiveWorkspaceName(string relativePath)
        {
            var state = LoadSolutionState();
            state.Workspace = relativePath ?? "";
            SaveSolutionState(state);
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

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
