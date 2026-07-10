using System;
using System.IO;
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

        private const string WorkspaceFileName = "DocSets.workspace.json";

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

        private async Task<bool> EnsureInitializedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var solution = await package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution == null)
            {
                ClearSolutionState();
                return false;
            }

            solution.GetSolutionInfo(
                out var directory,
                out var solutionFile,
                out _);

            if (string.IsNullOrWhiteSpace(solutionFile))
            {
                ClearSolutionState();
                return false;
            }

            var normalizedSolutionFile = Path.GetFullPath(solutionFile);

            if (string.Equals(
                    normalizedSolutionFile,
                    solutionFilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            solutionFilePath = normalizedSolutionFile;
            lastKnownWriteTimeUtc = DateTime.MinValue;
            lastKnownLength = -1;

            solutionDirectory = !string.IsNullOrWhiteSpace(directory)
                ? directory
                : Path.GetDirectoryName(normalizedSolutionFile);

            var workspaceFile = FindWorkspaceFile(solutionDirectory);
            if (!string.IsNullOrWhiteSpace(workspaceFile))
            {
                stateFilePath = workspaceFile;
                storageDirectory = Path.GetDirectoryName(workspaceFile);
                isSharedWorkspace = true;
                return true;
            }

            var solutionName = Path.GetFileNameWithoutExtension(normalizedSolutionFile);
            storageDirectory = solutionDirectory;
            stateFilePath = Path.Combine(solutionDirectory, $"{solutionName}.docsets.json");
            isSharedWorkspace = false;

            return true;
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

        private static string FindWorkspaceFile(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return "";
            }

            try
            {
                var current = new DirectoryInfo(directory);
                while (current != null)
                {
                    var candidate = Path.Combine(current.FullName, WorkspaceFileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    current = current.Parent;
                }
            }
            catch
            {
                // Keep the old per-solution behavior when the workspace search fails.
            }

            return "";
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