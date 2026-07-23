namespace DocSets
{
    /// <summary>
    /// Экспериментальный HTML-редактор Jodit.
    /// </summary>
    internal sealed class JoditCommentControl : HtmlWebEditorCommentControl
    {
        public JoditCommentControl(string userDataFolder = null)
            : base(
                "https://docsets-jodit.local/",
                "Jodit",
                "WebView2-Jodit",
                userDataFolder)
        {
        }

        protected override bool TryGetEditorResource(
            string name, out string resource, out string mime)
        {
            switch (name)
            {
                case "index.html": resource = "DocSets.Jodit.index.html"; mime = "text/html; charset=utf-8"; return true;
                case "jodit-editor.js": resource = "DocSets.Jodit.jodit-editor.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "jodit.min.js": resource = "DocSets.Jodit.jodit.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "jodit.min.css": resource = "DocSets.Jodit.jodit.min.css"; mime = "text/css; charset=utf-8"; return true;
                default: resource = null; mime = null; return false;
            }
        }
    }
}
