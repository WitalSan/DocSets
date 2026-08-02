using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Общий контрол отдельной сессии HTML-редактора.
    /// </summary>
    public class DocSetsHtmlCommentWindowControl : UserControl
    {
        private readonly HtmlWebEditorCommentControl editor;
        private readonly string editorName;
        private readonly CheckBox followSelection = new CheckBox();
        private readonly Button saveButton = new Button();
        private readonly CheckBox toolbarButton = new CheckBox();
        private readonly BookmarkBreadcrumb title = new BookmarkBreadcrumb();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer idleSaveTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim switchGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> editingSessions =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly LinkedList<string> editingSessionOrder = new LinkedList<string>();
        private const int MaximumEditingSessions = 100;
        private DocSetsViewModel viewModel;
        private DocSetsWinFormsControl source;
        private DocumentItem item;
        private bool dirty;
        private bool switching;
        private bool shuttingDown;
        private bool formatPromptActive;
        private long revision;

        protected DocSetsHtmlCommentWindowControl(
            HtmlWebEditorCommentControl editorControl, string name)
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
                ColumnCount = 4,
                RowCount = 1,
                Padding = new Padding(3)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            toolbarButton.Appearance = Appearance.Button;
            toolbarButton.Text = "☰";
            toolbarButton.TextAlign = ContentAlignment.MiddleCenter;
            toolbarButton.Checked = true;
            toolbarButton.Size = DpiService.Scale(this, new Size(32, 27));
            UpdateToolbarButtonToolTip();
            toolTip.SetToolTip(saveButton, "Сохранить HTML-заметку (Ctrl+S)");
            title.ItemSelected += (_, e) =>
            {
                var symbol = e.Item?.Value as string;
                if (!string.IsNullOrWhiteSpace(symbol) && viewModel != null)
                    _ = viewModel.OpenSymbolAsync(item, symbol, item?.Project);
            };
            bar.Controls.Add(followSelection, 0, 0);
            bar.Controls.Add(saveButton, 1, 0);
            bar.Controls.Add(toolbarButton, 2, 0);
            bar.Controls.Add(title, 3, 0);
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
            editor.CommentChanged += async (_, __) => await OnEditorCommentChangedAsync();
            editor.EditingCompleted += async (_, __) => await SaveAsync();
            editor.SaveRequested += async (_, __) => await SaveAsync(forceRead: true);
            editor.SaveStateChanged += enabled => saveButton.Enabled = enabled && item?.ContentFormat == ContentFormat.Html;
            saveButton.Click += async (_, __) => await SaveAsync(forceRead: true);
            toolbarButton.CheckedChanged += (_, __) =>
            {
                editor.SetToolbarVisible(toolbarButton.Checked);
                UpdateToolbarButtonToolTip();
                if (viewModel == null) return;
                viewModel.SolutionState.JoditToolbarVisible = toolbarButton.Checked;
                viewModel.SaveSolutionState();
            };
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

        public async Task AttachAsync(
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
            SetAssetDirectory(viewModel?.AssetDirectory);
            editor.SetNoteTagStyles(viewModel?.NoteTagStyles);
            var toolbarVisible = viewModel?.SolutionState?.JoditToolbarVisible ?? true;
            if (toolbarButton.Checked != toolbarVisible)
                toolbarButton.Checked = toolbarVisible;
            else
                editor.SetToolbarVisible(toolbarVisible);
            await SwitchItemAsync(selectedItem);
        }

        public async Task NavigateToAnchorAsync(DocumentItem selectedItem, string anchor)
        {
            await SwitchItemAsync(selectedItem);
            if (!string.IsNullOrWhiteSpace(anchor))
                await editor.ScrollToAnchorAsync(anchor);
        }

        private void UpdateToolbarButtonToolTip()
        {
            toolTip.SetToolTip(toolbarButton, toolbarButton.Checked
                ? "Скрыть панель инструментов редактора"
                : "Показать панель инструментов редактора");
        }

        public Task CommitPendingEditAsync() => SaveAsync(forceRead: true);

        public void SetAssetDirectory(string value) => editor.SetAssetDirectory(value);

        public Task CommitPendingEditBeforeCloseAsync()
            => SaveAsync(forceRead: true);

        public void FocusEditor()
        {
            if (item == null) return;
            Select();
            Focus();
            editor.Enabled = true;
            editor.FocusEditor();
        }

        public void ExecuteEditorCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            editor.ExecuteEditorCommand(command);
        }

        public void ShowSearchResult(int start, int length, int occurrenceIndex)
        {
            var content = item?.Content ?? string.Empty;
            if (start < 0 || start >= content.Length || length <= 0) return;
            var fragment = content.Substring(start, Math.Min(length, content.Length - start));
            var visibleText = WebUtility.HtmlDecode(
                Regex.Replace(fragment, @"<[^>]+>", string.Empty));
            if (string.IsNullOrWhiteSpace(visibleText)) return;
            editor.HighlightSearchMatch(visibleText, occurrenceIndex);
        }

        private void Source_CurrentCommentItemChanged(DocumentItem selectedItem)
        {
            if (followSelection.Checked) _ = SwitchItemAsync(selectedItem);
        }

        private void Source_CommentContentChanged(DocumentItem changedItem, object origin)
        {
            if (ReferenceEquals(origin, this) || dirty || item == null || !ReferenceEquals(item, changedItem)) return;
            RemoveEditingSession(item);
            _ = ReloadCurrentItemAsync();
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

        private async Task ReloadCurrentItemAsync()
        {
            switching = true;
            try
            {
                SetAssetDirectory(viewModel?.AssetDirectory);
                editor.SetNoteTagStyles(viewModel?.NoteTagStyles);
                var html = item?.Content ?? string.Empty;
                editor.SetLinkNodeId(item?.Id);
                editor.Enabled = item != null;
                var session = item?.ContentFormat == ContentFormat.Html
                    ? GetEditingSession(item)
                    : null;
                if (session == null)
                    editor.LoadComment(html);
                else
                    await editor.LoadEditingSessionAsync(html, session);
                UpdateTitle();
                dirty = false;
                saveButton.Enabled = false;
            }
            finally { switching = false; }
        }

        private async Task SwitchItemAsync(DocumentItem selectedItem)
        {
            await switchGate.WaitAsync();
            try
            {
                selectedItem = viewModel?.ResolvePin(selectedItem) ?? selectedItem;
                if (ReferenceEquals(item, selectedItem))
                {
                    if (!dirty && item != null &&
                        !string.Equals(editor.CommentText, item.Content ?? string.Empty, StringComparison.Ordinal))
                        await ReloadCurrentItemAsync();
                    return;
                }
                await SaveAsync();
                await CaptureCurrentEditingSessionAsync();
                item = selectedItem;
                await ReloadCurrentItemAsync();
            }
            finally { switchGate.Release(); }
        }

        private async Task CaptureCurrentEditingSessionAsync()
        {
            var key = GetEditingSessionKey(item);
            if (key == null || item.ContentFormat != ContentFormat.Html) return;
            var session = await editor.CaptureEditingSessionAsync();
            if (string.IsNullOrWhiteSpace(session)) return;

            editingSessions[key] = session;
            editingSessionOrder.Remove(key);
            editingSessionOrder.AddLast(key);
            while (editingSessionOrder.Count > MaximumEditingSessions)
            {
                var expired = editingSessionOrder.First.Value;
                editingSessionOrder.RemoveFirst();
                editingSessions.Remove(expired);
            }
        }

        private string GetEditingSession(DocumentItem value)
        {
            var key = GetEditingSessionKey(value);
            if (key == null || !editingSessions.TryGetValue(key, out var session)) return null;
            editingSessionOrder.Remove(key);
            editingSessionOrder.AddLast(key);
            return session;
        }

        private void RemoveEditingSession(DocumentItem value)
        {
            var key = GetEditingSessionKey(value);
            if (key == null) return;
            editingSessions.Remove(key);
            editingSessionOrder.Remove(key);
        }

        private static string GetEditingSessionKey(DocumentItem value)
            => string.IsNullOrWhiteSpace(value?.Id) ? null : value.Id;

        private static string GetTitle(DocumentItem value)
        {
            return value?.Name ?? "Заметка не выбрана";
        }

        private void UpdateTitle()
        {
            title.SetItems(BreadcrumbToolTipBuilder.BuildItems(item));
        }

        private async Task OnEditorCommentChangedAsync()
        {
            if (switching || item == null) return;
            if (item.ContentFormat != ContentFormat.Html)
            {
                if (formatPromptActive) return;
                formatPromptActive = true;
                idleSaveTimer.Stop();
                try
                {
                    var result = MessageBox.Show(
                        "Формат этой заметки не соответствует редактору Jodit.\r\n\r\n" +
                        "Изменить формат заметки на HTML и продолжить редактирование?\r\n" +
                        "Содержимое автоматически не преобразуется.",
                        "DocSets — формат заметки",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (result != DialogResult.Yes)
                    {
                        await ReloadCurrentItemAsync();
                        return;
                    }

                    await viewModel.ChangeContentFormatAsync(item, ContentFormat.Html);
                    source?.NotifyCommentSaved(item, this);
                    UpdateTitle();
                }
                finally
                {
                    formatPromptActive = false;
                }
            }

            dirty = true;
            revision++;
            saveButton.Enabled = true;
            idleSaveTimer.Stop();
            idleSaveTimer.Start();
        }

        private async Task SaveAsync(bool forceRead = false)
        {
            if (shuttingDown || formatPromptActive || IsDisposed ||
                viewModel?.CanSave != true) return;
            idleSaveTimer.Stop();
            await saveGate.WaitAsync();
            try
            {
                if (formatPromptActive) return;
                var target = item;
                if (target == null || target.ContentFormat != ContentFormat.Html || viewModel == null ||
                    !viewModel.CanSave ||
                    (!dirty && !forceRead)) return;
                var savingRevision = revision;
                // Актуальный HTML приходит в сообщении changed и хранится локально.
                // При закрытии Visual Studio нельзя ожидать ExecuteScriptAsync:
                // WebView2 может ожидать тот же поток UI.
                var editorValue = editor.CommentText ?? string.Empty;
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
                if (dirty && !shuttingDown && !IsDisposed) idleSaveTimer.Start();
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
                    var bookmarkTarget = link.Target ?? string.Empty;
                    var fragmentIndex = bookmarkTarget.IndexOf('#');
                    var bookmarkId = fragmentIndex < 0
                        ? bookmarkTarget
                        : bookmarkTarget.Substring(0, fragmentIndex);
                    var anchor = fragmentIndex < 0
                        ? string.Empty
                        : bookmarkTarget.Substring(fragmentIndex + 1);
                    if (await viewModel.OpenBookmarkByIdAsync(bookmarkId))
                    {
                        source?.NavigateToSelectedItem();
                        await SwitchItemAsync(source?.CurrentCommentItem);
                        if (!string.IsNullOrWhiteSpace(anchor))
                            await editor.ScrollToAnchorAsync(anchor);
                    }
                    break;
                case DocumentLinkKind.Url:
                    if (Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
                        await viewModel.OpenUrlAsync(uri.AbsoluteUri);
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
                shuttingDown = true;
                if (source != null)
                {
                    source.CurrentCommentItemChanged -= Source_CurrentCommentItemChanged;
                    source.CommentContentChanged -= Source_CommentContentChanged;
                }
                if (viewModel != null)
                    viewModel.UndoRedoStateRestored -= ViewModel_UndoRedoStateRestored;
                idleSaveTimer.Stop();
                editor.Dispose();
                idleSaveTimer.Dispose();
                switchGate.Dispose();
                toolTip.Dispose();
                saveButton.Image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
