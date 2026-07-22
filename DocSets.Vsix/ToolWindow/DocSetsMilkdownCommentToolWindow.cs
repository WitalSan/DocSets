using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;

namespace DocSets
{
    [Guid("8D93AE50-A39D-45E0-96AB-692C84BA1D86")]
    internal sealed class DocSetsMilkdownCommentToolWindow : ToolWindowPane
    {
        private readonly DocSetsMilkdownCommentWindowControl control =
            new DocSetsMilkdownCommentWindowControl();

        public DocSetsMilkdownCommentToolWindow() : base(null)
        {
            Caption = "DocSets Заметка — Milkdown";
            var host = new WindowsFormsHost { Child = control };
            host.LostKeyboardFocus += (_, __) => _ = control.CommitPendingEditAsync();
            Content = host;
        }

        internal static bool TryShowSearchResult(
            AsyncPackage package, DocumentItem item, int start, int length, int occurrenceIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || item == null) return false;
            var pane = package.FindToolWindow(typeof(DocSetsMilkdownCommentToolWindow), 0, false)
                as DocSetsMilkdownCommentToolWindow;
            if (pane?.Frame is not IVsWindowFrame frame) return false;
            if (frame.IsVisible() != Microsoft.VisualStudio.VSConstants.S_OK) return false;
            _ = pane.control.ShowSearchResultAsync(item, start, length, occurrenceIndex);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            return true;
        }

        internal static void Show(
            AsyncPackage package, DocSetsViewModel viewModel, DocSetsWinFormsControl source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || viewModel == null || source == null) return;
            var pane = package.FindToolWindow(typeof(DocSetsMilkdownCommentToolWindow), 0, true)
                as DocSetsMilkdownCommentToolWindow;
            if (pane == null || pane.Frame == null)
                throw new InvalidOperationException("Не удалось создать экспериментальное окно Milkdown.");
            pane.control.Attach(viewModel, source, source.CurrentCommentItem);
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)pane.Frame).Show());
        }
    }
}
