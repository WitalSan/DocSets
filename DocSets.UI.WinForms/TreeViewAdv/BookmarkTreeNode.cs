using Aga.Controls.Tree;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DocSets
{
    internal sealed class BookmarkTreeNode : Node
    {

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
                return IconProvider.Get(AppIcon.Item, 16);

            if (item.NodeType == NodeType.Folder)
            {
                switch (item.Type)
                {
                    case BookmarkType.File: return IconProvider.Get(AppIcon.FolderLinkFile, 16);
                    case BookmarkType.Symbol: return IconProvider.Get(AppIcon.FolderLinkSymbol, 16);
                    default: return IconProvider.Get(AppIcon.Folder, 16);
                }
            }

            switch (item.Type)
            {
                case BookmarkType.File: return IconProvider.Get(AppIcon.LinkFile, 16);
                case BookmarkType.Symbol: return IconProvider.Get(AppIcon.LinkSymbol, 16);
                default: return IconProvider.Get(AppIcon.Item, 16);
            }
        }
    }
}
