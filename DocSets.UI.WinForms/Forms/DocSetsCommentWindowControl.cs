using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Drawing;
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
            editor.CommentChanged += (_, __) => { if (!switching) dirty = true; };
            editor.EditingCompleted += async (_, __) => await SaveAsync();
            editor.LinkActivated += target => _ = ActivateLinkAsync(target);
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
                    Project = symbol.Project
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
            _ = SwitchItemAsync(selectedItem);
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
                editor.LoadComment(item?.Comment ?? string.Empty);
                title.Text = item?.Name ?? "Комментарий не выбран";
                dirty = false;
            }
            finally { switching = false; }
        }

        private async Task SaveAsync()
        {
            if (!dirty || item == null || viewModel == null) return;
            var value = editor.CommentText ?? string.Empty;
            if (string.Equals(item.Comment ?? string.Empty, value, StringComparison.Ordinal)) { dirty = false; return; }
            viewModel.CaptureUndoState("Изменение комментария", new[] { item });
            item.Comment = value;
            viewModel.MarkBookmarkModified(item);
            dirty = false;
            await viewModel.SaveAsync();
            source?.RefreshCommentAfterExternalEdit(item);
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
                case DocumentLinkKind.File: await viewModel.OpenFileLinkAsync(link.Target); break;
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
                if (dirty && viewModel != null) ThreadHelper.JoinableTaskFactory.Run(SaveAsync);
                editor.Dispose();
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}