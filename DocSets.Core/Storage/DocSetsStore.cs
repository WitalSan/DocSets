using System;
using System.IO;
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
        private string stateFilePath = "";

        public DocSetsStore(AsyncPackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            roslyn = new RoslynBookmarkResolver(package);
        }

        public string SolutionDirectory => solutionDirectory;

        public string SolutionFilePath => solutionFilePath;

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
                return JsonConvert.DeserializeObject<DocumentSetsState>(json)
                       ?? new DocumentSetsState();
            }
            catch
            {
                return new DocumentSetsState();
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
                    "Откройте solution (.sln), чтобы сохранить настройки DocSets рядом с ним.",
                    "DocSets",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return;
            }

            Directory.CreateDirectory(solutionDirectory);

            var json = JsonConvert.SerializeObject(state, Formatting.Indented);
            File.WriteAllText(StateFilePath, json);
        }

        public async Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync()
        {
            if (!await EnsureInitializedAsync())
            {
                return null;
            }

            return await roslyn.CreateBookmarkFromActiveDocumentAsync(
                SolutionDirectory,
                ToRelativePath);
        }

        public async Task OpenBookmarkAsync(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (await roslyn.TryOpenBookmarkBySymbolAsync(item))
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

            solutionDirectory = !string.IsNullOrWhiteSpace(directory)
                ? directory
                : Path.GetDirectoryName(normalizedSolutionFile);

            var solutionName = Path.GetFileNameWithoutExtension(normalizedSolutionFile);
            stateFilePath = Path.Combine(solutionDirectory, $"{solutionName}.docsets.json");

            return true;
        }

        private void ClearSolutionState()
        {
            solutionDirectory = "";
            solutionFilePath = "";
            stateFilePath = "";
        }

        private string ToRelativePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(solutionDirectory) ||
                string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath ?? "";
            }

            var solutionUri = new Uri(AppendDirectorySeparator(solutionDirectory));
            var fileUri = new Uri(fullPath);

            return Uri.UnescapeDataString(
                    solutionUri
                        .MakeRelativeUri(fileUri)
                        .ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private string ToFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                string.IsNullOrWhiteSpace(solutionDirectory))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(solutionDirectory, path));
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