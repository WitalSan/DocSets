using System;
using System.Threading;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Формирование Preview по текущему состоянию кода Visual Studio через Roslyn.
    /// </summary>
    internal sealed class VisualStudioPreviewService : IPreviewService
    {
        private readonly RoslynBookmarkResolver _roslyn;
        private readonly Func<Task<bool>> _ensureInitialized;
        private readonly Func<DocumentItem, string> _pathResolver;

        public VisualStudioPreviewService(
            RoslynBookmarkResolver roslyn,
            Func<Task<bool>> ensureInitialized,
            Func<DocumentItem, string> pathResolver)
        {
            _roslyn = roslyn ?? throw new ArgumentNullException(nameof(roslyn));
            _ensureInitialized = ensureInitialized ?? throw new ArgumentNullException(nameof(ensureInitialized));
            _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        }

        public async Task<string> GetPreviewAsync(
            DocumentItem item,
            CancellationToken cancellationToken)
        {
            if (item == null || item.NodeType == NodeType.Folder || string.IsNullOrWhiteSpace(item.Path))
            {
                return string.Empty;
            }

            if (!await _ensureInitialized()) return string.Empty;

            return await _roslyn.GetLivePreviewAsync(
                _pathResolver(item),
                Math.Max(1, item.Line),
                Math.Max(1, item.Column),
                cancellationToken);
        }
    }
}
