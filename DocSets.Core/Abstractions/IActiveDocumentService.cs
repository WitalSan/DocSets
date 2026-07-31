using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Получение документа и символа из активного редактора внешней среды.
    /// </summary>
    public interface IActiveDocumentService
    {
        Task<DocumentItem> CreateBookmarkAsync();
        Task<DocumentItem> CreateClassBookmarkAsync();
        Task<ActiveDocumentContext> GetContextAsync();
        Task<ActiveSymbolReference> GetSymbolReferenceAsync(string selectedText);
    }
}
