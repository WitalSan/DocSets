using Aga.Controls.Tree;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DocSets
{
    internal sealed class BookmarkTreeNode : Node
    {
        public static System.Func<DocumentItem, DocumentItem> PinResolver { get; set; }
        public static System.Func<DocumentItem, bool> PinChecker { get; set; }

        private DocumentItem ResolvedItem => Item != null && Item.IsPinItem ? PinResolver?.Invoke(Item) : Item;

        public BookmarkTreeNode(DocumentItem item) : this(item, true)
        {
        }

        private BookmarkTreeNode(DocumentItem item, bool rebuildChildren)
        {
            Item = item;
            Tag = item;
            Image = GetImage(item != null && item.IsPinItem ? PinResolver?.Invoke(item) : item);
            if (rebuildChildren)
                RebuildChildren();
        }

        public static BookmarkTreeNode CreateFiltered(DocumentItem item, System.Func<DocumentItem, bool> matches)
        {
            if (item == null)
                return null;

            var node = new BookmarkTreeNode(item, false);
            foreach (var child in item.Children)
            {
                var childNode = CreateFiltered(child, matches);
                if (childNode != null)
                    node.Nodes.Add(childNode);
            }

            return matches(item) || node.Nodes.Count > 0 ? node : null;
        }

        public DocumentItem Item { get; }

        public override bool IsLeaf => Item.NodeType != NodeType.Folder || Item.Children.Count == 0;

        public string Name
        {
            get
            {
                var resolved = ResolvedItem;
                var name = resolved?.NodeType == NodeType.Folder ? (string.IsNullOrWhiteSpace(resolved.Name) ? "Новая папка" : resolved.Name) : resolved?.Name;
                var isPinned = Item.IsPinItem || (resolved != null && PinChecker?.Invoke(resolved) == true);
                return isPinned ? "* " + (name ?? "<missing>") : name;
            }
            set
            {
                var target = ResolvedItem ?? Item;
                var newValue = value ?? string.Empty;
                if ((Item.IsPinItem || (target != null && PinChecker?.Invoke(target) == true)) && newValue.StartsWith("* "))
                    newValue = newValue.Substring(2);
                target.Name = newValue;
                NotifyModel();
            }
        }

        public string Kind => ResolvedItem?.NodeType == NodeType.Folder ? "Папка" : "Закладка";
        public string File => ResolvedItem == null || ResolvedItem.Type == BookmarkType.Empty ? string.Empty : ResolvedItem.Path;
        public string Line => ResolvedItem == null || ResolvedItem.Type == BookmarkType.Empty ? string.Empty : ResolvedItem.Line.ToString();
        public string Project => ResolvedItem?.Type == BookmarkType.Symbol ? ResolvedItem.Project ?? string.Empty : string.Empty;
        public string Symbol => ResolvedItem?.Type == BookmarkType.Symbol ? ResolvedItem.Symbol ?? string.Empty : string.Empty;
        public string Comment => ResolvedItem?.CommentFirstLine ?? string.Empty;
        public string ColorMarker => ResolvedItem == null || ResolvedItem.Color == BookmarkColor.None ? string.Empty : "■";

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
