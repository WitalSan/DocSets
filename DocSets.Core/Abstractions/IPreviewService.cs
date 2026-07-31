using System.Threading;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Формирование актуального представления компонента исходного кода.
    /// </summary>
    public interface IPreviewService
    {
        Task<string> GetPreviewAsync(DocumentItem item, CancellationToken cancellationToken);
    }
}
