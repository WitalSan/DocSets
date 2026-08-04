using System;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Desktop.Panels;

internal sealed class DesktopDockContent : DockContent
{
    public DesktopDockContent(DocSetsPanelControl content)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        Font = System.Drawing.SystemFonts.MessageBoxFont;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        PersistId = content.PersistId;
        Text = content.Title;
        TabText = content.Title;
        HideOnClose = true;
        DockAreas = DockAreas.Float | DockAreas.DockLeft | DockAreas.DockRight |
                    DockAreas.DockTop | DockAreas.DockBottom | DockAreas.Document;
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
    }

    public string PersistId { get; }
    public DocSetsPanelControl Content { get; }

    protected override string GetPersistString() => PersistId;
}
