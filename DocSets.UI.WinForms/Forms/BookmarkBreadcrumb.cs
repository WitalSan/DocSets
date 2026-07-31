using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BreadcrumbItem
    {
        public string Text { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public object Value { get; set; }
        public bool Selectable { get; set; }
    }

    internal sealed class BreadcrumbItemSelectedEventArgs : EventArgs
    {
        public BreadcrumbItemSelectedEventArgs(BreadcrumbItem item) => Item = item;
        public BreadcrumbItem Item { get; }
    }

    /// <summary>
    /// Самодостаточный breadcrumb: формирует строку, ссылки, tooltip и событие выбора.
    /// Внешний код передаёт только элементы и не настраивает его отображение.
    /// </summary>
    internal sealed class BookmarkBreadcrumb : LinkLabel
    {
        private sealed class ItemRange
        {
            public BreadcrumbItem Item;
            public int Start;
            public int Length;
        }

        private readonly ToolTip toolTip = new ToolTip();
        private readonly List<ItemRange> ranges = new List<ItemRange>();
        private BreadcrumbItem hoveredItem;

        public BookmarkBreadcrumb()
        {
            AutoSize = false;
            Dock = DockStyle.Fill;
            TextAlign = ContentAlignment.MiddleLeft;
            Font = new Font("Consolas", 10F, FontStyle.Bold);
            LinkColor = Color.FromArgb(86, 156, 214);
            ActiveLinkColor = Color.FromArgb(220, 220, 170);
            VisitedLinkColor = LinkColor;
            ForeColor = SystemColors.ControlText;
            LinkBehavior = LinkBehavior.HoverUnderline;
            Padding = new Padding(3, 3, 3, 5);
            AutoEllipsis = true;

            LinkClicked += (_, e) =>
            {
                if (e.Link?.LinkData is BreadcrumbItem item)
                    ItemSelected?.Invoke(this, new BreadcrumbItemSelectedEventArgs(item));
            };
            MouseMove += OnBreadcrumbMouseMove;
            MouseLeave += (_, __) =>
            {
                hoveredItem = null;
                toolTip.Hide(this);
            };
        }

        public event EventHandler<BreadcrumbItemSelectedEventArgs> ItemSelected;

        public IReadOnlyList<BreadcrumbItem> Items { get; private set; } =
            Array.Empty<BreadcrumbItem>();

        public void SetItems(IEnumerable<BreadcrumbItem> items)
        {
            Items = (items ?? Enumerable.Empty<BreadcrumbItem>())
                .Where(x => x != null)
                .ToArray();
            Links.Clear();
            ranges.Clear();
            hoveredItem = null;
            toolTip.Hide(this);

            Text = string.Join(".", Items.Select(x => x.Text ?? string.Empty));
            var offset = 0;
            foreach (var item in Items)
            {
                var length = (item.Text ?? string.Empty).Length;
                ranges.Add(new ItemRange { Item = item, Start = offset, Length = length });
                if (item.Selectable && length > 0)
                    Links.Add(offset, length, item);
                offset += length + 1;
            }
        }

        public BreadcrumbItem GetItemAt(Point point)
        {
            foreach (var range in ranges)
            {
                var before = Text.Substring(0, Math.Min(Text.Length, range.Start));
                var x = Padding.Left + Measure(before);
                var width = Math.Max(1, Measure(range.Item.Text));
                if (point.X >= x && point.X <= x + width &&
                    point.Y >= Padding.Top && point.Y <= Height - Padding.Bottom)
                    return range.Item;
            }
            return null;
        }

        private void OnBreadcrumbMouseMove(object sender, MouseEventArgs e)
        {
            var item = GetItemAt(e.Location);
            if (ReferenceEquals(item, hoveredItem)) return;
            hoveredItem = item;
            toolTip.Hide(this);
            if (!string.IsNullOrWhiteSpace(item?.Comment))
                toolTip.Show(item.Comment.Trim(), this,
                    e.X + DpiService.Scale(this, 12),
                    e.Y + DpiService.Scale(this, 18), 30000);
        }

        private int Measure(string text) => TextRenderer.MeasureText(
            text ?? string.Empty, Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

        protected override void Dispose(bool disposing)
        {
            if (disposing) toolTip.Dispose();
            base.Dispose(disposing);
        }
    }
}
