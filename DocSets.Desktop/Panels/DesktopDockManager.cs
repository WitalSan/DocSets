using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Desktop.Panels;

internal sealed class DesktopDockManager
{
    private readonly DockPanel _dockPanel;
    private readonly Dictionary<string, DesktopDockContent> _panels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _menuItems =
        new(StringComparer.OrdinalIgnoreCase);

    public DesktopDockManager(DockPanel dockPanel)
    {
        _dockPanel = dockPanel ?? throw new ArgumentNullException(nameof(dockPanel));
    }

    public IReadOnlyDictionary<string, DesktopDockContent> Panels => _panels;

    public bool IsVisible(string persistId)
        => _panels.TryGetValue(persistId, out var panel) &&
           panel.DockState != DockState.Hidden && panel.DockState != DockState.Unknown;

    public void Register(IEnumerable<DocSetsPanelControl> controls)
    {
        foreach (var control in controls)
        {
            if (_panels.ContainsKey(control.PersistId))
                throw new InvalidOperationException(
                    "Повторный persist ID панели: " + control.PersistId);
            var panel = new DesktopDockContent(control);
            panel.DockStateChanged += (_, __) => UpdateMenuItem(panel);
            panel.VisibleChanged += (_, __) => UpdateMenuItem(panel);
            _panels.Add(panel.PersistId, panel);
        }
    }

    public void PopulateMenu(ToolStripItemCollection items)
    {
        items.Clear();
        _menuItems.Clear();
        foreach (var definition in DesktopPanelCatalog.Definitions)
        {
            if (!_panels.TryGetValue(definition.PersistId, out var panel)) continue;
            var item = new ToolStripMenuItem(definition.Title)
            {
                CheckOnClick = false,
                Checked = panel.Visible,
                Tag = definition.PersistId
            };
            item.Click += (_, __) => Toggle(definition.PersistId);
            items.Add(item);
            _menuItems.Add(definition.PersistId, item);
        }
    }

    public void Show(string persistId)
    {
        if (!_panels.TryGetValue(persistId, out var panel)) return;
        var definition = DesktopPanelCatalog.Definitions.First(x =>
            string.Equals(x.PersistId, persistId, StringComparison.OrdinalIgnoreCase));
        if (panel.DockPanel == null)
            panel.Show(_dockPanel, definition.DefaultDockState);
        else
            panel.Show();
        panel.Activate();
        UpdateMenuItem(panel);
    }

    public void Hide(string persistId)
    {
        if (!_panels.TryGetValue(persistId, out var panel)) return;
        panel.Hide();
        UpdateMenuItem(panel);
    }

    public void Toggle(string persistId)
    {
        if (!_panels.TryGetValue(persistId, out var panel)) return;
        if (panel.Visible) Hide(persistId); else Show(persistId);
    }

    public void ShowDefaultLayout()
    {
        foreach (var definition in DesktopPanelCatalog.Definitions)
        {
            if (definition.VisibleByDefault)
            {
                var panel = _panels[definition.PersistId];
                panel.Show(_dockPanel, definition.DefaultDockState);
                panel.Activate();
                UpdateMenuItem(panel);
            }
            else Hide(definition.PersistId);
        }
    }

    public void ResetLayout()
    {
        foreach (var panel in _panels.Values)
            panel.Hide();
        ShowDefaultLayout();
    }

    public IDockContent Deserialize(string persistId)
        => _panels.TryGetValue(persistId, out var panel) ? panel : null;

    private void UpdateMenuItem(DesktopDockContent panel)
    {
        if (_menuItems.TryGetValue(panel.PersistId, out var item))
            item.Checked = panel.Visible;
    }
}
