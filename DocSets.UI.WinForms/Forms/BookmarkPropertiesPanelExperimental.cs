using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BookmarkPropertiesPanelExperimental : UserControl
    {
        private readonly TextBox nameTextBox = new TextBox();
        private readonly CheckBox folderCheckBox = new CheckBox();
        private readonly RadioButton emptyButton = new RadioButton();
        private readonly RadioButton symbolButton = new RadioButton();
        private readonly RadioButton fileButton = new RadioButton();
        private readonly TextBox pathTextBox = new TextBox();
        private readonly TextBox symbolTextBox = new TextBox();
        private readonly TextBox projectTextBox = new TextBox();
        private readonly NumericUpDown lineBox = new NumericUpDown();
        private readonly NumericUpDown columnBox = new NumericUpDown();
        private readonly TextBox commentTextBox = new TextBox();
        private readonly RichTextBox codeTextBox = new RichTextBox();
        private readonly RichTextBox livePreviewTextBox = new RichTextBox();
        private readonly LinkLabel codeSymbolLabel = new LinkLabel();
        private readonly Button copyCodeButton = new Button();
        private readonly Button refreshCodeButton = new Button();
        private readonly ToolTip toolTip = new ToolTip();
        private ExperimentalAccordionHost accordion;
        private ExperimentalAccordionSection commentSection;
        private ExperimentalAccordionSection codeSection;
        private ExperimentalAccordionSection previewSection;
        private ExperimentalAccordionSection propertiesSection;
        private readonly Panel detailsHost = new Panel();
        private readonly Dictionary<BookmarkColor, Button> colorButtons = new Dictionary<BookmarkColor, Button>();
        private readonly CheckBox pinCheckBox = new CheckBox();
        private bool loading;
        private bool multipleSelection;
        private bool loadedAllPinned;
        private DocumentItem item;
        private BookmarkColor selectedColor;

        public event EventHandler ItemChanged;
        public event EventHandler ColorChanged;
        public event EventHandler RefreshCodeRequested;
        public event EventHandler PreviewRequested;
        public event EventHandler PinChanged;
        public event EventHandler LayoutStateChanged;
        public event Action<string> SymbolLinkClicked;

        public BookmarkPropertiesPanelExperimental()
        {
            Dock = DockStyle.Fill;
            BuildLayout();
            WireChanges(detailsHost);
            commentTextBox.TextChanged += Changed;
            LoadItem(null);
        }

        public DocumentItem CurrentItem => item;
        public bool RequestedPinState => !loadedAllPinned;
        public BookmarkColor SelectedColor => selectedColor;

        public IList<string> SectionOrder => accordion?.SectionOrder ?? new List<string>();
        public IList<string> ExpandedSections => accordion?.ExpandedSections ?? new List<string>();

        public void ApplyLayoutState(IEnumerable<string> sectionOrder, IEnumerable<string> expandedSections)
        {
            accordion?.ApplyState(sectionOrder, expandedSections);
        }

        public void LoadItem(DocumentItem value, bool isPinned = false)
        {
            LoadSelection(value, false, isPinned, isPinned, value?.Color, value != null);
        }

        public void LoadSelection(
            DocumentItem value,
            bool multiple,
            bool allPinned,
            bool anyPinned,
            BookmarkColor? commonColor,
            bool canPin)
        {
            loading = true;
            try
            {
                item = value;
                multipleSelection = multiple;
                loadedAllPinned = value != null && allPinned;
                Enabled = value != null;
                nameTextBox.Text = value?.Name ?? string.Empty;
                folderCheckBox.Checked = value?.NodeType == NodeType.Folder;
                var type = value?.Type ?? BookmarkType.Empty;
                emptyButton.Checked = type == BookmarkType.Empty;
                symbolButton.Checked = type == BookmarkType.Symbol;
                fileButton.Checked = type == BookmarkType.File;
                pathTextBox.Text = value?.Path ?? string.Empty;
                symbolTextBox.Text = value?.Symbol ?? string.Empty;
                projectTextBox.Text = value?.Project ?? string.Empty;
                lineBox.Value = Clamp(value?.Line ?? 1, lineBox.Minimum, lineBox.Maximum);
                columnBox.Value = Clamp(value?.Column ?? 1, columnBox.Minimum, columnBox.Maximum);
                commentTextBox.Text = value?.Comment ?? string.Empty;
                UpdateCodePreview(value);
                selectedColor = commonColor ?? BookmarkColor.None;
                pinCheckBox.ThreeState = multiple;
                pinCheckBox.CheckState = value == null
                    ? CheckState.Unchecked
                    : allPinned
                        ? CheckState.Checked
                        : anyPinned && multiple
                            ? CheckState.Indeterminate
                            : CheckState.Unchecked;
                pinCheckBox.Enabled = value != null && canPin;
                codeSymbolLabel.Enabled = value != null && !multiple;
                SetSectionContentEnabled(value != null && !multiple);
                UpdateColorButtons();
                if (multiple && !commonColor.HasValue)
                {
                    ClearColorSelection();
                }
                UpdateEnabledState();
            }
            finally
            {
                loading = false;
            }

            RequestPreviewIfVisible();
        }


        public string GetPendingChangeDescription()
        {
            if (loading || item == null || multipleSelection) return null;

            var changes = new List<string>();
            if (!string.Equals(item.Name ?? string.Empty, nameTextBox.Text?.Trim() ?? string.Empty, StringComparison.Ordinal)) changes.Add("имя");
            if (item.NodeType != (folderCheckBox.Checked ? NodeType.Folder : NodeType.Item)) changes.Add("тип узла");
            var type = fileButton.Checked ? BookmarkType.File : symbolButton.Checked ? BookmarkType.Symbol : BookmarkType.Empty;
            if (item.Type != type) changes.Add("тип ссылки");
            if (!string.Equals(item.Path ?? string.Empty, type == BookmarkType.Empty ? string.Empty : pathTextBox.Text?.Trim() ?? string.Empty, StringComparison.Ordinal)) changes.Add("путь");
            if (!string.Equals(item.Symbol ?? string.Empty, type == BookmarkType.Symbol ? symbolTextBox.Text?.Trim() ?? string.Empty : string.Empty, StringComparison.Ordinal)) changes.Add("символ");
            if (!string.Equals(item.Project ?? string.Empty, type == BookmarkType.Symbol ? projectTextBox.Text?.Trim() ?? string.Empty : string.Empty, StringComparison.Ordinal)) changes.Add("проект");
            if (item.Line != (int)lineBox.Value) changes.Add("строка");
            if (item.Column != (int)columnBox.Value) changes.Add("колонка");
            if (!string.Equals(item.Comment ?? string.Empty, commentTextBox.Text ?? string.Empty, StringComparison.Ordinal)) changes.Add("комментарий");
            if (item.Color != selectedColor) changes.Add("цвет");

            if (changes.Count == 0) return null;
            return changes.Count == 1 ? "Изменение свойства: " + changes[0] : "Изменение свойств: " + string.Join(", ", changes);
        }

        public bool ApplyToCurrentItem()
        {
            if (loading || item == null || multipleSelection)
            {
                return false;
            }

            var changed = false;
            changed |= Set(ref item, item.Name, nameTextBox.Text?.Trim() ?? string.Empty, (x, v) => x.Name = v);
            var nodeType = folderCheckBox.Checked ? NodeType.Folder : NodeType.Item;
            if (item.NodeType != nodeType) { item.NodeType = nodeType; changed = true; }
            var type = fileButton.Checked ? BookmarkType.File : symbolButton.Checked ? BookmarkType.Symbol : BookmarkType.Empty;
            if (item.Type != type) { item.Type = type; changed = true; }
            var path = type == BookmarkType.Empty ? string.Empty : pathTextBox.Text?.Trim() ?? string.Empty;
            changed |= Set(ref item, item.Path, path, (x, v) => x.Path = v);
            var symbol = type == BookmarkType.Symbol ? symbolTextBox.Text?.Trim() ?? string.Empty : string.Empty;
            changed |= Set(ref item, item.Symbol, symbol, (x, v) => x.Symbol = v);
            var project = type == BookmarkType.Symbol ? projectTextBox.Text?.Trim() ?? string.Empty : string.Empty;
            changed |= Set(ref item, item.Project, project, (x, v) => x.Project = v);
            if (item.Line != (int)lineBox.Value) { item.Line = (int)lineBox.Value; changed = true; }
            if (item.Column != (int)columnBox.Value) { item.Column = (int)columnBox.Value; changed = true; }
            changed |= Set(ref item, item.Comment, commentTextBox.Text ?? string.Empty, (x, v) => x.Comment = v);
            if (item.Color != selectedColor) { item.Color = selectedColor; changed = true; }
            return changed;
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(3)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var colorRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 3) };
            colorRow.Controls.Add(CreatePalette());
            pinCheckBox.Text = "Pin";
            pinCheckBox.AutoSize = true;
            pinCheckBox.Margin = new Padding(10, 4, 0, 0);
            pinCheckBox.CheckedChanged += (_, __) => { if (!loading) PinChanged?.Invoke(this, EventArgs.Empty); };
            colorRow.Controls.Add(pinCheckBox);
            root.Controls.Add(colorRow, 0, 0);

            codeSymbolLabel.AutoSize = true;
            codeSymbolLabel.Dock = DockStyle.Fill;
            codeSymbolLabel.Font = new Font("Consolas", 10F, FontStyle.Bold);
            codeSymbolLabel.LinkColor = Color.FromArgb(86, 156, 214);
            codeSymbolLabel.ActiveLinkColor = Color.FromArgb(220, 220, 170);
            codeSymbolLabel.VisitedLinkColor = codeSymbolLabel.LinkColor;
            codeSymbolLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            codeSymbolLabel.Padding = new Padding(3, 3, 3, 5);
            codeSymbolLabel.AutoEllipsis = true;
            codeSymbolLabel.LinkClicked += (_, e) =>
            {
                if (e.Link?.LinkData is string symbol)
                {
                    SymbolLinkClicked?.Invoke(symbol);
                }
            };
            root.Controls.Add(codeSymbolLabel, 0, 1);

            accordion = new ExperimentalAccordionHost { Dock = DockStyle.Fill };
            accordion.StateChanged += (_, __) => LayoutStateChanged?.Invoke(this, EventArgs.Empty);
            root.Controls.Add(accordion, 0, 2);

            commentTextBox.Dock = DockStyle.Fill;
            commentTextBox.Multiline = true;
            commentTextBox.AcceptsReturn = true;
            commentTextBox.AcceptsTab = true;
            commentTextBox.ScrollBars = ScrollBars.Vertical;
            var commentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3) };
            commentHost.Controls.Add(commentTextBox);

            var codeRoot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            codeRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            codeRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var codeButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 2) };
            copyCodeButton.Text = string.Empty;
            copyCodeButton.Image = IconProvider.Get(AppIcon.Copy, 18);
            copyCodeButton.Size = new Size(30, 28);
            toolTip.SetToolTip(copyCodeButton, "Копировать код");
            copyCodeButton.Click += (_, __) =>
            {
                if (!string.IsNullOrEmpty(codeTextBox.Text))
                {
                    Clipboard.SetText(codeTextBox.Text);
                }
            };
            refreshCodeButton.Text = string.Empty;
            refreshCodeButton.Image = IconProvider.Get(AppIcon.Sync, 18);
            refreshCodeButton.Size = new Size(30, 28);
            toolTip.SetToolTip(refreshCodeButton, "Синхронизировать с текущей позицией");
            refreshCodeButton.Click += (_, __) => RefreshCodeRequested?.Invoke(this, EventArgs.Empty);
            codeButtons.Controls.Add(copyCodeButton);
            codeButtons.Controls.Add(refreshCodeButton);
            codeRoot.Controls.Add(codeButtons, 0, 0);

            codeTextBox.Dock = DockStyle.Fill;
            codeTextBox.ReadOnly = true;
            codeTextBox.WordWrap = false;
            codeTextBox.DetectUrls = false;
            codeTextBox.HideSelection = false;
            codeTextBox.ScrollBars = RichTextBoxScrollBars.Both;
            codeTextBox.Font = new Font("Consolas", 9F);
            codeRoot.Controls.Add(codeTextBox, 0, 1);

            livePreviewTextBox.Dock = DockStyle.Fill;
            livePreviewTextBox.ReadOnly = true;
            livePreviewTextBox.WordWrap = false;
            livePreviewTextBox.DetectUrls = false;
            livePreviewTextBox.HideSelection = false;
            livePreviewTextBox.ScrollBars = RichTextBoxScrollBars.Both;
            livePreviewTextBox.Font = new Font("Consolas", 9F);

            detailsHost.Dock = DockStyle.Fill;
            detailsHost.AutoScroll = true;
            detailsHost.Controls.Add(CreateDetailsLayout());
            commentSection = new ExperimentalAccordionSection("comment", "Комментарий", commentHost, 130, false);
            codeSection = new ExperimentalAccordionSection("code", "Код", codeRoot, 210, false);
            previewSection = new ExperimentalAccordionSection("preview", "Preview", livePreviewTextBox, 210, false);
            propertiesSection = new ExperimentalAccordionSection("properties", "Свойства", detailsHost, 180, false);
            previewSection.ExpandedChanged += (_, __) => RequestPreviewIfVisible();
            accordion.AddSection(propertiesSection);
            accordion.AddSection(commentSection);
            accordion.AddSection(codeSection);
            accordion.AddSection(previewSection);
        }


        public void RefreshCodePreview()
        {
            UpdateCodePreview(item);
        }

        public void ShowLivePreviewLoading()
        {
            livePreviewTextBox.Clear();
            livePreviewTextBox.Text = "Загрузка...";
        }

        public void ShowLivePreview(string preview)
        {
            var text = string.IsNullOrEmpty(preview) ? "Превью недоступно." : preview;
            CodePreviewHighlighter.Apply(livePreviewTextBox, text, item?.Path ?? string.Empty);
        }

        private void RequestPreviewIfVisible()
        {
            if (!loading && !multipleSelection && item != null && previewSection != null && previewSection.Expanded)
            {
                PreviewRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateCodePreview(DocumentItem value)
        {
            var state = value?.EditorState;
            var code = state?.HasSelection == true
                ? state.SelectedText ?? string.Empty
                : state?.CodePreview ?? state?.SelectedText ?? string.Empty;
            var path = value?.Path ?? string.Empty;
            UpdateCodeSymbolLinks(value);
            codeSymbolLabel.Visible = value != null;
            CodePreviewHighlighter.Apply(codeTextBox, code, path);
            copyCodeButton.Enabled = !string.IsNullOrEmpty(code);
        }

        private static string FormatCodeSymbol(DocumentItem value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value.Type == BookmarkType.File)
            {
                var fileName = Path.GetFileName(value.Path ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = value.Path ?? string.Empty;
                }

                return string.Format("{0} : {1}", fileName, Math.Max(1, value.Line));
            }

            return (value.Symbol ?? string.Empty);
        }

        private void UpdateCodeSymbolLinks(DocumentItem value)
        {
            var text = FormatCodeSymbol(value);
            codeSymbolLabel.Links.Clear();
            if (value == null || value.Type == BookmarkType.File || string.IsNullOrWhiteSpace(value.Symbol))
            {
                codeSymbolLabel.Text = text;
                return;
            }

            var parts = value.Symbol.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            codeSymbolLabel.Text = string.Join(".", parts);
            var displayOffset = 0;
            var symbolParts = new List<string>();
            foreach (var part in parts)
            {
                symbolParts.Add(part);
                codeSymbolLabel.Links.Add(displayOffset, part.Length, string.Join(".", symbolParts));
                displayOffset += part.Length + 1;
            }
        }

        private Control CreateDetailsLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 5, Padding = new Padding(2, 3, 2, 3) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            AddLabel(root, "Название:", 0, 0); nameTextBox.Dock = DockStyle.Fill; root.Controls.Add(nameTextBox, 1, 0);
            folderCheckBox.Text = "Папка"; folderCheckBox.AutoSize = true; root.Controls.Add(folderCheckBox, 2, 0);

            AddLabel(root, "Тип ссылки:", 0, 1);
            var typePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            SetupChoice(emptyButton, "Empty"); SetupChoice(symbolButton, "Symbol"); SetupChoice(fileButton, "File");
            typePanel.Controls.Add(emptyButton); typePanel.Controls.Add(symbolButton); typePanel.Controls.Add(fileButton);
            root.Controls.Add(typePanel, 1, 1);
            root.SetColumnSpan(typePanel, 3);

            AddLabel(root, "Файл:", 0, 2); pathTextBox.Dock = DockStyle.Fill; root.Controls.Add(pathTextBox, 1, 2); root.SetColumnSpan(pathTextBox, 3);
            AddLabel(root, "Символ:", 0, 3); symbolTextBox.Dock = DockStyle.Fill; root.Controls.Add(symbolTextBox, 1, 3);
            AddLabel(root, "Проект:", 2, 3); projectTextBox.Dock = DockStyle.Fill; root.Controls.Add(projectTextBox, 3, 3);

            AddLabel(root, "Позиция:", 0, 4);
            var pos = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            lineBox.Minimum = columnBox.Minimum = 1; lineBox.Maximum = columnBox.Maximum = 10000000; lineBox.Width = columnBox.Width = 80;
            pos.Controls.Add(new Label { Text = "Строка", AutoSize = true, Padding = new Padding(0, 5, 3, 0) }); pos.Controls.Add(lineBox);
            pos.Controls.Add(new Label { Text = "Колонка", AutoSize = true, Padding = new Padding(8, 5, 3, 0) }); pos.Controls.Add(columnBox);
            root.Controls.Add(pos, 1, 4); root.SetColumnSpan(pos, 3);

            for (var i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return root;
        }

        private FlowLayoutPanel CreatePalette()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            AddColorButton(panel, BookmarkColor.None, Color.White, "Без цвета");
            AddColorButton(panel, BookmarkColor.Red, Color.FromArgb(237, 28, 54), "Красный");
            AddColorButton(panel, BookmarkColor.Orange, Color.FromArgb(255, 140, 0), "Оранжевый");
            AddColorButton(panel, BookmarkColor.Yellow, Color.FromArgb(255, 229, 0), "Жёлтый");
            AddColorButton(panel, BookmarkColor.Green, Color.FromArgb(31, 201, 37), "Зелёный");
            AddColorButton(panel, BookmarkColor.Cyan, Color.FromArgb(0, 188, 212), "Бирюзовый");
            AddColorButton(panel, BookmarkColor.Blue, Color.FromArgb(22, 139, 205), "Синий");
            AddColorButton(panel, BookmarkColor.Purple, Color.FromArgb(156, 39, 176), "Фиолетовый");
            AddColorButton(panel, BookmarkColor.Gray, Color.FromArgb(128, 128, 128), "Серый");
            return panel;
        }

        private void AddColorButton(Control parent, BookmarkColor color, Color backColor, string tooltip)
        {
            var button = new Button { AutoSize = false, Width = 28, Height = 26, Margin = new Padding(0, 0, 4, 0), BackColor = backColor, FlatStyle = FlatStyle.Flat, Tag = color, Text = color == BookmarkColor.None ? "×" : string.Empty, UseVisualStyleBackColor = false };
            button.Click += (_, __) =>
            {
                selectedColor = color;
                UpdateColorButtons();
                if (!loading) ColorChanged?.Invoke(this, EventArgs.Empty);
            };
            new ToolTip().SetToolTip(button, tooltip);
            colorButtons[color] = button;
            parent.Controls.Add(button);
        }

        private void UpdateColorButtons()
        {
            foreach (var pair in colorButtons)
            {
                pair.Value.FlatAppearance.BorderSize = pair.Key == selectedColor ? 3 : 1;
                pair.Value.FlatAppearance.BorderColor = pair.Key == selectedColor ? SystemColors.Highlight : SystemColors.ControlDark;
            }
        }

        private void ClearColorSelection()
        {
            foreach (var button in colorButtons.Values)
            {
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = SystemColors.ControlDark;
            }
        }


        private void UpdateEnabledState()
        {
            var type = fileButton.Checked ? BookmarkType.File : symbolButton.Checked ? BookmarkType.Symbol : BookmarkType.Empty;
            pathTextBox.Enabled = type != BookmarkType.Empty;
            symbolTextBox.Enabled = type == BookmarkType.Symbol;
            projectTextBox.Enabled = type == BookmarkType.Symbol;
            lineBox.Enabled = type != BookmarkType.Empty;
            columnBox.Enabled = type != BookmarkType.Empty;
        }

        private void SetSectionContentEnabled(bool enabled)
        {
            if (commentSection != null) commentSection.ContentEnabled = enabled;
            if (codeSection != null) codeSection.ContentEnabled = enabled;
            if (previewSection != null) previewSection.ContentEnabled = enabled;
            if (propertiesSection != null) propertiesSection.ContentEnabled = enabled;
        }

        private void WireChanges(Control root)
        {
            foreach (Control control in root.Controls)
            {
                WireChanges(control);
                if (control is TextBox textBox) textBox.TextChanged += Changed;
                else if (control is CheckBox checkBox) checkBox.CheckedChanged += Changed;
                else if (control is RadioButton radioButton) radioButton.CheckedChanged += Changed;
                else if (control is NumericUpDown numeric) numeric.ValueChanged += Changed;
            }
        }

        private void Changed(object sender, EventArgs e)
        {
            if (sender == emptyButton || sender == symbolButton || sender == fileButton) UpdateEnabledState();
            if (!loading) ItemChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void SetupChoice(RadioButton button, string text)
        {
            button.Text = text; button.Appearance = Appearance.Button; button.TextAlign = ContentAlignment.MiddleCenter; button.AutoSize = false; button.Width = 74; button.Height = 28; button.Margin = new Padding(0, 0, 4, 0);
        }

        private static void AddLabel(TableLayoutPanel root, string text, int column, int row)
        {
            root.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 5, 0) }, column, row);
        }

        private static decimal Clamp(int value, decimal min, decimal max) => Math.Max(min, Math.Min(max, value));

        private static bool Set(ref DocumentItem target, string oldValue, string newValue, Action<DocumentItem, string> setter)
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return false;
            setter(target, newValue); return true;
        }
    }

    internal sealed class ExperimentalAccordionSection : Control
    {
        private const int HeaderHeight = 30;
        private const int DragHandleWidth = 34;
        private readonly Button headerButton;
        private readonly Panel dragHandle;
        private readonly Panel bodyPanel;
        private readonly string title;
        private readonly int contentHeight;
        private bool expanded;
        private Point dragStart;
        private bool dragArmed;

        public ExperimentalAccordionSection(string key, string title, Control content, int contentHeight, bool expanded)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            this.title = title;
            this.contentHeight = Math.Max(1, contentHeight);
            this.expanded = expanded;

            headerButton = new Button
            {
                FlatStyle = FlatStyle.System,
                TextAlign = ContentAlignment.MiddleLeft,
                TabStop = true
            };
            headerButton.Click += (_, __) => Expanded = !Expanded;

            dragHandle = new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.SizeAll,
                AccessibleName = "Изменить порядок секции",
                TabStop = false
            };
            dragHandle.Paint += (_, e) =>
            {
                var centerX = dragHandle.ClientSize.Width / 2;
                var top = Math.Max(5, (dragHandle.ClientSize.Height - 16) / 2);
                var bottom = Math.Min(dragHandle.ClientSize.Height - 5, top + 16);
                using (var pen = new Pen(SystemColors.ControlDarkDark, 2))
                {
                    e.Graphics.DrawLine(pen, centerX - 6, top, centerX - 6, bottom);
                    e.Graphics.DrawLine(pen, centerX, top, centerX, bottom);
                    e.Graphics.DrawLine(pen, centerX + 6, top, centerX + 6, bottom);
                }
            };
            dragHandle.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    dragStart = e.Location;
                    dragArmed = true;
                }
            };
            dragHandle.MouseMove += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || !dragArmed) return;
                var dragBounds = new Rectangle(
                    dragStart.X - SystemInformation.DragSize.Width / 2,
                    dragStart.Y - SystemInformation.DragSize.Height / 2,
                    SystemInformation.DragSize.Width,
                    SystemInformation.DragSize.Height);
                if (dragBounds.Contains(e.Location)) return;

                dragArmed = false;
                DoDragDrop(this, DragDropEffects.Move);
            };
            dragHandle.MouseUp += (_, __) => dragArmed = false;

            bodyPanel = new Panel { BorderStyle = BorderStyle.FixedSingle };
            content.Dock = DockStyle.Fill;
            bodyPanel.Controls.Add(content);

            Controls.Add(headerButton);
            Controls.Add(dragHandle);
            Controls.Add(bodyPanel);
            UpdateExpandedState();
        }

        public event EventHandler ExpandedChanged;

        public string Key { get; }

        public bool Expanded
        {
            get => expanded;
            set
            {
                if (expanded == value) return;
                expanded = value;
                UpdateExpandedState();
                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool ContentEnabled
        {
            get => bodyPanel.Enabled;
            set => bodyPanel.Enabled = value;
        }

        public int CurrentHeight => HeaderHeight + (expanded ? contentHeight : 0);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var handleWidth = Math.Min(DragHandleWidth, Math.Max(0, ClientSize.Width));
            headerButton.SetBounds(0, 0, Math.Max(0, ClientSize.Width - handleWidth), HeaderHeight);
            dragHandle.SetBounds(Math.Max(0, ClientSize.Width - handleWidth), 0, handleWidth, HeaderHeight);
            bodyPanel.SetBounds(0, HeaderHeight, Math.Max(0, ClientSize.Width), contentHeight);
        }

        private void UpdateExpandedState()
        {
            headerButton.Text = (expanded ? "▼  " : "▶  ") + title;
            bodyPanel.Visible = expanded;
            PerformLayout();
        }
    }

    internal sealed class ExperimentalAccordionHost : Panel
    {
        private readonly List<ExperimentalAccordionSection> sections = new List<ExperimentalAccordionSection>();
        private bool layingOut;
        private bool applyingState;

        public ExperimentalAccordionHost()
        {
            AutoScroll = true;
            AllowDrop = true;
            DragEnter += AccordionDragEnter;
            DragOver += AccordionDragEnter;
            DragDrop += AccordionDragDrop;
        }

        public event EventHandler StateChanged;

        public IList<string> SectionOrder
        {
            get
            {
                var result = new List<string>();
                foreach (var section in sections) result.Add(section.Key);
                return result;
            }
        }

        public IList<string> ExpandedSections
        {
            get
            {
                var result = new List<string>();
                foreach (var section in sections)
                {
                    if (section.Expanded) result.Add(section.Key);
                }
                return result;
            }
        }

        public void AddSection(ExperimentalAccordionSection section)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            sections.Add(section);
            section.ExpandedChanged += SectionExpandedChanged;
            Controls.Add(section);
            PerformLayout();
        }

        public void ApplyState(IEnumerable<string> sectionOrder, IEnumerable<string> expandedSections)
        {
            applyingState = true;
            try
            {
                var ordered = new List<ExperimentalAccordionSection>();
                foreach (var key in sectionOrder ?? new string[0])
                {
                    var section = sections.Find(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (section != null && !ordered.Contains(section)) ordered.Add(section);
                }
                foreach (var section in sections)
                {
                    if (!ordered.Contains(section)) ordered.Add(section);
                }
                sections.Clear();
                sections.AddRange(ordered);

                var expanded = new HashSet<string>(expandedSections ?? new string[0], StringComparer.OrdinalIgnoreCase);
                foreach (var section in sections) section.Expanded = expanded.Contains(section.Key);
                PerformLayout();
            }
            finally
            {
                applyingState = false;
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (layingOut) return;

            layingOut = true;
            try
            {
                var totalHeight = 0;
                foreach (var section in sections)
                {
                    totalHeight += section.CurrentHeight;
                }

                var needsVerticalScrollBar = totalHeight > ClientSize.Height;
                var width = Math.Max(0, ClientSize.Width - (needsVerticalScrollBar ? SystemInformation.VerticalScrollBarWidth : 0));
                var y = AutoScrollPosition.Y;
                foreach (var section in sections)
                {
                    section.SetBounds(0, y, width, section.CurrentHeight);
                    y += section.CurrentHeight;
                }

                AutoScrollMinSize = new Size(0, totalHeight);
            }
            finally
            {
                layingOut = false;
            }
        }

        private void SectionExpandedChanged(object sender, EventArgs e)
        {
            PerformLayout();
            if (!applyingState) StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void AccordionDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(ExperimentalAccordionSection))
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void AccordionDragDrop(object sender, DragEventArgs e)
        {
            var section = e.Data.GetData(typeof(ExperimentalAccordionSection)) as ExperimentalAccordionSection;
            var oldIndex = sections.IndexOf(section);
            if (oldIndex < 0) return;

            var location = PointToClient(new Point(e.X, e.Y));
            var insertionIndex = sections.Count;
            for (var i = 0; i < sections.Count; i++)
            {
                var candidate = sections[i];
                if (location.Y < candidate.Top + candidate.Height / 2)
                {
                    insertionIndex = i;
                    break;
                }
            }

            sections.RemoveAt(oldIndex);
            if (insertionIndex > oldIndex) insertionIndex--;
            insertionIndex = Math.Max(0, Math.Min(insertionIndex, sections.Count));
            sections.Insert(insertionIndex, section);
            PerformLayout();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
