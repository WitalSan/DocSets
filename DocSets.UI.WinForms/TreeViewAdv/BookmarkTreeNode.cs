using Aga.Controls.Tree;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace DocSets
{
    internal sealed class BookmarkTreeNode : Node
    {
        private static readonly Image FolderImage = CreateFolderImage();
        private static readonly Image SymbolBookmarkImage = CreateSymbolBookmarkImage();
        private static readonly Image FileBookmarkImage = CreateFileBookmarkImage();

        public BookmarkTreeNode(DocumentItem item)
        {
            Item = item;
            Tag = item;
            Image = GetImage(item);
            RebuildChildren();
        }

        public DocumentItem Item { get; }

        public override bool IsLeaf => !Item.IsFolder || Item.Children.Count == 0;

        public string Name
        {
            get => Item.IsFolder ? (string.IsNullOrWhiteSpace(Item.Name) ? "Новая папка" : Item.Name) : Item.Name;
            set
            {
                Item.Name = value ?? string.Empty;
                NotifyModel();
            }
        }

        public string Kind => Item.IsFolder ? "Папка" : "Закладка";
        public string File => Item.IsFolder ? string.Empty : Item.Path;
        public string Line => Item.IsFolder ? string.Empty : Item.Line.ToString();
        public string Project => Item.Project ?? string.Empty;
        public string Symbol => Item.Symbol ?? string.Empty;
        public string Comment => Item.IsFolder ? string.Empty : Item.CommentFirstLine;

        public void RebuildChildren()
        {
            Nodes.Clear();
            foreach (var child in Item.Children)
                Nodes.Add(new BookmarkTreeNode(child));
        }

        public static List<DocumentItem> CloneItems(IEnumerable<BookmarkTreeNode> nodes)
        {
            return nodes.Select(n => JsonConvert.DeserializeObject<DocumentItem>(JsonConvert.SerializeObject(n.Item))).Where(x => x != null).ToList();
        }

        private static Image GetImage(DocumentItem item)
        {
            if (item == null || item.IsFolder)
            {
                return FolderImage;
            }

            return item.Type == BookmarkType.File ? FileBookmarkImage : SymbolBookmarkImage;
        }

        private static Image CreateFolderImage()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var tab = new SolidBrush(Color.FromArgb(250, 212, 96)))
                using (var body = new SolidBrush(Color.FromArgb(245, 184, 64)))
                using (var border = new Pen(Color.FromArgb(166, 120, 28)))
                {
                    g.FillRectangle(tab, 2, 3, 6, 3);
                    g.DrawRectangle(border, 2, 3, 6, 3);
                    g.FillRectangle(body, 1, 6, 14, 8);
                    g.DrawRectangle(border, 1, 6, 14, 8);
                }
            }

            return bitmap;
        }

        private static Image CreateSymbolBookmarkImage()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var fill = new SolidBrush(Color.FromArgb(88, 134, 207)))
                using (var border = new Pen(Color.FromArgb(41, 82, 157)))
                using (var white = new Pen(Color.White, 1.5f))
                {
                    var rect = new Rectangle(3, 2, 10, 12);
                    g.FillRectangle(fill, rect);
                    g.DrawRectangle(border, rect);
                    g.DrawLine(white, 6, 5, 10, 5);
                    g.DrawLine(white, 6, 8, 10, 8);
                    g.DrawLine(white, 6, 11, 9, 11);
                }
            }

            return bitmap;
        }

        private static Image CreateFileBookmarkImage()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var file = new SolidBrush(Color.FromArgb(246, 246, 246)))
                using (var fold = new SolidBrush(Color.FromArgb(220, 226, 237)))
                using (var border = new Pen(Color.FromArgb(105, 117, 138)))
                using (var mark = new SolidBrush(Color.FromArgb(66, 166, 92)))
                using (var markBorder = new Pen(Color.FromArgb(40, 120, 62)))
                {
                    var points = new[]
                    {
                        new Point(3, 1), new Point(10, 1), new Point(13, 4),
                        new Point(13, 14), new Point(3, 14)
                    };
                    g.FillPolygon(file, points);
                    g.DrawPolygon(border, points);
                    g.FillPolygon(fold, new[] { new Point(10, 1), new Point(13, 4), new Point(10, 4) });
                    g.DrawLine(border, 10, 1, 10, 4);
                    g.DrawLine(border, 10, 4, 13, 4);

                    g.FillEllipse(mark, 1, 9, 7, 7);
                    g.DrawEllipse(markBorder, 1, 9, 7, 7);
                    using (var pen = new Pen(Color.White, 1.4f))
                    {
                        g.DrawLine(pen, 3, 12, 4, 13);
                        g.DrawLine(pen, 4, 13, 6, 10);
                    }
                }
            }

            return bitmap;
        }
    }
}
