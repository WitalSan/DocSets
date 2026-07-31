using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Переход к файлам и символам средствами Visual Studio и Roslyn.
    /// </summary>
    internal sealed class VisualStudioNavigationService : INavigationService
    {
        private readonly AsyncPackage _package;
        private readonly RoslynBookmarkResolver _roslyn;
        private readonly Func<DocumentItem, string> _pathResolver;

        public VisualStudioNavigationService(
            AsyncPackage package,
            RoslynBookmarkResolver roslyn,
            Func<DocumentItem, string> pathResolver)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _roslyn = roslyn ?? throw new ArgumentNullException(nameof(roslyn));
            _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        }

        public async Task OpenBookmarkAsync(DocumentItem item)
        {
            if (item == null) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (item.Type != BookmarkType.File && await _roslyn.TryOpenBookmarkBySymbolAsync(item))
            {
                return;
            }

            var fullPath = _pathResolver(item);
            if (!File.Exists(fullPath))
            {
                VsShellUtilities.ShowMessageBox(
                    _package,
                    $"Файл не найден:{Environment.NewLine}{fullPath}",
                    "DocSets",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            await _roslyn.OpenFileAtAsync(
                fullPath,
                Math.Max(1, item.Line),
                Math.Max(1, item.Column));
            await _roslyn.RestoreEditorStateAsync(item, Math.Max(1, item.Line));
        }

        public Task<bool> OpenSymbolAsync(string symbol, string project)
        {
            return _roslyn.TryOpenSymbolAsync(symbol, project);
        }

        public Task OpenUrlAsync(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                VsShellUtilities.OpenSystemBrowser(uri.AbsoluteUri);
            return Task.CompletedTask;
        }
    }
}
