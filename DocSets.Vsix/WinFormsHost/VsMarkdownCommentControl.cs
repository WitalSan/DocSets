using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace DocSets
{
    /// <summary>Experimental host for Visual Studio's native editor using its Markdown content type.</summary>
    internal sealed class VsMarkdownCommentControl : UserControl
    {
        private readonly ElementHost elementHost = new ElementHost { Dock = DockStyle.Fill };
        private readonly TextBox fallback = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10F)
        };
        private ITextBuffer buffer;
        private IWpfTextView textView;
        private IWpfTextViewHost textViewHost;
        private bool loading;
        private bool editing;

        public event EventHandler CommentChanged;
        public event EventHandler EditingCompleted;
        public event EventHandler FocusRequested;

        public VsMarkdownCommentControl()
        {
            Dock = DockStyle.Fill;
            if (!TryCreateVisualStudioEditor())
            {
                Controls.Add(fallback);
                fallback.TextChanged += (_, __) => OnTextChanged();
                fallback.LostFocus += (_, __) => CompleteEditing();
            }
        }

        public string CommentText => buffer?.CurrentSnapshot.GetText() ?? fallback.Text ?? string.Empty;
        public bool UsesVisualStudioMarkdownEditor => textView != null;

        public void LoadComment(string value)
        {
            value = value ?? string.Empty;
            loading = true;
            try
            {
                if (buffer != null)
                {
                    var snapshot = buffer.CurrentSnapshot;
                    if (!string.Equals(snapshot.GetText(), value, StringComparison.Ordinal))
                        buffer.Replace(new Microsoft.VisualStudio.Text.Span(0, snapshot.Length), value);
                }
                else if (!string.Equals(fallback.Text, value, StringComparison.Ordinal))
                {
                    fallback.Text = value;
                }
                editing = false;
            }
            finally { loading = false; }
        }

        public void FocusEditorFromHost()
        {
            if (textView != null)
            {
                elementHost.Focus();
                textView.VisualElement.Focus();
                textView.Caret.EnsureVisible();
            }
            else fallback.Focus();
        }

        private bool TryCreateVisualStudioEditor()
        {
            try
            {
                var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var editorFactory = componentModel?.GetService<ITextEditorFactoryService>();
                var bufferFactory = componentModel?.GetService<ITextBufferFactoryService>();
                var contentTypes = componentModel?.GetService<IContentTypeRegistryService>();
                if (editorFactory == null || bufferFactory == null || contentTypes == null) return false;

                var contentType = contentTypes.GetContentType("markdown")
                    ?? contentTypes.GetContentType("Markdown")
                    ?? contentTypes.GetContentType("text");
                if (contentType == null) return false;

                buffer = bufferFactory.CreateTextBuffer(string.Empty, contentType);
                var roles = editorFactory.CreateTextViewRoleSet(
                    PredefinedTextViewRoles.Document,
                    PredefinedTextViewRoles.Editable,
                    PredefinedTextViewRoles.Interactive,
                    PredefinedTextViewRoles.Zoomable);
                textView = editorFactory.CreateTextView(buffer, roles);
                textViewHost = editorFactory.CreateTextViewHost(textView, setFocus: false);
                buffer.Changed += (_, __) => OnTextChanged();
                textView.VisualElement.LostKeyboardFocus += (_, __) => CompleteEditing();
                textViewHost.HostControl.PreviewMouseDown += (_, __) => FocusRequested?.Invoke(this, EventArgs.Empty);
                elementHost.Child = textViewHost.HostControl;
                Controls.Add(elementHost);
                return true;
            }
            catch
            {
                buffer = null;
                textView = null;
                textViewHost = null;
                elementHost.Child = null;
                return false;
            }
        }

        private void OnTextChanged()
        {
            if (loading) return;
            editing = true;
            CommentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CompleteEditing()
        {
            if (!editing || loading) return;
            editing = false;
            EditingCompleted?.Invoke(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CompleteEditing();
                if (textViewHost != null) textViewHost.Close();
                else textView?.Close();
                fallback.Dispose();
                elementHost.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}