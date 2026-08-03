using System;
using System.Collections.Generic;
using System.Linq;

namespace DocSets
{
    public sealed class ImportSessionTreeService
    {
        private readonly Dictionary<string, ImportSessionState> sessions =
            new Dictionary<string, ImportSessionState>(StringComparer.OrdinalIgnoreCase);

        public DocumentItem Root { get; private set; }
        public IReadOnlyCollection<ImportSessionState> Sessions => sessions.Values.ToList();

        public static bool IsManagedNode(DocumentItem node)
            => node?.IsImportsRoot == true || node?.IsImportSession == true;

        public void Attach(DocumentSetsState state, IEnumerable<ImportSessionState> source)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            Root = state.Sets.FirstOrDefault(x => x != null && x.IsImportsRoot);
            if (Root == null)
            {
                Root = new DocumentItem
                {
                    Id = "imports",
                    Name = "Imports",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsImportsRoot = true
                };
                var position = Math.Min(3, state.Sets.Count);
                state.Sets.Insert(position, Root);
            }
            sessions.Clear();
            Root.Children.Clear();
            foreach (var session in source ?? Enumerable.Empty<ImportSessionState>()) Upsert(session);
        }

        public DocumentItem Upsert(ImportSessionState session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id) || Root == null) return null;
            sessions[session.Id] = session;
            var node = Root.Children.FirstOrDefault(x => string.Equals(
                x.ImportSessionId, session.Id, StringComparison.OrdinalIgnoreCase));
            if (node == null)
            {
                node = new DocumentItem
                {
                    Id = "import-" + session.Id,
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Empty,
                    ContentFormat = ContentFormat.Html,
                    IsLocalOnly = true,
                    IsImportSession = true,
                    ImportSessionId = session.Id
                };
                Root.Children.Insert(0, node);
            }
            node.Name = session.Name;
            node.Content = BuildSummary(session);
            return node;
        }

        public ImportSessionState Find(string id)
            => !string.IsNullOrWhiteSpace(id) && sessions.TryGetValue(id, out var value) ? value : null;

        public bool RemoveNode(string id)
        {
            var node = Root?.Children.FirstOrDefault(x => string.Equals(
                x.ImportSessionId, id, StringComparison.OrdinalIgnoreCase));
            return node != null && Root.Children.Remove(node);
        }

        public bool Forget(string id) => sessions.Remove(id ?? "");

        private static string BuildSummary(ImportSessionState session)
        {
            var percent = Math.Max(0, Math.Min(100, session.OverallProgressPercent));
            return "<p><b>" + System.Net.WebUtility.HtmlEncode(session.Status.ToString()) +
                   "</b> — " + percent + "% (" + session.ProgressCurrent + "/" +
                   session.ProgressTotal + ")</p><p>" +
                   System.Net.WebUtility.HtmlEncode(session.Stage ?? "") + "</p>";
        }
    }
}
