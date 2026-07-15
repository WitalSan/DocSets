using Aga.Controls.Tree;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DocSets
{
    internal sealed class BookmarkTreeNode : Node
    {
        public static System.Func<DocumentItem, DocumentItem> PinResolver { get; set; }
        public static System.Func<DocumentItem, bool> PinChecker { get; set; }
        public static System.Func<DocumentItem, string> TagTextResolver { get; set; }
        public static System.Func<DocumentItem, Image> TagImageResolver { get; set; }
        public static System.Func<int> IconSizeResolver { get; set; }

        private DocumentItem ResolvedItem => Item != null && (Item.IsPinItem || Item.IsRecentItem) ? PinResolver?.Invoke(Item) : Item;

        public BookmarkTreeNode(DocumentItem item) : this(item, true)
        {
        }

        private BookmarkTreeNode(DocumentItem item, bool rebuildChildren)
        {
            Item = item;
            Tag = item;
            Image = GetImage(item);
            PinnedImage = GetPinnedImage(item);
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
        public Image PinnedImage { get; }

        public override bool IsLeaf => Item.NodeType != NodeType.Folder || Item.Children.Count == 0;

        public string Name
        {
            get
            {
                var resolved = ResolvedItem;
                var name = resolved?.NodeType == NodeType.Folder ? (string.IsNullOrWhiteSpace(resolved.Name) ? "Новая папка" : resolved.Name) : resolved?.Name;
                if (Item?.IsRecentItem == true && !string.IsNullOrWhiteSpace(Item.Name)) return Item.Name;
                return name ?? "<missing>";
            }
            set
            {
                var target = ResolvedItem ?? Item;
                var newValue = value ?? string.Empty;
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
        public string Solution => ResolvedItem?.ModifiedInSolution ?? string.Empty;
        public string Modified => FormatDate(ResolvedItem?.ModifiedAtUtc);
        public string Created => FormatDate(ResolvedItem?.CreatedAtUtc);
        public string Tags => TagTextResolver?.Invoke(ResolvedItem) ?? string.Empty;
        public Image TagImage => TagImageResolver?.Invoke(ResolvedItem);
        public string ColorMarker => ResolvedItem == null || ResolvedItem.Color == BookmarkColor.None ? string.Empty : "■";

        private static string FormatDate(DateTimeOffset? value)
        {
            return value.HasValue ? value.Value.ToLocalTime().ToString("g") : string.Empty;
        }

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
            var resolved = item != null && (item.IsPinItem || item.IsRecentItem) ? PinResolver?.Invoke(item) : item;
            return IconProvider.Get(GetBaseIcon(resolved), IconSizeResolver?.Invoke() ?? 16);
        }

        private static Image GetPinnedImage(DocumentItem item)
        {
            var resolved = item != null && (item.IsPinItem || item.IsRecentItem) ? PinResolver?.Invoke(item) : item;
            var isPinned = item != null && (item.IsPinItem || (resolved != null && PinChecker?.Invoke(resolved) == true));
            return isPinned ? IconProvider.Get(AppIcon.PinOverlay, IconSizeResolver?.Invoke() ?? 16) : null;
        }

        private static AppIcon GetBaseIcon(DocumentItem item)
        {
            if (item == null)
                return AppIcon.Item;

            if (item.NodeType == NodeType.Folder)
            {
                switch (item.Type)
                {
                    case BookmarkType.File: return AppIcon.FolderLinkFile;
                    case BookmarkType.Symbol: return AppIcon.FolderLinkSymbol;
                    default: return AppIcon.Folder;
                }
            }

            switch (item.Type)
            {
                case BookmarkType.File: return AppIcon.LinkFile;
                case BookmarkType.Symbol: return AppIcon.LinkSymbol;
                default: return AppIcon.Item;
            }
        }

    }
}
