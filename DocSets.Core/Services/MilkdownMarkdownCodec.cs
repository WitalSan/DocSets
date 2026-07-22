using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocSets
{
    /// <summary>
    /// Преобразует канонический Markdown DocSets во внутреннее представление Milkdown
    /// и обратно. Отдельный кодек не позволяет экспериментальному редактору менять
    /// формат уже сохранённых заметок без явного решения DocSets.
    /// </summary>
    internal static class MilkdownMarkdownCodec
    {
        private const string InternalLinkPrefix = "https://docsets.local/";
        private const string AltMetadataName = "docsets-alt=";
        private const string ScaleMetadataName = "docsets-scale=";

        private static readonly Regex StoredDocSetsLink = new Regex(
            @"\]\((?<kind>symbol|bookmark|file):(?<target>[^\)]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EditorDocSetsLink = new Regex(
            @"\]\(https://docsets\.local/(?<kind>symbol|bookmark|file)/(?<target>[^\)]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Milkdown ImageBlock использует alt для коэффициента размера. Настоящий alt
        // временно переносится в служебный fragment URL, который браузер не отправляет
        // при запросе файла. Обрабатываются только отдельные изображения-абзацы:
        // inline-изображения Milkdown не превращает в ImageBlock.
        private static readonly Regex StandaloneImage = new Regex(
            @"^(?<indent>[ \t]*)!\[(?<alt>(?:\\.|[^\]])*)\]\(" +
            @"(?<url>(?:asset:[^\s\)]+|https://docsets\.assets/[^\s\)]+))" +
            @"(?:[ \t]+(?<quote>[""'])(?<title>.*?)(?:\k<quote>))?\)(?<tail>[ \t]*)(?<cr>\r?)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex EditorAssetLink = new Regex(
            @"\]\(https://docsets\.assets/(?<path>[^\)\s]+)(?<suffix>(?:\s+[""'].*?[""'])?)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string ToEditorMarkdown(string markdown)
        {
            var converted = RepairLegacyExplodedOneNoteTables(markdown ?? string.Empty);
            converted = StoredDocSetsLink.Replace(converted, match =>
                "](" + InternalLinkPrefix + match.Groups["kind"].Value.ToLowerInvariant() + "/" +
                Uri.EscapeDataString(match.Groups["target"].Value) + ")");

            return StandaloneImage.Replace(converted, match =>
            {
                var url = match.Groups["url"].Value;
                var ratio = ExtractScale(ref url);
                url = AddFragmentValue(url, AltMetadataName, Encode(match.Groups["alt"].Value));
                return match.Groups["indent"].Value + "![" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + "](" +
                    url + FormatTitle(match) + ")" + match.Groups["tail"].Value + match.Groups["cr"].Value;
            });
        }

        internal static string FromEditorMarkdown(string markdown)
        {
            var converted = StandaloneImage.Replace(markdown ?? string.Empty, match =>
            {
                var url = match.Groups["url"].Value;
                if (!TryRemoveFragmentValue(ref url, AltMetadataName, out var encodedAlt)) return match.Value;

                var ratio = ParseRatio(match.Groups["alt"].Value);
                if (Math.Abs(ratio - 1d) > 0.0001d)
                    url = AddFragmentValue(url, ScaleMetadataName,
                        ratio.ToString("0.00", CultureInfo.InvariantCulture));

                return match.Groups["indent"].Value + "![" + Decode(encodedAlt) + "](" + url +
                    FormatTitle(match) + ")" + match.Groups["tail"].Value + match.Groups["cr"].Value;
            });

            converted = EditorDocSetsLink.Replace(converted, match =>
                "](" + match.Groups["kind"].Value.ToLowerInvariant() + ":" +
                Uri.UnescapeDataString(match.Groups["target"].Value) + ")");

            return EditorAssetLink.Replace(converted, match =>
                "](asset:" + match.Groups["path"].Value + match.Groups["suffix"].Value + ")");
        }

        internal static string FromEditorLink(string target)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "docsets.local", StringComparison.OrdinalIgnoreCase))
                return target;

            var parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, 2);
            if (parts.Length != 2) return target;
            var kind = parts[0].ToLowerInvariant();
            if (kind != "symbol" && kind != "bookmark" && kind != "file") return target;
            return kind + ":" + Uri.UnescapeDataString(parts[1]);
        }

        private static string FormatTitle(Match match)
        {
            if (!match.Groups["quote"].Success) return string.Empty;
            var quote = match.Groups["quote"].Value;
            return " " + quote + match.Groups["title"].Value + quote;
        }

        private static double ExtractScale(ref string url)
        {
            if (!TryRemoveFragmentValue(ref url, ScaleMetadataName, out var scale)) return 1d;
            return ParseRatio(scale);
        }

        private static double ParseRatio(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) ||
                ratio <= 0d) return 1d;
            return Math.Min(1d, ratio);
        }

        private static string AddFragmentValue(string url, string name, string value)
        {
            return url + (url.IndexOf('#') >= 0 ? "&" : "#") + name + value;
        }

        private static bool TryRemoveFragmentValue(ref string url, string name, out string value)
        {
            value = string.Empty;
            var hash = url.IndexOf('#');
            if (hash < 0) return false;

            var baseUrl = url.Substring(0, hash);
            var parts = url.Substring(hash + 1).Split('&').ToList();
            var index = parts.FindIndex(part => part.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;

            value = parts[index].Substring(name.Length);
            parts.RemoveAt(index);
            url = baseUrl + (parts.Count == 0 ? string.Empty : "#" + string.Join("&", parts));
            return true;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Decode(string value)
        {
            try
            {
                var encoded = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Первые версии Milkdown могли сохранить многострочную ячейку OneNote как
        /// таблицу, где каждая строка кода стала отдельной колонкой. Исправляем только
        /// однозначный шаблон: почти пустая строка заголовка, одно имя файла и не менее
        /// четырёх колонок. Обычные Markdown-таблицы этот метод не затрагивает.
        /// </summary>
        internal static string RepairLegacyExplodedOneNoteTables(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown ?? string.Empty;
            var newline = markdown.Contains("\r\n") ? "\r\n" : "\n";
            var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
            for (var index = 0; index + 2 < lines.Count; index++)
            {
                if (!TryRepairLegacyTable(lines[index], lines[index + 1], lines[index + 2],
                        newline, out var replacement)) continue;
                lines[index] = replacement;
                lines.RemoveAt(index + 2);
                lines.RemoveAt(index + 1);
            }
            return string.Join(newline, lines);
        }

        private static bool TryRepairLegacyTable(string headerLine, string separatorLine,
            string bodyLine, string newline, out string replacement)
        {
            replacement = string.Empty;
            var headers = SplitTableRow(headerLine);
            var separators = SplitTableRow(separatorLine);
            var body = SplitTableRow(bodyLine);
            if (headers.Count < 4 || headers.Count != separators.Count || headers.Count != body.Count ||
                separators.Any(cell => !Regex.IsMatch(cell.Trim(), @"^:?-{3,}:?$"))) return false;

            var namedHeaders = headers
                .Select((value, index) => new { Value = CleanHeaderCell(value), Index = index })
                .Where(cell => !string.IsNullOrWhiteSpace(cell.Value))
                .ToList();
            if (namedHeaders.Count != 1) return false;

            var fileName = namedHeaders[0].Value;
            if (!Regex.IsMatch(fileName, @"^[^\r\n\\/:*?""<>|]+\.[A-Za-z0-9]{1,10}$")) return false;
            if (body.Count(cell => !string.IsNullOrWhiteSpace(cell)) < 2) return false;

            var code = string.Join(newline, body.Select(CleanCodeCell));
            var language = LanguageByFileName(fileName);
            replacement = "**" + fileName + "**" + newline + newline +
                "```" + language + newline + code + newline + "```";
            return true;
        }

        private static System.Collections.Generic.List<string> SplitTableRow(string line)
        {
            var result = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(line)) return result;
            var value = line.Trim();
            if (!value.StartsWith("|", StringComparison.Ordinal) ||
                !value.EndsWith("|", StringComparison.Ordinal)) return result;

            var cell = new StringBuilder();
            for (var index = 1; index < value.Length - 1; index++)
            {
                var character = value[index];
                if (character == '|' && (index == 0 || value[index - 1] != '\\'))
                {
                    result.Add(RemoveTablePadding(cell.ToString()));
                    cell.Clear();
                }
                else cell.Append(character);
            }
            result.Add(RemoveTablePadding(cell.ToString()));
            return result;
        }

        private static string RemoveTablePadding(string value)
        {
            if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.EndsWith(" ", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
            return value;
        }

        private static string CleanHeaderCell(string value)
        {
            var result = Regex.Replace(value ?? string.Empty, @"<br\s*/?>", string.Empty,
                RegexOptions.IgnoreCase).Trim();
            if (result.StartsWith("**", StringComparison.Ordinal) &&
                result.EndsWith("**", StringComparison.Ordinal) && result.Length >= 4)
                result = result.Substring(2, result.Length - 4).Trim();
            return WebUtility.HtmlDecode(result).Replace('\u00a0', ' ');
        }

        private static string CleanCodeCell(string value)
        {
            var result = WebUtility.HtmlDecode(value ?? string.Empty).Replace('\u00a0', ' ');
            result = Regex.Replace(result, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            return Regex.Replace(result, @"\\([\\`*_{}\[\]()#+\-.!>])", "$1");
        }

        private static string LanguageByFileName(string fileName)
        {
            switch ((System.IO.Path.GetExtension(fileName) ?? string.Empty).ToLowerInvariant())
            {
                case ".py": return "python";
                case ".cs": return "csharp";
                case ".js": return "javascript";
                case ".ts": return "typescript";
                case ".json": return "json";
                case ".sql": return "sql";
                case ".xml": return "xml";
                case ".html": return "html";
                case ".css": return "css";
                case ".ps1": return "powershell";
                case ".sh": return "shell";
                case ".cmd":
                case ".bat": return "batch";
                default: return string.Empty;
            }
        }
    }
}
