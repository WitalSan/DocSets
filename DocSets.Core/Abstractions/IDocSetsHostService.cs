using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Возможности внешней среды, необходимые прикладной модели DocSets.
    /// Visual Studio и самостоятельное приложение предоставляют разные реализации.
    /// </summary>
    public interface IDocSetsHostService
    {
        string CurrentSolutionName { get; }
        string SolutionDirectory { get; }
        string SolutionFilePath { get; }
        string StateFilePath { get; }
        string AssetDirectory { get; }
        string CurrentWorkspaceRelativePath { get; }
        bool IsSharedWorkspace { get; }
        bool HasOpenDocSet { get; }
        SourceReferenceContext CurrentSourceContext { get; }

        Task<IReadOnlyList<WorkspaceInfo>> GetWorkspacesAsync();
        Task<bool> SelectWorkspaceAsync(string relativePath);
        Task<bool> OpenDocSetAsync(string directoryPath);
        Task<bool> CreateDocSetAsync(string directoryPath, string name);
        Task<DocumentSetsState> LoadAsync(bool forceReload = false);
        Task SaveAsync(DocumentSetsState state);
        Task<bool> HasExternalChangesAsync();

        Task<string> GetLivePreviewAsync(DocumentItem item, CancellationToken cancellationToken);

        Task<string> SaveImageAssetAsync(byte[] content, string mimeType, string originalName);
        Task<string> NormalizeCommentAssetsAsync(string content, CancellationToken cancellationToken = default);
        IReadOnlyList<string> FindAssetReferences(string content);
        byte[] ReadAsset(string assetReference);
        string GetAssetMimeType(string assetReference);

        SolutionLocalState LoadSolutionState();
        void SaveSolutionState(SolutionLocalState state);
        string ToFullPath(string path);
    }

    /// <summary>
    /// Отслеживание перемещений закладок при редактировании исходных файлов.
    /// Вне IDE допустима реализация без операций.
    /// </summary>
    public interface IEditorTrackingService
    {
        Task TrackFromActiveDocumentAsync(DocumentItem item);
        Task TrackAfterOpenAsync(DocumentItem item);
        Task UpdateTrackedPositionsAsync(IEnumerable<DocumentItem> items);
    }
}
