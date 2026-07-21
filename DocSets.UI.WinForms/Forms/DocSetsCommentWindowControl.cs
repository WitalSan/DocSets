using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class DocSetsCommentWindowControl : UserControl
    {
        private readonly ToastCommentControl editor = new ToastCommentControl();
        private readonly CheckBox followSelection = new CheckBox();
        private readonly Label title = new Label();
        private readonly ToolTip toolTip = new ToolTip();
        private DocSetsViewModel viewModel;
        private DocSetsWinFormsControl source;
        private DocumentItem item;
        private bool dirty;
        private bool switching;
        private long revision;
        private readonly System.Windows.Forms.Timer idleSaveTimer =
            new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);

        public DocSetsCommentWindowControl()
        {
            Dock = DockStyle.Fill;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(3) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            followSelection.Appearance = Appearance.Button;
            followSelection.Text = "∞";
            followSelection.TextAlign = ContentAlignment.MiddleCenter;
            followSelection.Checked = true;
            followSelection.Size = new Size(32, 27);
            toolTip.SetToolTip(followSelection, "Следовать за выделением в дереве DocSets");
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.AutoEllipsis = true;
            title.Padding = new Padding(6, 0, 0, 0);
            bar.Controls.Add(followSelection, 0, 0);
            bar.Controls.Add(title, 1, 0);
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
        }

        internal void Attach(DocSetsViewModel model, DocSetsWinFormsControl owner, DocumentItem selectedItem)
        {
            if (!ReferenceEquals(source, owner))
            {
                if (source != null) source.CurrentCommentItemChanged -= Source_CurrentCommentItemChanged;
                source = owner;
                if (source != null) source.CurrentCommentItemChanged += Source_CurrentCommentItemChanged;
            }
            viewModel = model;
            editor.SetAssetDirectory(viewModel?.AssetDirectory);
            _ = SwitchItemAsync(selectedItem);
        }

        internal async Task ShowSearchResultAsync(DocumentItem selectedItem, int start, int length, int occurrenceIndex)
        {
            await SwitchItemAsync(selectedItem);
            var comment = item?.Content ?? string.Empty;
            if (!DocumentLinkService.TryResolveSearchHighlight(comment, start, length, occurrenceIndex, out var visibleText, out var visibleOccurrence)) return;
            editor.HighlightSearchMatch(visibleText, visibleOccurrence);
        }
        private void Source_CurrentCommentItemChanged(DocumentItem selectedItem)
        {
            if (followSelection.Checked) _ = SwitchItemAsync(selectedItem);
        }

        private async Task SwitchItemAsync(DocumentItem selectedItem)
        {
            selectedItem = viewModel?.ResolvePin(selectedItem) ?? selectedItem;
            if (ReferenceEquals(item, selectedItem)) return;
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

        private async Task SaveAsync(bool readEditor = true)
        {
            idleSaveTimer.Stop();
            await saveGate.WaitAsync();
            try
            {
                var target = item;
                if (target == null || viewModel == null || !dirty) return;
                var savingRevision = revision;
                // TOAST сохраняет собственную редактируемую версию. В модель передаём
                // отдельный снимок, в котором data:image уже вынесены в assets.
                var editorValue = readEditor
                    ? await editor.GetCurrentCommentAsync()
                    : editor.CommentText ?? string.Empty;
                var value = await viewModel.NormalizeCommentAssetsAsync(editorValue);
                if (!string.Equals(target.Content ?? string.Empty, value, StringComparison.Ordinal))
                {
                    viewModel.CaptureUndoState("Изменение заметки", new[] { target });
                    target.Content = value;
                    viewModel.MarkBookmarkModified(target);
                    await viewModel.SaveAsync();
                    source?.RefreshCommentAfterExternalEdit(target);
                }
                if (savingRevision == revision) dirty = false;
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
                case DocumentLinkKind.Symbol: await viewModel.OpenSymbolAsync(item, link.Target, link.Project); break;
                case DocumentLinkKind.File: await viewModel.OpenFileLinkAsync(link.Target, link.SourceId); break;
                case DocumentLinkKind.Bookmark: await viewModel.OpenBookmarkByIdAsync(link.Target); break;
                case DocumentLinkKind.Url:
                    if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri)) VsShellUtilities.OpenSystemBrowser(uri.AbsoluteUri);
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (source != null) source.CurrentCommentItemChanged -= Source_CurrentCommentItemChanged;
                idleSaveTimer.Stop();
                if (dirty && item != null && viewModel != null)
                    ThreadHelper.JoinableTaskFactory.Run(() => SaveAsync(readEditor: false));
                editor.Dispose();
                idleSaveTimer.Dispose();
                saveGate.Dispose();
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
