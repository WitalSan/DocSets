namespace DocSets
{
    /// <summary>
    /// Экспериментальный HTML-редактор Jodit.
    /// </summary>
    public sealed class JoditCommentControl : HtmlWebEditorCommentControl
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
                case "prism.js": resource = "DocSets.Jodit.Prism.prism.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-csharp.min.js": resource = "DocSets.Jodit.Prism.prism-csharp.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-typescript.min.js": resource = "DocSets.Jodit.Prism.prism-typescript.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-json.min.js": resource = "DocSets.Jodit.Prism.prism-json.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-sql.min.js": resource = "DocSets.Jodit.Prism.prism-sql.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-python.min.js": resource = "DocSets.Jodit.Prism.prism-python.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-powershell.min.js": resource = "DocSets.Jodit.Prism.prism-powershell.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                case "prism-bash.min.js": resource = "DocSets.Jodit.Prism.prism-bash.min.js"; mime = "text/javascript; charset=utf-8"; return true;
                default: resource = null; mime = null; return false;
            }
        }
    }
}
