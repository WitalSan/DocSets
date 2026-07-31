using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Переход к файлам и символам во внешней среде.
    /// </summary>
    public interface INavigationService
    {
        Task OpenBookmarkAsync(DocumentItem item);
        Task<bool> OpenSymbolAsync(string symbol, string project);
        Task OpenUrlAsync(string url);
    }
}
