using System;
using System.Threading.Tasks;
using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Desktop.Panels;

/// <summary>Desktop hosting only; the complete import UI lives in DocSets.UI.WinForms.</summary>
internal sealed class ImportSessionDockContent : DockContent
{
    public ImportSessionDockContent(ImportSessionState session,
        Func<OneNoteImportReportEntry, Task> openEntry)
    {
        SessionId = session?.Id ?? throw new ArgumentNullException(nameof(session));
        Text = session.Name;
        TabText = session.Name;
        DockAreas = DockAreas.Document | DockAreas.Float;
        HideOnClose = false;
        Report = new OneNoteImportReportDialog
        {
            TopLevel = false,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            Dock = System.Windows.Forms.DockStyle.Fill
        };
        Report.Attach(session, openEntry);
        Controls.Add(Report);
        Report.Show();
    }

    public string SessionId { get; }
    public OneNoteImportReportDialog Report { get; }
}
