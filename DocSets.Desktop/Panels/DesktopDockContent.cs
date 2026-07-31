using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Desktop.Panels;

internal abstract class DesktopDockContent : DockContent
{
    protected DesktopDockContent(string persistId, string title)
    {
        PersistId = persistId;
        Text = title;
        TabText = title;
        HideOnClose = true;
        DockAreas = DockAreas.Float | DockAreas.DockLeft | DockAreas.DockRight |
                    DockAreas.DockTop | DockAreas.DockBottom | DockAreas.Document;
    }

    public string PersistId { get; }
    protected override string GetPersistString() => PersistId;
}
