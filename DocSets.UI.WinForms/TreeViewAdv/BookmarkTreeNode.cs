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
            Image = item.IsFolder ? SystemIcons.WinLogo.ToBitmap() : SystemIcons.Information.ToBitmap();
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
    }
}
