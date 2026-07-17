using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;

namespace DocSets
{
    [Guid("43F7BF20-5F14-4FC9-9640-BDF11E38EF48")]
    internal sealed class DocSetsCommentToolWindow : ToolWindowPane
    {
        private readonly DocSetsCommentWindowControl control = new DocSetsCommentWindowControl();

        public DocSetsCommentToolWindow() : base(null)
        {
            Caption = "DocSets Comment";
            Content = new WindowsFormsHost { Child = control };
        }

        internal static bool TryShowSearchResult(AsyncPackage package, DocumentItem item, int start, int length, int occurrenceIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || item == null) return false;
            var pane = package.FindToolWindow(typeof(DocSetsCommentToolWindow), 0, false) as DocSetsCommentToolWindow;
            if (pane?.Frame is not IVsWindowFrame frame) return false;
            if (frame.IsVisible() != Microsoft.VisualStudio.VSConstants.S_OK) return false;
            _ = pane.control.ShowSearchResultAsync(item, start, length, occurrenceIndex);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            return true;
        }
        internal static void Show(AsyncPackage package, DocSetsViewModel viewModel, DocSetsWinFormsControl source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || viewModel == null || source == null) return;
            var pane = package.FindToolWindow(typeof(DocSetsCommentToolWindow), 0, true) as DocSetsCommentToolWindow;
            if (pane == null || pane.Frame == null) throw new InvalidOperationException("Не удалось создать окно комментария DocSets.");
            pane.control.Attach(viewModel, source, source.CurrentCommentItem);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)pane.Frame).Show());
        }
    }
}