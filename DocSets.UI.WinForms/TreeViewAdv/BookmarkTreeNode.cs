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
        private static readonly Image EmptyItemImage = CreateEmptyItemImage();
        private static readonly Image SymbolBookmarkImage = CreateSymbolBookmarkImage();
        private static readonly Image FileBookmarkImage = CreateFileBookmarkImage();
        private static readonly Image EmptyFolderImage = CreateFolderImage(BookmarkType.Empty);
        private static readonly Image SymbolFolderImage = CreateFolderImage(BookmarkType.Symbol);
        private static readonly Image FileFolderImage = CreateFolderImage(BookmarkType.File);

        public BookmarkTreeNode(DocumentItem item)
        {
            Item = item;
            Tag = item;
            Image = GetImage(item);
            RebuildChildren();
        }

        public DocumentItem Item { get; }

        public override bool IsLeaf => Item.NodeType != NodeType.Folder || Item.Children.Count == 0;

        public string Name
        {
            get => Item.NodeType == NodeType.Folder ? (string.IsNullOrWhiteSpace(Item.Name) ? "Новая папка" : Item.Name) : Item.Name;
            set
            {
                Item.Name = value ?? string.Empty;
                NotifyModel();
            }
        }

        public string Kind => Item.NodeType == NodeType.Folder ? "Папка" : "Закладка";
        public string File => Item.Type == BookmarkType.Empty ? string.Empty : Item.Path;
        public string Line => Item.Type == BookmarkType.Empty ? string.Empty : Item.Line.ToString();
        public string Project => Item.Type == BookmarkType.Symbol ? Item.Project ?? string.Empty : string.Empty;
        public string Symbol => Item.Type == BookmarkType.Symbol ? Item.Symbol ?? string.Empty : string.Empty;
        public string Comment => Item.CommentFirstLine;

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
            if (item == null)
                return EmptyItemImage;

            if (item.NodeType == NodeType.Folder)
            {
                switch (item.Type)
                {
                    case BookmarkType.File: return FileFolderImage;
                    case BookmarkType.Empty: return EmptyFolderImage;
                    default: return SymbolFolderImage;
                }
            }

            switch (item.Type)
            {
                case BookmarkType.File: return FileBookmarkImage;
                case BookmarkType.Empty: return EmptyItemImage;
                default: return SymbolBookmarkImage;
            }
        }

        private static Image CreateFolderImage(BookmarkType type)
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

                if (type == BookmarkType.Symbol)
                    DrawSymbolOverlay(g);
                else if (type == BookmarkType.File)
                    DrawFileOverlay(g);
            }

            return bitmap;
        }

        private static Image CreateEmptyItemImage()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var fill = new SolidBrush(Color.FromArgb(244, 244, 244)))
                using (var border = new Pen(Color.FromArgb(145, 145, 145)))
                {
                    var rect = new Rectangle(3, 2, 10, 12);
                    g.FillRectangle(fill, rect);
                    g.DrawRectangle(border, rect);
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
                    DrawFileMark(g, mark, markBorder);
                }
            }

            return bitmap;
        }

        private static void DrawSymbolOverlay(Graphics g)
        {
            using (var fill = new SolidBrush(Color.FromArgb(88, 134, 207)))
            using (var border = new Pen(Color.FromArgb(41, 82, 157)))
            using (var white = new Pen(Color.White, 1f))
            {
                g.FillRectangle(fill, 9, 8, 6, 7);
                g.DrawRectangle(border, 9, 8, 6, 7);
                g.DrawLine(white, 11, 10, 13, 10);
                g.DrawLine(white, 11, 12, 13, 12);
            }
        }

        private static void DrawFileOverlay(Graphics g)
        {
            using (var file = new SolidBrush(Color.FromArgb(246, 246, 246)))
            using (var border = new Pen(Color.FromArgb(105, 117, 138)))
            using (var mark = new SolidBrush(Color.FromArgb(66, 166, 92)))
            using (var markBorder = new Pen(Color.FromArgb(40, 120, 62)))
            {
                g.FillRectangle(file, 9, 7, 6, 8);
                g.DrawRectangle(border, 9, 7, 6, 8);
                DrawFileMark(g, mark, markBorder, 8, 10, 6);
            }
        }

        private static void DrawFileMark(Graphics g, Brush mark, Pen markBorder, int x = 1, int y = 9, int size = 7)
        {
            g.FillEllipse(mark, x, y, size, size);
            g.DrawEllipse(markBorder, x, y, size, size);
            using (var pen = new Pen(Color.White, 1.2f))
            {
                g.DrawLine(pen, x + 2, y + 3, x + 3, y + 4);
                g.DrawLine(pen, x + 3, y + 4, x + size - 2, y + 1);
            }
        }
    }
}
