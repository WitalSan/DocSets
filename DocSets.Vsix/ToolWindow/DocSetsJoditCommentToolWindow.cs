using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace DocSets
{
    [Guid("D61718DA-9B40-4E21-A944-4774B86D6E02")]
    internal sealed class DocSetsJoditCommentToolWindow : ToolWindowPane
    {
        private static DocSetsViewModel currentViewModel;
        private static DocSetsWinFormsControl currentSource;
        private readonly DocSetsJoditCommentWindowControl control =
            new DocSetsJoditCommentWindowControl();

        public DocSetsJoditCommentToolWindow() : base(null)
        {
            Caption = "DocSets Заметка — Jodit (HTML)";
            var host = new WindowsFormsHost { Child = control };
            host.LostKeyboardFocus += (_, __) => _ = control.CommitPendingEditAsync();
            Content = host;
            _ = AttachCurrentContextAsync();
        }

        internal static void RegisterContext(
            AsyncPackage package, DocSetsViewModel viewModel, DocSetsWinFormsControl source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            currentViewModel = viewModel;
            currentSource = source;

            var pane = package?.FindToolWindow(
                typeof(DocSetsJoditCommentToolWindow), 0, false)
                as DocSetsJoditCommentToolWindow;
            if (pane != null) _ = pane.AttachCurrentContextAsync();
        }

        internal static void UnregisterContext(DocSetsWinFormsControl source)
        {
            if (!ReferenceEquals(currentSource, source)) return;
            currentViewModel = null;
            currentSource = null;
        }

        private async Task AttachCurrentContextAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var viewModel = currentViewModel;
            var source = currentSource;
            if (viewModel == null || source == null || source.IsDisposed) return;
            await control.AttachAsync(
                viewModel, source, viewModel.ResolvePin(source.CurrentCommentItem) ?? source.CurrentCommentItem);
        }

        internal static async Task ShowAsync(
            AsyncPackage package, DocSetsViewModel viewModel, DocSetsWinFormsControl source)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (package == null || viewModel == null || source == null) return;
            var item = viewModel.ResolvePin(source.CurrentCommentItem) ?? source.CurrentCommentItem;
            if (item == null) return;
            if (item.ContentFormat != ContentFormat.Html)
            {
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    MessageBox.Show(
                        "Эта заметка хранится в Markdown. Jodit её не преобразует.\r\n\r\n" +
                        "Выберите HTML как формат новых заметок и создайте новую закладку.",
                        "DocSets — Jodit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(
                        "Заметка пуста. Назначить ей формат HTML и открыть в Jodit?\r\n\r\n" +
                        "Формат можно безопасно изменить только пока содержимое пустое.",
                        "DocSets — Jodit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                await viewModel.ChangeEmptyContentFormatAsync(item, ContentFormat.Html);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                source.NotifyCommentSaved(item, null);
            }

            var pane = package.FindToolWindow(typeof(DocSetsJoditCommentToolWindow), 0, true)
                as DocSetsJoditCommentToolWindow;
            if (pane == null || pane.Frame == null)
                throw new InvalidOperationException("Не удалось создать экспериментальное окно Jodit.");
            currentViewModel = viewModel;
            currentSource = source;
            await pane.control.AttachAsync(viewModel, source, item);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)pane.Frame).Show());
            pane.control.BeginInvoke(new Action(pane.control.FocusEditor));
        }
    }
}
