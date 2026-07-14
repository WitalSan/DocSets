using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DocSets
{
    internal enum DocumentLinkKind { Symbol, File, Bookmark, Url }

    [Serializable]
    internal sealed class DocumentLink
    {
        public DocumentLinkKind Kind { get; set; }
        public string Caption { get; set; }
        public string Target { get; set; }
        public string Project { get; set; }
    }

    internal sealed class MarkdownLinkSpan
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public DocumentLink Link { get; set; }
    }

    internal sealed class MarkdownRenderResult
    {
        public string Text { get; set; } = string.Empty;
        public List<MarkdownLinkSpan> Links { get; } = new List<MarkdownLinkSpan>();
        public List<MarkdownStyleSpan> Styles { get; } = new List<MarkdownStyleSpan>();
        public int[] PreviewToSource { get; set; } = new[] { 0 };
    }

    internal enum MarkdownStyle { Bold, Italic, Code }
    internal sealed class MarkdownStyleSpan { public int Start { get; set; } public int Length { get; set; } public MarkdownStyle Style { get; set; } }

    internal static class DocumentLinkService
    {
        public const string DataFormat = "DocSets.DocumentLink.v1";
        private static readonly Regex LinkPattern = new Regex(
            @"\[(?<caption>[^\]]+)\]\((?:(?<kind>symbol|file|bookmark):(?<target>[^\)]+)|(?<url>https?://[^\)]+))\)|\[\[(?<short>[^\]]+)\]\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ToMarkdown(DocumentLink link)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Target)) return string.Empty;
            var caption = string.IsNullOrWhiteSpace(link.Caption) ? GetDefaultCaption(link) : link.Caption.Trim();
            var target = link.Target.Trim();
            if (link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.Project)) target = link.Project.Trim() + "|" + target;
            return "[" + EscapeCaption(caption) + "](" + (link.Kind == DocumentLinkKind.Url ? target : link.Kind.ToString().ToLowerInvariant() + ":" + target) + ")";
        }

        public static DataObject CreateDataObject(DocumentLink link)
        {
            var data = new DataObject(); data.SetData(DataFormat, link); data.SetData(DataFormats.UnicodeText, ToMarkdown(link)); return data;
        }

        public static bool TryGetLink(IDataObject data, out DocumentLink link)
        {
            link = null;
            if (data == null) return false;
            if (data.GetDataPresent(DataFormat)) link = data.GetData(DataFormat) as DocumentLink;
            if (link != null) return true;
            foreach (var format in data.GetFormats())
            {
                Array nodes;
                try { nodes = data.GetData(format) as Array; }
                catch { continue; }
                if (nodes == null || nodes.Length != 1) continue;
                var node = nodes.GetValue(0);
                var tag = node?.GetType().GetProperty("Tag")?.GetValue(node, null);
                var item = tag as DocumentItem;
                if (item == null)
                {
                    var candidate = tag?.GetType().GetProperty("Item")?.GetValue(tag, null);
                    item = candidate as DocumentItem;
                }
                if (item != null)
                {
                    var targetId = string.IsNullOrWhiteSpace(item.TargetId) ? item.Id : item.TargetId;
                    link = new DocumentLink { Kind = DocumentLinkKind.Bookmark, Caption = item.Name, Target = targetId };
                    return !string.IsNullOrWhiteSpace(targetId);
                }
            }
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length == 1)
                {
                    link = new DocumentLink { Kind = DocumentLinkKind.File, Caption = Path.GetFileName(files[0]), Target = files[0] }; return true;
                }
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string;
                if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    link = new DocumentLink { Kind = DocumentLinkKind.Url, Caption = uri.Host, Target = uri.AbsoluteUri }; return true;
                }
                if (File.Exists(text))
                {
                    link = new DocumentLink { Kind = DocumentLinkKind.File, Caption = Path.GetFileName(text), Target = text }; return true;
                }
                var rendered = Render(text);
                if (rendered.Links.Count == 1 && string.Equals(rendered.Text.Trim(), rendered.Links[0].Link.Caption, StringComparison.Ordinal))
                {
                    link = rendered.Links[0].Link; return true;
                }
            }
            var visualStudioFile = TryGetVisualStudioFile(data);
            if (!string.IsNullOrWhiteSpace(visualStudioFile))
            {
                link = new DocumentLink { Kind = DocumentLinkKind.File, Caption = Path.GetFileName(visualStudioFile), Target = visualStudioFile }; return true;
            }
            return false;
        }

        private static string TryGetVisualStudioFile(IDataObject data)
        {
            foreach (var format in data.GetFormats())
            {
                if (format.IndexOf("PROJECTITEM", StringComparison.OrdinalIgnoreCase) < 0 && format.IndexOf("VSSTG", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var raw = data.GetData(format);
                    byte[] bytes = raw as byte[];
                    var stream = raw as MemoryStream;
                    if (bytes == null && stream != null)
                    {
                        var position = stream.Position; stream.Position = 0; bytes = stream.ToArray(); stream.Position = position;
                    }
                    if (bytes == null) continue;
                    foreach (var encoding in new[] { Encoding.Unicode, Encoding.UTF8 })
                    {
                        var text = encoding.GetString(bytes);
                        foreach (Match match in Regex.Matches(text, @"[A-Za-z]:\\[^\0\r\n]+"))
                        {
                            var candidate = match.Value.Trim().TrimEnd('\0');
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public static MarkdownRenderResult Render(string markdown)
        {
            var result = new MarkdownRenderResult();
            var source = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var output = new StringBuilder(); var offset = 0;
            foreach (Match match in LinkPattern.Matches(source))
            {
                AppendPlain(result, output, source.Substring(offset, match.Index - offset));
                var shorthand = match.Groups["short"].Success;
                var caption = shorthand ? match.Groups["short"].Value : match.Groups["caption"].Value;
                var target = shorthand ? caption : match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["target"].Value;
                DocumentLinkKind kind;
                if (shorthand) kind = DocumentLinkKind.Symbol;
                else if (match.Groups["url"].Success) kind = DocumentLinkKind.Url;
                else if (!Enum.TryParse(match.Groups["kind"].Value, true, out kind)) { output.Append(match.Value); offset = match.Index + match.Length; continue; }
                string project = null;
                if (kind == DocumentLinkKind.Symbol && target.IndexOf('|') > 0)
                {
                    var separator = target.IndexOf('|'); project = target.Substring(0, separator); target = target.Substring(separator + 1);
                }
                var start = output.Length; output.Append(caption);
                result.Links.Add(new MarkdownLinkSpan { Start = start, Length = caption.Length, Link = new DocumentLink { Kind = kind, Caption = caption, Target = target, Project = project } });
                offset = match.Index + match.Length;
            }
            AppendPlain(result, output, source.Substring(offset, source.Length - offset));
            result.Text = output.ToString();
            result.PreviewToSource = BuildPositionMap(markdown ?? string.Empty, result.Text);
            return result;
        }

        private static int[] BuildPositionMap(string source, string rendered)
        {
            var map = new int[rendered.Length + 1];
            var sourceIndex = 0;
            for (var renderedIndex = 0; renderedIndex < rendered.Length; renderedIndex++)
            {
                var character = rendered[renderedIndex];
                while (sourceIndex < source.Length && source[sourceIndex] != character) sourceIndex++;
                map[renderedIndex] = Math.Min(sourceIndex, source.Length);
                if (sourceIndex < source.Length) sourceIndex++;
            }
            map[rendered.Length] = Math.Min(sourceIndex, source.Length);
            return map;
        }
        private static void AppendPlain(MarkdownRenderResult result, StringBuilder output, string value)
        {
            value = Regex.Replace(value, @"(?m)^#{1,6}\s+", string.Empty);
            var pattern = new Regex(@"\*\*(?<bold>[^\r\n]+?)\*\*|__(?<bold2>[^\r\n]+?)__|(?<!\*)\*(?<italic>[^\r\n]+?)\*|(?<!_)_(?<italic2>[^\r\n]+?)_|`(?<code>[^`]+)`");
            var offset = 0;
            foreach (Match match in pattern.Matches(value))
            {
                output.Append(value, offset, match.Index - offset);
                var group = match.Groups["bold"].Success ? match.Groups["bold"] : match.Groups["bold2"].Success ? match.Groups["bold2"] :
                    match.Groups["italic"].Success ? match.Groups["italic"] : match.Groups["italic2"].Success ? match.Groups["italic2"] : match.Groups["code"];
                var style = match.Groups["bold"].Success || match.Groups["bold2"].Success ? MarkdownStyle.Bold :
                    match.Groups["code"].Success ? MarkdownStyle.Code : MarkdownStyle.Italic;
                var start = output.Length; output.Append(group.Value);
                result.Styles.Add(new MarkdownStyleSpan { Start = start, Length = group.Value.Length, Style = style });
                offset = match.Index + match.Length;
            }
            output.Append(value, offset, value.Length - offset);
        }

        private static string GetDefaultCaption(DocumentLink link)
        {
            if (link.Kind == DocumentLinkKind.File) return Path.GetFileName(link.Target);
            var index = link.Target.LastIndexOf('.'); return index >= 0 ? link.Target.Substring(index + 1) : link.Target;
        }
        private static string EscapeCaption(string value) => (value ?? string.Empty).Replace("[", "\\[").Replace("]", "\\]");
    }

    internal sealed class MarkdownCommentControl : UserControl
    {
        private readonly ToolStrip toolbar = new ToolStrip();
        private readonly ToolStripButton previewButton = new ToolStripButton("Просмотр");
        private readonly ToolStripButton editButton = new ToolStripButton("Редактирование");
        private readonly ToolStripButton boldButton = new ToolStripButton("B");
        private readonly ToolStripButton italicButton = new ToolStripButton("I");
        private readonly ToolStripButton codeButton = new ToolStripButton("Код");
        private readonly ToolStripButton symbolButton = new ToolStripButton("Символ");
        private readonly ToolStripButton linkButton = new ToolStripButton("Link");
        private readonly Panel body = new Panel();
        private readonly RichTextBox preview = new RichTextBox();
        private readonly RichTextBox editor = new RichTextBox();
        private readonly List<MarkdownLinkSpan> links = new List<MarkdownLinkSpan>();
        private int[] previewToEditorPositions = new[] { 0 };
        private MarkdownLinkSpan contextLink;
        private bool loading;
        private readonly System.Windows.Forms.Timer highlightTimer = new System.Windows.Forms.Timer { Interval = 140 };
        private Font editorRegularFont;
        private Font editorBoldFont;
        private Font editorItalicFont;
        private Font editorCodeFont;
        private Font editorLinkFont;
        private const int WmSetRedraw = 0x000B;
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event Action<DocumentLink> LinkActivated;
        public event Action<string, int> ExternalSymbolDropRequested;

        public MarkdownCommentControl()
        {
            Dock = DockStyle.Fill; toolbar.GripStyle = ToolStripGripStyle.Hidden;
            previewButton.CheckOnClick = editButton.CheckOnClick = true; toolbar.Items.Add(previewButton); toolbar.Items.Add(editButton);
            toolbar.Items.Add(new ToolStripSeparator()); toolbar.Items.Add(boldButton); toolbar.Items.Add(italicButton); toolbar.Items.Add(codeButton); toolbar.Items.Add(symbolButton); toolbar.Items.Add(linkButton);
            previewButton.Click += (_, __) => ShowPreview(true); editButton.Click += (_, __) => ShowEditor();
            boldButton.Font = new Font(toolbar.Font, FontStyle.Bold); italicButton.Font = new Font(toolbar.Font, FontStyle.Italic);
            boldButton.ToolTipText = "Полужирный (Ctrl+B)"; italicButton.ToolTipText = "Курсив (Ctrl+I)";
            codeButton.ToolTipText = "Встроенный код"; symbolButton.ToolTipText = "Ссылка на символ (Ctrl+K)";
            linkButton.ToolTipText = "Внешняя ссылка";
            boldButton.Click += (_, __) => WrapSelection("**", "**"); italicButton.Click += (_, __) => WrapSelection("_", "_");
            codeButton.Click += (_, __) => WrapSelection("`", "`"); symbolButton.Click += (_, __) => WrapSelection("[[", "]]", "Symbol");
            linkButton.Click += (_, __) => InsertExternalLink();
            Controls.Add(body); Controls.Add(toolbar); toolbar.Dock = DockStyle.Top; body.Dock = DockStyle.Fill;
            preview.Dock = DockStyle.Fill; preview.ReadOnly = true; preview.BorderStyle = BorderStyle.None; preview.BackColor = SystemColors.Window;
            preview.DetectUrls = false; preview.ScrollBars = RichTextBoxScrollBars.Vertical; preview.AllowDrop = true;
            preview.MouseMove += PreviewMouseMove; preview.MouseUp += PreviewMouseUp;
            preview.DragEnter += EditorDragEnter; preview.DragOver += EditorDragEnter; preview.DragDrop += PreviewDragDrop;
            preview.KeyDown += PreviewKeyDown;
            preview.KeyPress += PreviewKeyPress;
            var linkMenu = new ContextMenuStrip();
            linkMenu.Items.Add("Копировать ссылку", null, (_, __) => { if (contextLink != null) Clipboard.SetText(DocumentLinkService.ToMarkdown(contextLink.Link)); });
            linkMenu.Items.Add("Копировать адрес", null, (_, __) => { if (contextLink != null) Clipboard.SetText(contextLink.Link.Target); });
            linkMenu.Opening += (_, e) => e.Cancel = contextLink == null;
            preview.ContextMenuStrip = linkMenu;
            body.Controls.Add(preview);
            editor.Dock = DockStyle.Fill; editor.AcceptsTab = true; editor.ScrollBars = RichTextBoxScrollBars.Vertical; editor.AllowDrop = true;
            editor.DetectUrls = false; editor.HideSelection = false; editor.BorderStyle = BorderStyle.None; editor.BackColor = SystemColors.Window;
            editorRegularFont = new Font(editor.Font, FontStyle.Regular);
            editorBoldFont = new Font(editor.Font, FontStyle.Bold);
            editorItalicFont = new Font(editor.Font, FontStyle.Italic);
            editorCodeFont = new Font("Consolas", editor.Font.Size, FontStyle.Regular);
            editorLinkFont = new Font(editor.Font, FontStyle.Underline);
            highlightTimer.Tick += (_, __) => { highlightTimer.Stop(); ApplyEditorFormatting(); };
            editor.TextChanged += EditorTextChanged;
            editor.KeyDown += EditorKeyDown;
            editor.DragEnter += EditorDragEnter; editor.DragOver += EditorDragEnter; editor.DragDrop += EditorDragDrop; body.Controls.Add(editor);
            ShowPreview();
        }

        public string CommentText => editor.Text ?? string.Empty;
        public bool IsEditing => editor.Visible;
        public void LoadComment(string value, bool resetToPreview = false)
        {
            loading = true;
            try
            {
                editor.Text = value ?? string.Empty; ApplyEditorFormatting(); RenderPreview();
                if (resetToPreview) { editor.Visible = false; preview.Visible = true; previewButton.Checked = true; editButton.Checked = false; }
            }
            finally { loading = false; }
        }
        public void ShowPreview(bool focusPreview = false)
        {
            var wasEditing = editor.Visible;
            var caretPosition = Math.Max(0, Math.Min(editor.TextLength, editor.SelectionStart));
            RenderPreview(caretPosition); editor.Visible = false; preview.Visible = true; previewButton.Checked = true; editButton.Checked = false;
            if (wasEditing && !loading) EditingCompleted?.Invoke(this, EventArgs.Empty);
            if (focusPreview)
            {
                preview.Focus();
                if (IsHandleCreated) BeginInvoke((Action)(() => { if (preview.Visible) preview.Focus(); }));
            }
        }
        public void ShowEditor() { preview.Visible = false; editor.Visible = true; previewButton.Checked = false; editButton.Checked = true; editor.Focus(); }
        public void InsertLink(DocumentLink link)
        {
            if (link == null) return; ShowEditor();
            if (!string.IsNullOrWhiteSpace(editor.SelectedText)) link.Caption = editor.SelectedText;
            var markdown = DocumentLinkService.ToMarkdown(link); if (markdown.Length != 0) editor.SelectedText = markdown;
        }

        public void InsertResolvedLink(DocumentLink link, int position)
        {
            ShowEditor(); editor.SelectionStart = Math.Max(0, Math.Min(editor.TextLength, position)); editor.SelectionLength = 0; InsertLink(link);
        }

        private void RenderPreview(int caretPosition = 0)
        {
            var rendered = DocumentLinkService.Render(editor.Text); links.Clear(); links.AddRange(rendered.Links); previewToEditorPositions = rendered.PreviewToSource; preview.Text = rendered.Text;
            foreach (var span in rendered.Styles)
            {
                preview.Select(span.Start, span.Length);
                if (span.Style == MarkdownStyle.Code) { preview.SelectionFont = new Font("Consolas", preview.Font.Size); preview.SelectionBackColor = Color.FromArgb(238, 238, 238); }
                else preview.SelectionFont = new Font(preview.Font, span.Style == MarkdownStyle.Bold ? FontStyle.Bold : FontStyle.Italic);
            }
            foreach (var span in links) { preview.Select(span.Start, span.Length); preview.SelectionColor = Color.FromArgb(0, 102, 204); preview.SelectionFont = new Font(preview.Font, FontStyle.Underline); }
            preview.Select(EditorToPreviewPosition(caretPosition), 0);
        }
        private int PreviewToEditorPosition(int previewPosition)
        {
            if (previewToEditorPositions == null || previewToEditorPositions.Length == 0) return 0;
            var index = Math.Max(0, Math.Min(previewToEditorPositions.Length - 1, previewPosition));
            return Math.Max(0, Math.Min(editor.TextLength, previewToEditorPositions[index]));
        }

        private int EditorToPreviewPosition(int editorPosition)
        {
            if (previewToEditorPositions == null || previewToEditorPositions.Length == 0) return 0;
            var target = Math.Max(0, Math.Min(editor.TextLength, editorPosition));
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            for (var index = 0; index < previewToEditorPositions.Length; index++)
            {
                var distance = Math.Abs(previewToEditorPositions[index] - target);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = index;
                if (distance == 0) break;
            }
            return Math.Max(0, Math.Min(preview.TextLength, bestIndex));
        }
        private MarkdownLinkSpan HitTest(Point point)
        {
            if (links.Count == 0 || preview.TextLength == 0) return null;
            foreach (var span in links) if (IsInsideLinkRegion(span, point)) return span;
            return null;
        }

        private bool IsInsideLinkRegion(MarkdownLinkSpan span, Point point)
        {
            if (span.Length <= 0 || span.Start < 0 || span.Start + span.Length > preview.TextLength) return false;
            var start = preview.GetPositionFromCharIndex(span.Start);
            var lastIndex = span.Start + span.Length - 1;
            var last = preview.GetPositionFromCharIndex(lastIndex);
            var lastWidth = TextRenderer.MeasureText(preview.Text.Substring(lastIndex, 1), preview.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var lineHeight = preview.Font.Height;
            if (start.Y == last.Y)
                return point.X >= start.X && point.X <= last.X + Math.Max(1, lastWidth) && point.Y >= start.Y && point.Y <= start.Y + lineHeight;
            if (point.Y >= start.Y && point.Y < start.Y + lineHeight) return point.X >= start.X;
            if (point.Y >= last.Y && point.Y <= last.Y + lineHeight) return point.X <= last.X + Math.Max(1, lastWidth);
            return point.Y > start.Y + lineHeight && point.Y < last.Y;
        }
        private void PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (links.Count == 0) { if (preview.Cursor != Cursors.IBeam) preview.Cursor = Cursors.IBeam; return; }
            var cursor = HitTest(e.Location) == null ? Cursors.IBeam : Cursors.Hand;
            if (preview.Cursor != cursor) preview.Cursor = cursor;
        }
        private void PreviewMouseUp(object sender, MouseEventArgs e)
        {
            var span = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && span != null) LinkActivated?.Invoke(span.Link);
            else if (e.Button == MouseButtons.Left)
            {
                preview.Select(preview.GetCharIndexFromPosition(e.Location), 0); preview.Focus();
            }
            if (e.Button == MouseButtons.Right) contextLink = span;
        }
        private void PreviewKeyPress(object sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;
            var position = PreviewToEditorPosition(preview.SelectionStart);
            ShowEditor(); editor.SelectionStart = position; editor.SelectionLength = 0; editor.SelectedText = e.KeyChar.ToString();
            e.Handled = true;
        }
        private void PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.F2) return;
            var position = PreviewToEditorPosition(preview.SelectionStart);
            ShowEditor();
            editor.SelectionStart = position;
            editor.SelectionLength = 0;
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
        private void EditorDragEnter(object sender, DragEventArgs e) => e.Effect = DocumentLinkService.TryGetLink(e.Data, out _) || HasExternalText(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        private void EditorDragDrop(object sender, DragEventArgs e)
        {
            var index = editor.GetCharIndexFromPosition(editor.PointToClient(new Point(e.X, e.Y)));
            if (!DocumentLinkService.TryGetLink(e.Data, out var link))
            {
                var text = GetExternalText(e.Data); if (!string.IsNullOrWhiteSpace(text)) ExternalSymbolDropRequested?.Invoke(text.Trim(), index); return;
            }
            var insideSelection = editor.SelectionLength > 0 && index >= editor.SelectionStart && index <= editor.SelectionStart + editor.SelectionLength;
            if (!insideSelection) { editor.SelectionStart = index; editor.SelectionLength = 0; }
            InsertLink(link);
        }
        private void PreviewDragDrop(object sender, DragEventArgs e)
        {
            editor.SelectionStart = editor.TextLength; editor.SelectionLength = 0;
            if (!DocumentLinkService.TryGetLink(e.Data, out var link))
            {
                var text = GetExternalText(e.Data); if (!string.IsNullOrWhiteSpace(text)) ExternalSymbolDropRequested?.Invoke(text.Trim(), editor.TextLength); return;
            }
            if (editor.TextLength > 0 && !editor.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal)) editor.SelectedText = Environment.NewLine;
            InsertLink(link);
        }
        private static bool HasExternalText(IDataObject data) => !string.IsNullOrWhiteSpace(GetExternalText(data));
        private static string GetExternalText(IDataObject data)
        {
            try { return data?.GetDataPresent(DataFormats.UnicodeText) == true ? data.GetData(DataFormats.UnicodeText) as string : null; }
            catch { return null; }
        }
        private void WrapSelection(string prefix, string suffix, string placeholder = null)
        {
            ShowEditor(); var selected = editor.SelectedText; if (selected.Length == 0) selected = placeholder ?? string.Empty;
            var start = editor.SelectionStart; editor.SelectedText = prefix + selected + suffix;
            editor.SelectionStart = start + prefix.Length; editor.SelectionLength = selected.Length;
        }
        private void InsertExternalLink()
        {
            ShowEditor();
            var caption = string.IsNullOrWhiteSpace(editor.SelectedText) ? "Link" : editor.SelectedText;
            var clipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : string.Empty;
            var url = IsHttpUrl(clipboard) ? clipboard : PromptForUrl();
            if (!IsHttpUrl(url)) return;
            editor.SelectedText = DocumentLinkService.ToMarkdown(new DocumentLink { Kind = DocumentLinkKind.Url, Caption = caption, Target = url });
        }
        private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        private string PromptForUrl()
        {
            using (var dialog = new Form { Text = "Внешняя ссылка", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(520, 92), AutoScaleMode = AutoScaleMode.Font })
            using (var input = new TextBox { Left = 10, Top = 12, Width = 500 })
            using (var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 354, Top = 50, Width = 75 })
            using (var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 435, Top = 50, Width = 75 })
            {
                dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.Controls.Add(cancel); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                return dialog.ShowDialog(FindForm()) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (ContainsFocus && keyData == Keys.Escape)
            {
                if (IsEditing) ShowPreview(true);
                return true;
            }
            if (preview.ContainsFocus && (keyData == Keys.Enter || keyData == Keys.F2))
            {
                var position = PreviewToEditorPosition(preview.SelectionStart);
                ShowEditor();
                editor.SelectionStart = position;
                editor.SelectionLength = 0;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        private void EditorTextChanged(object sender, EventArgs e)
        {
            highlightTimer.Stop();
            if (!loading) highlightTimer.Start();
            if (!loading) CommentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyEditorFormatting()
        {
            if (editor.IsDisposed || editorRegularFont == null) return;
            var selectionStart = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            var firstVisibleLine = editor.IsHandleCreated ? (int)SendMessage(editor.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero) : 0;
            if (editor.IsHandleCreated) SendMessage(editor.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                editor.SelectAll();
                editor.SelectionFont = editorRegularFont;
                editor.SelectionColor = SystemColors.WindowText;
                editor.SelectionBackColor = SystemColors.Window;
                var source = editor.Text ?? string.Empty;
                foreach (Match match in Regex.Matches(source, @"(?m)^(?<marks>#{1,6})[ \t]+(?<text>.+)$"))
                {
                    FormatEditorRange(match.Groups["marks"], editorRegularFont, SystemColors.GrayText);
                    FormatEditorRange(match.Groups["text"], editorBoldFont, SystemColors.WindowText);
                }
                foreach (Match match in Regex.Matches(source, @"\*\*(?<text>[^\r\n]+?)\*\*|__(?<text2>[^\r\n]+?)__"))
                {
                    var group = match.Groups["text"].Success ? match.Groups["text"] : match.Groups["text2"];
                    FormatEditorRange(match, editorRegularFont, SystemColors.GrayText);
                    FormatEditorRange(group, editorBoldFont, SystemColors.WindowText);
                }
                foreach (Match match in Regex.Matches(source, @"(?<!\*)\*(?<text>[^\r\n]+?)\*|(?<!_)_(?<text2>[^\r\n]+?)_"))
                {
                    var group = match.Groups["text"].Success ? match.Groups["text"] : match.Groups["text2"];
                    FormatEditorRange(match, editorRegularFont, SystemColors.GrayText);
                    FormatEditorRange(group, editorItalicFont, SystemColors.WindowText);
                }
                foreach (Match match in Regex.Matches(source, @"`(?<text>[^`]+)`"))
                {
                    FormatEditorRange(match, editorCodeFont, Color.FromArgb(80, 80, 80), Color.FromArgb(238, 238, 238));
                    FormatEditorRange(match.Groups["text"], editorCodeFont, SystemColors.WindowText, Color.FromArgb(238, 238, 238));
                }
                foreach (Match match in Regex.Matches(source, @"\[(?<caption>[^\]]+)\]\((?<target>[^\)]+)\)|\[\[(?<short>[^\]]+)\]\]"))
                {
                    FormatEditorRange(match, editorRegularFont, SystemColors.GrayText);
                    var caption = match.Groups["short"].Success ? match.Groups["short"] : match.Groups["caption"];
                    FormatEditorRange(caption, editorLinkFont, Color.FromArgb(0, 102, 204));
                    if (match.Groups["target"].Success) FormatEditorRange(match.Groups["target"], editorCodeFont, Color.FromArgb(96, 96, 96));
                }
                editor.Select(Math.Max(0, Math.Min(editor.TextLength, selectionStart)), Math.Max(0, Math.Min(editor.TextLength - Math.Min(editor.TextLength, selectionStart), selectionLength)));
                if (editor.IsHandleCreated)
                {
                    var currentFirstLine = (int)SendMessage(editor.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(editor.Handle, EmLineScroll, IntPtr.Zero, new IntPtr(firstVisibleLine - currentFirstLine));
                }
            }
            finally
            {
                if (editor.IsHandleCreated) SendMessage(editor.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                editor.Invalidate();
            }
        }

        private void FormatEditorRange(Group range, Font font, Color color, Color? background = null)
        {
            if (!range.Success || range.Length == 0) return;
            editor.Select(range.Index, range.Length);
            editor.SelectionFont = font;
            editor.SelectionColor = color;
            if (background.HasValue) editor.SelectionBackColor = background.Value;
        }

        private void FormatEditorRange(Match range, Font font, Color color, Color? background = null)
        {
            if (!range.Success || range.Length == 0) return;
            editor.Select(range.Index, range.Length);
            editor.SelectionFont = font;
            editor.SelectionColor = color;
            if (background.HasValue) editor.SelectionBackColor = background.Value;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                highlightTimer.Dispose();
                editorRegularFont?.Dispose();
                editorBoldFont?.Dispose();
                editorItalicFont?.Dispose();
                editorCodeFont?.Dispose();
                editorLinkFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.B) { WrapSelection("**", "**"); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.I) { WrapSelection("_", "_"); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.K) { WrapSelection("[[", "]]", "Symbol"); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Enter) { ShowPreview(true); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { ShowPreview(true); e.SuppressKeyPress = true; }
        }
    }
}
