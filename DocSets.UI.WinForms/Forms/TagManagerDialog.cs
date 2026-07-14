using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class TagManagerDialog : Form
    {
        private readonly DocSetsViewModel viewModel;
        private readonly ListBox list = new ListBox();
        private readonly TextBox name = new TextBox();
        private readonly TextBox color = new TextBox();
        private readonly ComboBox icon = new ComboBox();
        private readonly SplitContainer split = new SplitContainer();
        private bool loading;
        private int IconSize => Math.Max(20, (int)Math.Round(24f * DeviceDpi / 96f));

        public TagManagerDialog(DocSetsViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            Text = "Теги";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 440);
            MinimumSize = new Size(620, 380);

            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1MinSize = 220;
            //split.Panel2MinSize = 300;

            list.Dock = DockStyle.Fill;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.IntegralHeight = false;
            list.SelectedIndexChanged += (_, __) => LoadSelected();
            list.DrawItem += DrawTagItem;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
            var add = new Button { Text = "+ Добавить", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 3, 8, 3) };
            var remove = new Button { Text = "Удалить", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 3, 8, 3) };
            add.Click += async (_, __) => { try { var tag = await viewModel.AddTagAsync("Новый тег " + (viewModel.Tags.Count + 1)); RefreshList(tag); } catch (Exception ex) { MessageBox.Show(this, ex.GetBaseException().Message, "Теги"); } };
            remove.Click += async (_, __) => { if (!(list.SelectedItem is TagDefinition tag)) return; await viewModel.DeleteTagAsync(tag.Id); RefreshList(null); };
            buttons.Controls.Add(add); buttons.Controls.Add(remove);
            split.Panel1.Controls.Add(list);

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(16), ColumnCount = 2, RowCount = 4 };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddRow(editor, 0, "Имя:", name);
            AddRow(editor, 1, "Цвет (#RRGGBB):", color);

            icon.DropDownStyle = ComboBoxStyle.DropDownList;
            icon.DrawMode = DrawMode.OwnerDrawFixed;
            icon.Items.AddRange(TagIconProvider.Names.Cast<object>().ToArray());
            icon.DrawItem += DrawIconItem;
            AddRow(editor, 2, "Иконка:", icon);

            name.TextChanged += (_, __) => Apply();
            color.TextChanged += (_, __) => Apply();
            icon.SelectedIndexChanged += (_, __) => Apply();
            split.Panel2.Controls.Add(editor);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(split, 0, 0);
            layout.Controls.Add(buttons, 0, 1);
            Controls.Add(layout);

            Shown += (_, __) => ApplyDpiMetrics();
            DpiChanged += (_, __) => BeginInvoke(new Action(ApplyDpiMetrics));
            FormClosed += async (_, __) => await viewModel.SaveAsync();
            RefreshList(null);
        }

        private void ApplyDpiMetrics()
        {
            var scale = DeviceDpi / 96f;
            list.ItemHeight = Math.Max(Font.Height + 10, (int)Math.Round(34 * scale));
            icon.ItemHeight = Math.Max(Font.Height + 10, (int)Math.Round(34 * scale));
            icon.DropDownWidth = Math.Max(icon.Width, (int)Math.Round(220 * scale));
            split.SplitterDistance = Math.Max(split.Panel1MinSize, (int)Math.Round(270 * scale));
            list.Invalidate(); icon.Invalidate();
        }

        private static void AddRow(TableLayoutPanel panel, int row, string caption, Control control)
        {
            panel.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 12, 9) }, 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(0, 6, 0, 6); panel.Controls.Add(control, 1, row);
        }

        private void DrawTagItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground(); if (e.Index < 0) return;
            var tag = list.Items[e.Index] as TagDefinition; var size = Math.Min(IconSize, e.Bounds.Height - 6);
            e.Graphics.DrawImage(TagIconProvider.Get(tag?.Icon, size), e.Bounds.Left + 5, e.Bounds.Top + (e.Bounds.Height-size)/2, size, size);
            TextRenderer.DrawText(e.Graphics, tag?.Name ?? "", e.Font, new Rectangle(e.Bounds.Left + size + 12, e.Bounds.Top, e.Bounds.Width-size-14, e.Bounds.Height), e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private void DrawIconItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground(); if (e.Index < 0) return;
            var key = icon.Items[e.Index] as string ?? ""; var size = Math.Min(IconSize, e.Bounds.Height - 6);
            e.Graphics.DrawImage(TagIconProvider.Get(key, size), e.Bounds.Left + 5, e.Bounds.Top + (e.Bounds.Height-size)/2, size, size);
            var text = string.IsNullOrEmpty(key) ? "Без иконки" : key;
            TextRenderer.DrawText(e.Graphics, text, e.Font, new Rectangle(e.Bounds.Left + size + 12, e.Bounds.Top, e.Bounds.Width-size-14, e.Bounds.Height), e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }

        private void RefreshList(TagDefinition select)
        {
            list.BeginUpdate(); list.Items.Clear(); foreach (var tag in viewModel.Tags.Where(x => x != null)) list.Items.Add(tag); list.EndUpdate();
            if (select != null) list.SelectedItem = select; else if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        private void LoadSelected()
        {
            loading = true; var tag = list.SelectedItem as TagDefinition;
            name.Text = tag?.Name ?? ""; color.Text = tag?.Color ?? ""; icon.SelectedItem = tag?.Icon ?? ""; if (icon.SelectedIndex < 0) icon.SelectedIndex = 0; loading = false;
        }

        private void Apply()
        {
            if (loading || !(list.SelectedItem is TagDefinition tag)) return;
            tag.Name = name.Text.Trim(); tag.Color = color.Text.Trim(); tag.Icon = icon.SelectedItem as string ?? ""; list.Invalidate();
        }
    }
}