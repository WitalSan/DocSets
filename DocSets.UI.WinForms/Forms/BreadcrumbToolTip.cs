using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BreadcrumbToolTipController
    {
        private readonly LinkLabel label;
        private readonly ToolTip toolTip;
        private readonly Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.Ordinal);
        private string currentKey;

        public BreadcrumbToolTipController(LinkLabel label, ToolTip toolTip)
        {
            this.label = label; this.toolTip = toolTip;
            label.MouseMove += OnMouseMove;
            label.MouseLeave += (_, __) => { currentKey = null; toolTip.Hide(label); };
        }

        public void Clear() { texts.Clear(); currentKey = null; toolTip.SetToolTip(label, null); }
        public void Set(string symbol, string text) { if (!string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(text)) texts[symbol] = text.Trim(); }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var link = FindLink(e.Location); var key = link?.LinkData as string;
            if (string.Equals(key, currentKey, StringComparison.Ordinal)) return;
            currentKey = key;
            toolTip.Hide(label);
            if (key != null && texts.TryGetValue(key, out var text)) toolTip.Show(text, label, e.X + DpiService.Scale(label, 12), e.Y + DpiService.Scale(label, 18), 30000);
        }

        private LinkLabel.Link FindLink(Point point)
        {
            foreach (LinkLabel.Link link in label.Links)
            {
                var before = link.Start <= 0 ? "" : label.Text.Substring(0, link.Start);
                var value = label.Text.Substring(link.Start, link.Length);
                var x = label.Padding.Left + Measure(before);
                var width = Math.Max(1, Measure(value));
                if (point.X >= x && point.X <= x + width && point.Y >= label.Padding.Top && point.Y <= label.Height - label.Padding.Bottom) return link;
            }
            return null;
        }

        private int Measure(string text) => TextRenderer.MeasureText(text ?? "", label.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    internal static class BreadcrumbToolTipBuilder
    {
        private const string Separator = "────────────────";
        public static string Build(DocumentItem item, string symbolPath)
        {
            var bookmarkComment = item?.Content?.Trim() ?? "";
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
