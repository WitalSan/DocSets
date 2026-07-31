using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DocSets
{
    internal static class BreadcrumbToolTipBuilder
    {
        private const string Separator = "────────────────";

        public static BreadcrumbItem[] BuildItems(DocumentItem item)
        {
            if (item == null)
                return new[] { new BreadcrumbItem { Text = "Заметка не выбрана" } };

            if (item.Type == BookmarkType.Empty)
                return new[]
                {
                    new BreadcrumbItem
                    {
                        Text = item.Name ?? string.Empty,
                        Comment = BookmarkToolTipFormatter.Format(item.Content, item.ContentFormat)
                    }
                };

            if (item.Type == BookmarkType.File)
            {
                var fileName = System.IO.Path.GetFileName(item.Path ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = item.Name ?? item.Path ?? string.Empty;
                return new[]
                {
                    new BreadcrumbItem
                    {
                        Text = string.Format("{0} : {1}", fileName, Math.Max(1, item.Line)),
                        Comment = BookmarkToolTipFormatter.Format(item.Content, item.ContentFormat)
                    }
                };
            }

            var result = new List<BreadcrumbItem>();
            var path = new List<string>();
            foreach (var part in (item.Symbol ?? string.Empty)
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                path.Add(part);
                var symbol = string.Join(".", path);
                result.Add(new BreadcrumbItem
                {
                    Text = part,
                    Comment = Build(item, symbol),
                    Value = symbol,
                    Selectable = true
                });
            }
            if (result.Count == 0)
                result.Add(new BreadcrumbItem
                {
                    Text = item.Name ?? string.Empty,
                    Comment = BookmarkToolTipFormatter.Format(item.Content, item.ContentFormat)
                });
            return result.ToArray();
        }

        public static string Build(DocumentItem item, string symbolPath)
        {
            var bookmarkComment = item == null
                ? ""
                : BookmarkToolTipFormatter.Format(item.Content, item.ContentFormat);
            var snapshot = item?.EditorState?.SymbolSnapshots?
                .FirstOrDefault(x => string.Equals(x?.Symbol, symbolPath, StringComparison.Ordinal));
            var component = snapshot == null ? "" : JoinNonEmpty(snapshot.Signature, snapshot.Comment);
            if (bookmarkComment.Length == 0) return component;
            if (component.Length == 0) return bookmarkComment;
            return component + Environment.NewLine + Environment.NewLine + Separator + Environment.NewLine + Environment.NewLine + bookmarkComment;
        }
        private static Match FindMethodMatch(string snapshot, string methodName)
        {
            if (string.IsNullOrWhiteSpace(snapshot) || string.IsNullOrWhiteSpace(methodName)) return Match.Empty;
            return Regex.Match(snapshot, @"\b" + Regex.Escape(methodName) + @"\s*(\([^\)]*\))", RegexOptions.Singleline);
        }
        private static string NormalizeParameters(string value) => Regex.Replace(value ?? "", @"\s+", " ").Replace("( ", "(").Replace(" )", ")");
        private static string ExtractCodeComment(string snapshot, int declarationIndex)
        {
            if (string.IsNullOrWhiteSpace(snapshot) || declarationIndex <= 0) return "";
            var prefix = snapshot.Substring(0, Math.Min(snapshot.Length, declarationIndex));
            var block = Regex.Matches(prefix, @"/\*\*?(?<text>[\s\S]*?)\*/").Cast<Match>().LastOrDefault();
            string text;
            if (block != null) { text = block.Groups["text"].Value; text = Regex.Replace(text, @"(?m)^\s*\*\s?", ""); }
            else
            {
                var lines = Regex.Matches(prefix, @"(?m)^\s*///?\s?(?<text>.*)$").Cast<Match>().Select(x => x.Groups["text"].Value).ToList();
                if (lines.Count == 0) return "";
                text = string.Join(Environment.NewLine, lines);
            }
            text = Regex.Replace(text, "<see\\s+cref=\\\"[^\\\"]*\\\"\\s*/>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "");
            var cleaned = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).Select(x => x.Trim()).Where(x => x.Length > 0);
            return string.Join(Environment.NewLine, cleaned);
        }
        private static string JoinNonEmpty(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(second)) return first.Trim();
            return first.Trim() + Environment.NewLine + Environment.NewLine + second.Trim();
        }
    }
}
