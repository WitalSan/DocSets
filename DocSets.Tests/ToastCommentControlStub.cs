using System;
using System.Windows.Forms;

namespace DocSets
{
    // The production control hosts Visual Studio's WPF editor. Unit tests run without a VS shell.
    internal sealed class ToastCommentControl : UserControl
    {
        private readonly TextBox editor = new TextBox { Dock = DockStyle.Fill, Multiline = true };
        private bool loading;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event Action<string> LinkActivated;
        public event Action<string> ExternalSymbolDropRequested;

        public ToastCommentControl()
        {
            Controls.Add(editor);
            editor.TextChanged += (_, __) => { if (!loading) CommentChanged?.Invoke(this, EventArgs.Empty); };
            editor.LostFocus += (_, __) => EditingCompleted?.Invoke(this, EventArgs.Empty);
        }

        public string CommentText => editor.Text ?? string.Empty;
        public bool UsesVisualStudioMarkdownEditor => false;

        public void LoadComment(string value)
        {
            loading = true;
            try { editor.Text = value ?? string.Empty; }
            finally { loading = false; }
        }

        public void InsertResolvedLink(DocumentLink link) { }
        public void FocusEditorFromHost() => editor.Focus();
    }
}