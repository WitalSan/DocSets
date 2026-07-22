using Microsoft.VisualStudio.Shell;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Отдельная экспериментальная сессия Milkdown. Рабочее окно TOAST имеет
    /// независимый жизненный цикл и этим классом не затрагивается.
    /// </summary>
    internal sealed class DocSetsMilkdownCommentWindowControl : UserControl
    {
        private readonly MilkdownCommentControl editor = new MilkdownCommentControl();
        private readonly CheckBox followSelection = new CheckBox();
        private readonly Button saveButton = new Button();
        private readonly Label title = new Label();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer idleSaveTimer =
            new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        private DocSetsViewModel viewModel;
        private DocSetsWinFormsControl source;
        private DocumentItem item;
        private bool dirty;
        private bool switching;
        private long revision;

        public DocSetsMilkdownCommentWindowControl()
        {
            Dock = DockStyle.Fill;
            editor.ShowSaveToolbar = false;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 3,
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
            toolTip.SetToolTip(followSelection, "Следовать за выделением в дереве DocSets");
            saveButton.Image = SaveIconFactory.Create(this, 18);
            saveButton.Size = DpiService.Scale(this, new Size(32, 27));
            saveButton.Enabled = false;
            saveButton.Margin = new Padding(3, 0, 3, 0);
            toolTip.SetToolTip(saveButton, "Сохранить заметку (Ctrl+S)");
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
                if (switching) return;
                dirty = true;
                revision++;
                idleSaveTimer.Stop();
                idleSaveTimer.Start();
            };
            editor.EditingCompleted += async (_, __) => await SaveAsync();
            editor.SaveRequested += async (_, __) => await SaveAsync();
            editor.SaveStateChanged += enabled => saveButton.Enabled = enabled;
            saveButton.Click += async (_, __) => await SaveAsync(forceRead: true);
            editor.LinkActivated += target => _ = ActivateLinkAsync(target);
            editor.ImageInsertionRequested += async (data, mime, name, requestId) =>
            {
                if (viewModel == null) return;
                try
                {
                    var bytes = Convert.FromBase64String(data);
                    var assetReference = await viewModel.SaveImageAssetAsync(bytes, mime, name);
                    editor.InsertImage(assetReference, name, requestId);
                }
                catch (Exception exception)
                {
                    title.Text = "Не удалось сохранить изображение: " + exception.Message;
                }
            };
            editor.ExternalSymbolDropRequested += async text =>
            {
                if (viewModel == null) return;
                var symbol = await viewModel.GetActiveSymbolReferenceAsync(text);
                if (symbol == null || string.IsNullOrWhiteSpace(symbol.Symbol)) return;
                editor.InsertResolvedLink(new DocumentLink
                {
                    Kind = DocumentLinkKind.Symbol,
                    Caption = string.IsNullOrWhiteSpace(text) ? symbol.Name : text,
                    Target = symbol.Symbol,
                    Project = symbol.Project,
                    SourceId = symbol.SourceId
                });
            };
            Leave += async (_, __) => await SaveAsync(forceRead: true);
        }

        internal void Attach(DocSetsViewModel model, DocSetsWinFormsControl owner, DocumentItem selectedItem)
        {
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
            viewModel = model;
            editor.SetAssetDirectory(viewModel?.AssetDirectory);
            _ = SwitchItemAsync(selectedItem);
        }

        internal async Task ShowSearchResultAsync(
            DocumentItem selectedItem, int start, int length, int occurrenceIndex)
        {
            await SwitchItemAsync(selectedItem);
            var comment = item?.Content ?? string.Empty;
            if (!DocumentLinkService.TryResolveSearchHighlight(comment, start, length, occurrenceIndex,
                    out var visibleText, out var visibleOccurrence)) return;
            editor.HighlightSearchMatch(visibleText, visibleOccurrence);
        }
        internal Task CommitPendingEditAsync() => SaveAsync(forceRead: true);

        private void Source_CurrentCommentItemChanged(DocumentItem selectedItem)
        {
            if (followSelection.Checked) _ = SwitchItemAsync(selectedItem);
        }

        private void Source_CommentContentChanged(DocumentItem changedItem, object origin)
        {
            if (ReferenceEquals(origin, this) || dirty || item == null ||
                !ReferenceEquals(item, changedItem)) return;
            ReloadCurrentItem();
        }

        private void ReloadCurrentItem()
        {
            switching = true;
            try
            {
                editor.LoadComment(item?.Content ?? string.Empty);
                title.Text = item?.Name ?? "Заметка не выбрана";
                dirty = false;
            }
            finally { switching = false; }
        }

        private async Task SwitchItemAsync(DocumentItem selectedItem)
        {
            selectedItem = viewModel?.ResolvePin(selectedItem) ?? selectedItem;
            if (ReferenceEquals(item, selectedItem))
            {
                if (!dirty && !string.Equals(editor.CommentText, item?.Content ?? string.Empty,
                        StringComparison.Ordinal)) ReloadCurrentItem();
                return;
            }
            await SaveAsync();
            item = selectedItem;
            switching = true;
            try
            {
                editor.Enabled = item != null;
                editor.LoadComment(item?.Content ?? string.Empty);
                title.Text = item?.Name ?? "Заметка не выбрана";
                dirty = false;
            }
            finally { switching = false; }
        }

        private async Task SaveAsync(bool readEditor = true, bool forceRead = false)
        {
            idleSaveTimer.Stop();
            await saveGate.WaitAsync();
            try
            {
                var target = item;
                if (target == null || viewModel == null || (!dirty && !forceRead)) return;
                var savingRevision = revision;
                var editorValue = readEditor
                    ? await editor.GetCurrentCommentAsync()
                    : editor.CommentText ?? string.Empty;
                var value = await viewModel.NormalizeCommentAssetsAsync(editorValue);
                if (!string.Equals(target.Content ?? string.Empty, value, StringComparison.Ordinal))
                {
                    viewModel.CaptureUndoState("Изменение заметки в Milkdown", new[] { target });
                    target.Content = value;
                    viewModel.MarkBookmarkModified(target);
                    await viewModel.SaveAsync();
                    source?.NotifyCommentSaved(target, this);
                }
                if (savingRevision == revision)
                {
                    dirty = false;
                    editor.SetSaveEnabled(false);
                }
            }
            finally
            {
                saveGate.Release();
                if (dirty && !IsDisposed)
                {
                    idleSaveTimer.Stop();
                    idleSaveTimer.Start();
                }
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
                idleSaveTimer.Stop();
                if (dirty && item != null && viewModel != null)
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
