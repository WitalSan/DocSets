using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DocSets
{
    internal enum BookmarkSearchScope { CurrentGroup, AllGroups }
    internal enum BookmarkSearchField { Name, Symbol, Path, Content }

    internal sealed class BookmarkSearchRequest
    {
        public string Query { get; set; } = string.Empty;
        public BookmarkSearchScope Scope { get; set; } = BookmarkSearchScope.CurrentGroup;
        public bool SearchNames { get; set; } = true;
        public bool SearchSymbolsAndPaths { get; set; } = true;
        public bool SearchContent { get; set; } = true;
        public bool MatchCase { get; set; }
        public bool MatchWholeWord { get; set; }
        public bool UseRegularExpressions { get; set; }
        public int MaximumResults { get; set; } = 5000;
    }

    internal sealed class BookmarkSearchResult
    {
        public DocumentItem Item { get; set; }
        public DocumentItem Group { get; set; }
        public BookmarkSearchField Field { get; set; }
        public string TreePath { get; set; } = string.Empty;
        public int MatchStart { get; set; }
        public int MatchLength { get; set; }
        public int OccurrenceIndex { get; set; }
        public string Snippet { get; set; } = string.Empty;
        public string FieldName => Field == BookmarkSearchField.Name ? "Название" :
            Field == BookmarkSearchField.Symbol ? "Символ" :
            Field == BookmarkSearchField.Path ? "Путь" : "Заметка";
    }

    internal sealed class BookmarkSearchService
    {
        public IReadOnlyList<BookmarkSearchResult> Search(
            BookmarkSearchRequest request,
            IEnumerable<DocumentItem> allGroups,
            DocumentItem currentGroup)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var query = (request.Query ?? string.Empty).Trim();
            if (query.Length == 0) return Array.Empty<BookmarkSearchResult>();

            var groups = request.Scope == BookmarkSearchScope.CurrentGroup
                ? new[] { currentGroup }.Where(x => x != null)
                : (allGroups ?? Enumerable.Empty<DocumentItem>()).Where(IsStoredGroup);
            Regex matcher;
            try
            {
                var pattern = request.UseRegularExpressions ? query : Regex.Escape(query);
                if (request.MatchWholeWord) pattern = @"(?<!\w)(?:" + pattern + @")(?!\w)";
                var options = RegexOptions.Compiled;
                if (!request.MatchCase) options |= RegexOptions.IgnoreCase;
                matcher = new Regex(pattern, options);
            }
            catch (ArgumentException)
            {
                return Array.Empty<BookmarkSearchResult>();
            }

            var results = new List<BookmarkSearchResult>();
            foreach (var group in groups)
            {
                foreach (var item in Enumerate(group.Children))
                {
                    if (item == null || item.IsLocalOnly) continue;
                    var treePath = BuildTreePath(group, item);
                    if (request.SearchNames) AddMatches(results, item, group, BookmarkSearchField.Name, item.Name, matcher, request.MaximumResults, treePath);
                    if (request.SearchSymbolsAndPaths)
                    {
                        AddMatches(results, item, group, BookmarkSearchField.Symbol, item.Symbol, matcher, request.MaximumResults, treePath);
                        AddMatches(results, item, group, BookmarkSearchField.Path, item.Path, matcher, request.MaximumResults, treePath);
                    }
                    if (request.SearchContent) AddMatches(results, item, group, BookmarkSearchField.Content, item.Content, matcher, request.MaximumResults, treePath);
                    if (results.Count >= request.MaximumResults) return results;
                }
            }
            return results;
        }

        private static string BuildTreePath(DocumentItem group, DocumentItem item)
        {
            var parts = new Stack<string>();
            for (var parent = item?.Parent; parent != null && !ReferenceEquals(parent, group); parent = parent.Parent)
            {
                if (!string.IsNullOrWhiteSpace(parent.Name)) parts.Push(parent.Name);
            }
            if (!string.IsNullOrWhiteSpace(group?.Name)) parts.Push(group.Name);
            return string.Join(" \u203a ", parts);
        }
        private static bool IsStoredGroup(DocumentItem item) => item != null && !item.IsLocalOnly &&
            !item.IsHistoryRoot && !item.IsRecentRoot && !item.IsPinRoot;

        private static IEnumerable<DocumentItem> Enumerate(IEnumerable<DocumentItem> nodes)
        {
            foreach (var node in nodes ?? Enumerable.Empty<DocumentItem>())
            {
                if (node == null) continue;
                yield return node;
                foreach (var child in Enumerate(node.Children)) yield return child;
            }
        }

        private static void AddMatches(List<BookmarkSearchResult> results, DocumentItem item, DocumentItem group,
            BookmarkSearchField field, string value, Regex matcher, int maximum, string treePath)
        {
            if (string.IsNullOrEmpty(value) || results.Count >= maximum) return;
            var occurrence = 0;
            foreach (Match match in matcher.Matches(value))
            {
                if (results.Count >= maximum) break;
                if (!match.Success || match.Length == 0) continue;
                results.Add(new BookmarkSearchResult
                {
                    Item = item,
                    Group = group,
                    Field = field,
                    TreePath = treePath ?? string.Empty,
                    MatchStart = match.Index,
                    MatchLength = match.Length,
                    OccurrenceIndex = occurrence++,
                    Snippet = CreateSnippet(value, match.Index, match.Length)
                });
            }
        }
        private static string CreateSnippet(string value, int start, int length)
        {
            var normalized = (value ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            var left = Math.Max(0, start - 36);
            var right = Math.Min(normalized.Length, start + length + 54);
            var snippet = normalized.Substring(left, right - left).Trim();
            return (left > 0 ? "…" : string.Empty) + snippet + (right < normalized.Length ? "…" : string.Empty);
        }
    }
}
