using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Получение текущего solution средствами Visual Studio.
    /// </summary>
    internal sealed class VisualStudioSolutionContextService : ISolutionContextService
    {
        private readonly AsyncPackage _package;
        private SolutionContext _current = SolutionContext.Unavailable;

        public VisualStudioSolutionContextService(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public SolutionContext Current => _current;

        public async Task<SolutionContext> GetCurrentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var solution = await _package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (solution == null)
            {
                return SetUnavailable();
            }

            solution.GetSolutionInfo(out var directory, out var solutionFile, out _);
            if (string.IsNullOrWhiteSpace(solutionFile))
            {
                return SetUnavailable();
            }

            var filePath = Path.GetFullPath(solutionFile);
            var solutionDirectory = !string.IsNullOrWhiteSpace(directory)
                ? Path.GetFullPath(directory)
                : Path.GetDirectoryName(filePath) ?? "";
            _current = new SolutionContext(
                true,
                Path.GetFileNameWithoutExtension(filePath) ?? "",
                solutionDirectory,
                filePath);
            return _current;
        }

        private SolutionContext SetUnavailable()
        {
            _current = SolutionContext.Unavailable;
            return _current;
        }
    }
}
