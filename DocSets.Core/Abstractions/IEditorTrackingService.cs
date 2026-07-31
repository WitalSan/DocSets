using System.Collections.Generic;
using System.Threading.Tasks;

namespace DocSets
{
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
