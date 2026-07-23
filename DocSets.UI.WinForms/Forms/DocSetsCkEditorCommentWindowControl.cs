using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Отдельная сессия HTML-редактора CKEditor. Markdown-заметки в этом окне
    /// не преобразуются и не редактируются.
    /// </summary>
    internal class DocSetsCkEditorCommentWindowControl : UserControl
    {
        private readonly CkEditorCommentControl editor;
        private readonly string editorName;
        private readonly CheckBox followSelection = new CheckBox();
        private readonly Button saveButton = new Button();
        private readonly Label title = new Label();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer idleSaveTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        private DocSetsViewModel viewModel;
        private DocSetsWinFormsControl source;
        private DocumentItem item;
        private bool dirty;
        private bool switching;
        private long revision;

        public DocSetsCkEditorCommentWindowControl()
            : this(new CkEditorCommentControl(), "CKEditor")
        {
        }

        protected DocSetsCkEditorCommentWindowControl(
            CkEditorCommentControl editorControl, string name)
        {
            editor = editorControl ?? throw new ArgumentNullException(nameof(editorControl));
            editorName = name ?? "HTML-редактор";
            Dock = DockStyle.Fill;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(3)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            followSelection.Appearance = Appearance.Button;
            followSelection.Text = "∞";
            followSelection.TextAlign = ContentAlignment.MiddleCenter;
            followSelection.Checked = true;
            followSelection.Size = DpiService.Scale(this, new Size(32, 27));
            toolTip.SetToolTip(followSelection, "Следовать за выделением");
            saveButton.Image = SaveIconFactory.Create(this, 18);
            saveButton.Size = DpiService.Scale(this, new Size(32, 27));
            saveButton.Enabled = false;
            toolTip.SetToolTip(saveButton, "Сохранить HTML-заметку (Ctrl+S)");
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.AutoEllipsis = true;
            title.Padding = new Padding(6, 0, 0, 0);
            bar.Controls.Add(followSelection, 0, 0);
            bar.Controls.Add(saveButton, 1, 0);
            bar.Controls.Add(title, 2, 0);
            root.Controls.Add(bar, 0, 0);
            root.Controls.Add(editor, 0, 1);
            Controls.Add(root);

            followSelection.CheckedChanged += async (_, __) =>
            {
                if (followSelection.Checked && source != null)
                    await SwitchItemAsync(source.CurrentCommentItem);
            };
            idleSaveTimer.Tick += async (_, __) =>
            {
                idleSaveTimer.Stop();
                await SaveAsync();
            };
            editor.CommentChanged += (_, __) =>
            {
                if (switching || item?.ContentFormat != ContentFormat.Html) return;
                dirty = true;
                revision++;
                saveButton.Enabled = true;
                idleSaveTimer.Stop();
                idleSaveTimer.Start();
            };
            editor.EditingCompleted += async (_, __) => await SaveAsync();
            editor.SaveRequested += async (_, __) => await SaveAsync(forceRead: true);
            editor.SaveStateChanged += enabled => saveButton.Enabled = enabled && item?.ContentFormat == ContentFormat.Html;
            saveButton.Click += async (_, __) => await SaveAsync(forceRead: true);
            editor.LinkActivated += target => _ = ActivateLinkAsync(target);
            editor.ImageInsertionRequested += async (data, mime, name, requestId) =>
            {
                if (viewModel == null) return;
                try
                {
                    var bytes = Convert.FromBase64String(data);
                    var assetReference = await viewModel.SaveImageAssetAsync(bytes, mime, name);
                    editor.CompleteImage(assetReference, requestId);
                }
                catch (Exception exception)
                {
                    editor.FailImage(requestId, exception.Message);
                    DocSetsLog.Current.Error("Изображения", "Не удалось сохранить изображение " + editorName + ".", exception);
                }
            };
            editor.ExternalSymbolDropRequested += async text =>
            {
                var link = await DocumentLinkService.ResolveDroppedSymbolAsync(viewModel, text);
                if (link != null) editor.InsertResolvedLink(link);
            };
            Leave += async (_, __) => await SaveAsync(forceRead: true);
        }

        internal async Task AttachAsync(
            DocSetsViewModel model, DocSetsWinFormsControl owner, DocumentItem selectedItem)
        {
            if (!ReferenceEquals(viewModel, model))
            {
                if (viewModel != null)
                    viewModel.UndoRedoStateRestored -= ViewModel_UndoRedoStateRestored;
                viewModel = model;
                if (viewModel != null)
                    viewModel.UndoRedoStateRestored += ViewModel_UndoRedoStateRestored;
            }
            if (!ReferenceEquals(source, owner))
            {
                if (source != null)
                {
                    source.CurrentCommentItemChanged -= Source_CurrentCommentItemChanged;
                    source.CommentContentChanged -= Source_CommentContentChanged;
                }
                source = owner;
                if (source != null)
                {
                    source.CurrentCommentItemChanged += Source_CurrentCommentItemChanged;
                    source.CommentContentChanged += Source_CommentContentChanged;
                }
            }
            editor.SetAssetDirectory(viewModel?.AssetDirectory);
            await SwitchItemAsync(selectedItem);
        }

        internal Task CommitPendingEditAsync() => SaveAsync(forceRead: true);

        internal void FocusEditor()
        {
            if (item?.ContentFormat != ContentFormat.Html) return;
            Select();
            Focus();
            editor.Enabled = true;
            editor.FocusEditor();
        }

        private void Source_CurrentCommentItemChanged(DocumentItem selectedItem)
        {
            if (followSelection.Checked) _ = SwitchItemAsync(selectedItem);
        }

        private void Source_CommentContentChanged(DocumentItem changedItem, object origin)
        {
            if (ReferenceEquals(origin, this) || dirty || item == null || !ReferenceEquals(item, changedItem)) return;
            ReloadCurrentItem();
        }

        private void ViewModel_UndoRedoStateRestored(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ViewModel_UndoRedoStateRestored(sender, e)));
                return;
            }

            // Undo/Redo восстанавливает всё дерево новыми экземплярами DocumentItem.
            // Отдельный редактор обязан отбросить старую ссылку и подключиться к
            // текущему экземпляру выбранной закладки.
            _ = SwitchItemAsync(source?.CurrentCommentItem);
        }

        private void ReloadCurrentItem()
        {
            switching = true;
            try
            {
                var html = item?.ContentFormat == ContentFormat.Html ? item.Content ?? string.Empty : string.Empty;
                editor.Enabled = item?.ContentFormat == ContentFormat.Html;
                editor.LoadComment(html);
                title.Text = GetTitle(item);
                dirty = false;
                saveButton.Enabled = false;
            }
            finally { switching = false; }
        }

        private async Task SwitchItemAsync(DocumentItem selectedItem)
        {
            selectedItem = viewModel?.ResolvePin(selectedItem) ?? selectedItem;
            if (ReferenceEquals(item, selectedItem))
            {
                if (!dirty && item?.ContentFormat == ContentFormat.Html &&
                    !string.Equals(editor.CommentText, item.Content ?? string.Empty, StringComparison.Ordinal))
                    ReloadCurrentItem();
                return;
            }
            await SaveAsync();
            item = selectedItem;
            ReloadCurrentItem();
        }

        private static string GetTitle(DocumentItem value)
        {
            if (value == null) return "Заметка не выбрана";
            return value.ContentFormat == ContentFormat.Html
                ? value.Name + "  [HTML]"
                : value.Name + "  [Markdown — откройте в TOAST]";
        }

        private async Task SaveAsync(bool forceRead = false, bool readEditor = true)
        {
            idleSaveTimer.Stop();
            await saveGate.WaitAsync();
            try
            {
                var target = item;
                if (target == null || target.ContentFormat != ContentFormat.Html || viewModel == null ||
                    (!dirty && !forceRead)) return;
                var savingRevision = revision;
                var editorValue = readEditor
                    ? await editor.GetCurrentCommentAsync()
                    : editor.CommentText ?? string.Empty;
                var value = await viewModel.NormalizeCommentAssetsAsync(editorValue);
                if (!string.Equals(target.Content ?? string.Empty, value, StringComparison.Ordinal))
                {
                    viewModel.CaptureUndoState("Изменение HTML-заметки в " + editorName, new[] { target });
                    target.Content = value;
                    viewModel.MarkBookmarkModified(target);
                    await viewModel.SaveAsync();
                    source?.NotifyCommentSaved(target, this);
                }
                if (savingRevision == revision)
                {
                    dirty = false;
                    editor.SetSaveEnabled(false);
                    saveButton.Enabled = false;
                }
            }
            finally
            {
                saveGate.Release();
                if (dirty && !IsDisposed) idleSaveTimer.Start();
            }
        }

        private async Task ActivateLinkAsync(string target)
        {
            if (viewModel == null || string.IsNullOrWhiteSpace(target)) return;
            var rendered = DocumentLinkService.Render("[link](" + target + ")");
            var link = rendered.Links.Count == 0 ? null : rendered.Links[0].Link;
            if (link == null) return;
            switch (link.Kind)
            {
                case DocumentLinkKind.Symbol:
                    await viewModel.OpenSymbolAsync(item, link.Target, link.Project);
                    break;
                case DocumentLinkKind.File:
                    await viewModel.OpenFileLinkAsync(link.Target, link.SourceId);
                    break;
                case DocumentLinkKind.Bookmark:
                    await viewModel.OpenBookmarkByIdAsync(link.Target);
                    break;
                case DocumentLinkKind.Url:
                    if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
                        VsShellUtilities.OpenSystemBrowser(uri.AbsoluteUri);
                    break;
            }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            followSelection.Size = DpiService.Scale(this, new Size(32, 27));
            saveButton.Size = DpiService.Scale(this, new Size(32, 27));
            var previous = saveButton.Image;
            saveButton.Image = SaveIconFactory.Create(this, 18);
            previous?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (source != null)
                {
                    source.CurrentCommentItemChanged -= Source_CurrentCommentItemChanged;
                    source.CommentContentChanged -= Source_CommentContentChanged;
                }
                if (viewModel != null)
                    viewModel.UndoRedoStateRestored -= ViewModel_UndoRedoStateRestored;
                idleSaveTimer.Stop();
                if (dirty && item?.ContentFormat == ContentFormat.Html && viewModel != null)
                    ThreadHelper.JoinableTaskFactory.Run(() => SaveAsync(readEditor: false));
                editor.Dispose();
                idleSaveTimer.Dispose();
                saveGate.Dispose();
                toolTip.Dispose();
                saveButton.Image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
