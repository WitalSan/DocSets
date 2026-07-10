using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DocSets
{
    [Guid("7b16d10b-f141-4727-a28e-b020649a6844")]
    public class DocSetsToolWindow : ToolWindowPane
    {
        public DocSetsToolWindow() : base(null)
        {
            Caption = "Wital DockSet BookMarks";
            Content = new DocSetsWinFormsHostControl(DocSetsPackage.Instance);
        }
    }
}
