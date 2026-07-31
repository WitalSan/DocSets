using System.Collections.Generic;
using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Desktop.Panels;

internal sealed class DesktopPanelDefinition
{
    public DesktopPanelDefinition(string persistId, string title,
        DockState defaultDockState, bool visibleByDefault)
    {
        PersistId = persistId;
        Title = title;
        DefaultDockState = defaultDockState;
        VisibleByDefault = visibleByDefault;
    }

    public string PersistId { get; }
    public string Title { get; }
    public DockState DefaultDockState { get; }
    public bool VisibleByDefault { get; }
}

internal static class DesktopPanelCatalog
{
    private static readonly IReadOnlyList<DesktopPanelDefinition> _definitions =
        new[]
        {
            new DesktopPanelDefinition(DocSetsPanelIds.Tree, "DocSets", DockState.DockLeft, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Properties, "Свойства", DockState.DockRight, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Code, "Код", DockState.Document, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Preview, "Preview", DockState.Document, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Note, "Заметка", DockState.Document, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Search, "Поиск", DockState.DockBottom, true),
            new DesktopPanelDefinition(DocSetsPanelIds.Log, "Лог", DockState.DockBottom, false)
        };

    public static IReadOnlyList<DesktopPanelDefinition> Definitions => _definitions;
}
