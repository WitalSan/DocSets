using System;
using System.Threading.Tasks;

namespace DocSets
{
    internal delegate string SourceRelativePathResolver(string fullPath, out string sourceId);

    /// <summary>
    /// Получение активного документа и символа средствами Visual Studio и Roslyn.
    /// </summary>
    internal sealed class VisualStudioActiveDocumentService : IActiveDocumentService
    {
        private readonly RoslynBookmarkResolver _roslyn;
        private readonly Func<Task<bool>> _ensureInitialized;
        private readonly Func<string> _storageDirectoryAccessor;
        private readonly Func<string> _solutionNameAccessor;
        private readonly SourceRelativePathResolver _relativePathResolver;

        public VisualStudioActiveDocumentService(
            RoslynBookmarkResolver roslyn,
            Func<Task<bool>> ensureInitialized,
            Func<string> storageDirectoryAccessor,
            Func<string> solutionNameAccessor,
            SourceRelativePathResolver relativePathResolver)
        {
            _roslyn = roslyn ?? throw new ArgumentNullException(nameof(roslyn));
            _ensureInitialized = ensureInitialized ?? throw new ArgumentNullException(nameof(ensureInitialized));
            _storageDirectoryAccessor = storageDirectoryAccessor ?? throw new ArgumentNullException(nameof(storageDirectoryAccessor));
            _solutionNameAccessor = solutionNameAccessor ?? throw new ArgumentNullException(nameof(solutionNameAccessor));
            _relativePathResolver = relativePathResolver ?? throw new ArgumentNullException(nameof(relativePathResolver));
        }

        public async Task<DocumentItem> CreateBookmarkAsync()
        {
            if (!await _ensureInitialized()) return null;

            var sourceId = string.Empty;
            var item = await _roslyn.CreateBookmarkFromActiveDocumentAsync(
                _storageDirectoryAccessor(),
                path => _relativePathResolver(path, out sourceId));
            if (item != null) item.SourceId = sourceId;
            return item;
        }

        public async Task<DocumentItem> CreateClassBookmarkAsync()
        {
            if (!await _ensureInitialized()) return null;

            var sourceId = string.Empty;
            var item = await _roslyn.CreateClassBookmarkFromActiveDocumentAsync(
                _storageDirectoryAccessor(),
                path => _relativePathResolver(path, out sourceId));
            if (item != null) item.SourceId = sourceId;
            return item;
        }

        public async Task<ActiveDocumentContext> GetContextAsync()
        {
            if (!await _ensureInitialized()) return null;

            var context = await _roslyn.GetActiveDocumentContextAsync();
            if (context != null) context.SolutionName = _solutionNameAccessor() ?? string.Empty;
            return context;
        }

        public async Task<ActiveSymbolReference> GetSymbolReferenceAsync(string selectedText)
        {
            var reference = await _roslyn.GetActiveSymbolReferenceAsync(selectedText);
            if (reference == null) return null;

            _relativePathResolver(reference.Path, out var sourceId);
            reference.SourceId = sourceId;
            return reference;
        }
    }
}
