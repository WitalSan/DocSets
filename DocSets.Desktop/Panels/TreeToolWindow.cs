using System.Collections.ObjectModel;

namespace DocSets.Desktop.Panels;

internal sealed class TreeToolWindow : DesktopDockContent
{
    private readonly TreeView tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
        LabelEdit = true
    };

    public TreeToolWindow() : base("tree", "DocSets")
    {
        Controls.Add(tree);
        tree.AfterSelect += (_, e) => SelectedItemChanged?.Invoke(this, e.Node.Tag as DocumentItem);
        tree.AfterLabelEdit += (_, e) =>
        {
            if (e.CancelEdit || string.IsNullOrWhiteSpace(e.Label) || e.Node?.Tag is not DocumentItem item) return;
            item.Name = e.Label.Trim();
            ItemEdited?.Invoke(this, item);
        };
        tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2) RenameSelected();
            if (e.KeyCode == Keys.Delete) DeleteRequested?.Invoke(this, SelectedItem);
        };
    }

    public event EventHandler<DocumentItem> SelectedItemChanged;
    public event EventHandler<DocumentItem> ItemEdited;
    public event EventHandler<DocumentItem> DeleteRequested;
    public DocumentItem SelectedItem => tree.SelectedNode?.Tag as DocumentItem;

    public void LoadDocument(DocumentSetsState state, string name, DesktopSettings settings)
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        if (state != null)
        {
            var root = new TreeNode(name) { Tag = null, Name = "__root" };
            tree.Nodes.Add(root);
            foreach (var item in state.Sets) root.Nodes.Add(CreateNode(item, settings));
            root.Expand();
            SelectById(settings?.SelectedItemId);
        }
        tree.EndUpdate();
    }

    public void RefreshSelectedCaption()
    {
        if (tree.SelectedNode?.Tag is DocumentItem item) tree.SelectedNode.Text = Caption(item);
    }

    public void RenameSelected()
    {
        if (tree.SelectedNode?.Tag is DocumentItem) tree.SelectedNode.BeginEdit();
    }

    public void SelectItem(DocumentItem item)
    {
        if (item == null) return;
        var node = FindNode(tree.Nodes, item.Id);
        if (node == null) return;
        tree.SelectedNode = node;
        node.EnsureVisible();
    }

    public void CaptureState(DesktopSettings settings)
    {
        settings.SelectedItemId = SelectedItem?.Id ?? "";
        settings.ExpandedItemIds.Clear();
        CaptureExpanded(tree.Nodes, settings.ExpandedItemIds);
    }

    private static TreeNode CreateNode(DocumentItem item, DesktopSettings settings)
    {
        var node = new TreeNode(Caption(item)) { Tag = item, Name = item.Id ?? "" };
        foreach (var child in item.Children) node.Nodes.Add(CreateNode(child, settings));
        if (settings?.ExpandedItemIds?.Contains(item.Id ?? "") == true) node.Expand();
        return node;
    }

    private static string Caption(DocumentItem item) =>
        (item.NodeType != NodeType.Item ? "📁 " : "") +
        (string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name);

    private void SelectById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var node = FindNode(tree.Nodes, id);
        if (node != null) tree.SelectedNode = node;
    }

    private static TreeNode FindNode(TreeNodeCollection nodes, string id)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is DocumentItem item && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return node;
            var nested = FindNode(node.Nodes, id);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void CaptureExpanded(TreeNodeCollection nodes, ISet<string> target)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && node.Tag is DocumentItem item && !string.IsNullOrWhiteSpace(item.Id)) target.Add(item.Id);
            CaptureExpanded(node.Nodes, target);
        }
    }
}
