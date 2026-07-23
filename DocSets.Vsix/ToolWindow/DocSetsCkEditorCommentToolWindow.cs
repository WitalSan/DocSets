using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace DocSets
{
    [Guid("B3E1DD4B-11B2-4D8A-A1B4-6EC616BAA8E1")]
    internal sealed class DocSetsCkEditorCommentToolWindow : ToolWindowPane
    {
        private static DocSetsViewModel currentViewModel;
        private static DocSetsWinFormsControl currentSource;
        private readonly DocSetsCkEditorCommentWindowControl control =
            new DocSetsCkEditorCommentWindowControl();

        public DocSetsCkEditorCommentToolWindow() : base(null)
        {
            Caption = "DocSets Заметка — CKEditor (HTML)";
            var host = new WindowsFormsHost { Child = control };
            host.LostKeyboardFocus += (_, __) => _ = control.CommitPendingEditAsync();
            Content = host;
            _ = AttachCurrentContextAsync();
        }

        /// <summary>
        /// Регистрирует текущее окно DocSets как источник выделения. Visual Studio может
        /// восстановить CKEditor из сохранённой раскладки без нажатия кнопки EC, поэтому
        /// связь устанавливается независимо от команды открытия окна.
        /// </summary>
        internal static void RegisterContext(
            AsyncPackage package, DocSetsViewModel viewModel, DocSetsWinFormsControl source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            currentViewModel = viewModel;
            currentSource = source;

            var pane = package?.FindToolWindow(
                typeof(DocSetsCkEditorCommentToolWindow), 0, false)
                as DocSetsCkEditorCommentToolWindow;
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
                        "Эта заметка хранится в Markdown. CKEditor её не преобразует.\r\n\r\n" +
                        "Выберите HTML как формат новых заметок и создайте новую закладку.",
                        "DocSets — CKEditor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (MessageBox.Show(
                        "Заметка пуста. Назначить ей формат HTML и открыть в CKEditor?\r\n\r\n" +
                        "Формат можно безопасно изменить только пока содержимое пустое.",
                        "DocSets — CKEditor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                await viewModel.ChangeEmptyContentFormatAsync(item, ContentFormat.Html);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                source.NotifyCommentSaved(item, null);
            }

            var pane = package.FindToolWindow(typeof(DocSetsCkEditorCommentToolWindow), 0, true)
                as DocSetsCkEditorCommentToolWindow;
            if (pane == null || pane.Frame == null)
                throw new InvalidOperationException("Не удалось создать экспериментальное окно CKEditor.");
            currentViewModel = viewModel;
            currentSource = source;
            await pane.control.AttachAsync(viewModel, source, item);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(((IVsWindowFrame)pane.Frame).Show());
            pane.control.BeginInvoke(new Action(pane.control.FocusEditor));
        }
    }
}
