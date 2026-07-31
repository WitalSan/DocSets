namespace DocSets.Desktop.Panels;

internal sealed class PropertiesToolWindow : DesktopDockContent
{
    private readonly PropertyGrid grid = new() { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = true };

    public PropertiesToolWindow() : base("properties", "Свойства")
    {
        Controls.Add(grid);
        grid.PropertyValueChanged += (_, __) => ItemChanged?.Invoke(this, grid.SelectedObject as DocumentItem);
    }

    public event EventHandler<DocumentItem> ItemChanged;
    public void SetItem(DocumentItem item) => grid.SelectedObject = item;
}
