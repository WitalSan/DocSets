using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class DocSetsWinFormsControl
    {
        internal DocumentItem CurrentCommentItem { get; set; }
    }

    // Модульные тесты панели не запускают WebView2 и Visual Studio shell.
    internal sealed class DocSetsJoditCommentWindowControl : UserControl
    {
        internal Task AttachAsync(
            DocSetsViewModel viewModel, DocSetsWinFormsControl owner, DocumentItem item)
            => Task.CompletedTask;

        internal Task CommitPendingEditAsync() => Task.CompletedTask;

        internal void FocusEditor() => Focus();

        internal void ShowSearchResult(int start, int length, int occurrenceIndex)
        {
        }
    }
}
