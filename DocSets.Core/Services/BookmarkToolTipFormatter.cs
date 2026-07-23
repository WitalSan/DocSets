using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace DocSets
{
    internal static class BookmarkToolTipFormatter
    {
        internal const int MaximumLines = 5;
        internal const int MaximumCharactersPerLine = 200;

        private static readonly Regex NonVisibleHtml = new Regex(
            @"<(script|style)\b[^>]*>.*?</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex HtmlLineBreak = new Regex(
            @"<\s*(br\b[^>]*|/p\s*|/div\s*|/li\s*|/tr\s*|/h[1-6]\s*)>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HtmlTag = new Regex(
            @"<[^>]+>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex HorizontalWhitespace = new Regex(
            @"[^\S\r\n]+",
            RegexOptions.Compiled);

        public static string Format(DocumentItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(item.Content))
            {
                return item.NodeType == NodeType.Folder ? item.Name : item.Display;
            }

            return Format(item.Content, item.ContentFormat);
        }

        internal static string Format(string content, ContentFormat format)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var text = format == ContentFormat.Html
                ? ConvertHtmlToText(content)
                : content;

            text = WebUtility.HtmlDecode(text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');

            var sourceLines = text.Split(new[] { '\n' }, StringSplitOptions.None);
            var result = new List<string>(MaximumLines);
            var truncated = false;

            for (var sourceIndex = 0; sourceIndex < sourceLines.Length; sourceIndex++)
            {
                var line = HorizontalWhitespace.Replace(sourceLines[sourceIndex], " ").Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                while (line.Length > 0)
                {
                    if (result.Count == MaximumLines)
                    {
                        truncated = true;
                        break;
                    }

                    var take = Math.Min(MaximumCharactersPerLine, line.Length);
                    result.Add(line.Substring(0, take).TrimEnd());
                    line = line.Substring(take).TrimStart();
                }

                if (truncated)
                {
                    break;
                }
            }

            if (truncated && result.Count > 0)
            {
                var last = result[result.Count - 1];
                result[result.Count - 1] = last.Length >= MaximumCharactersPerLine
                    ? last.Substring(0, MaximumCharactersPerLine - 1) + "\u2026"
                    : last + "\u2026";
            }

            return string.Join(Environment.NewLine, result);
        }

        private static string ConvertHtmlToText(string html)
        {
            var text = NonVisibleHtml.Replace(html, string.Empty);
            text = HtmlLineBreak.Replace(text, "\n");
            return HtmlTag.Replace(text, string.Empty);
        }
    }
}
