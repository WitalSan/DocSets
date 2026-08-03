using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace DocSets
{
    public static class ImportSessionLinkInspector
    {
        private static readonly Regex ObjectLink = new Regex(
            "href=[\"']https://docsets\\.local/bookmark/(?<node>[^\"'#]+)#(?<anchor>onenote-object-[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool HasUnresolvedObjectLinks(DocumentItem root)
        {
            if (root == null) return false;
            var nodes = Enumerate(root).Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var source in nodes.Values)
                foreach (Match match in ObjectLink.Matches(source.Content ?? ""))
                {
                    var nodeId = Uri.UnescapeDataString(WebUtility.HtmlDecode(match.Groups["node"].Value));
                    var anchor = Uri.UnescapeDataString(WebUtility.HtmlDecode(match.Groups["anchor"].Value));
                    if (!nodes.TryGetValue(nodeId, out var target) ||
                        (target.Content ?? "").IndexOf("id=\"" + anchor + "\"",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        return true;
                }
            return false;
        }

        internal static IEnumerable<DocumentItem> Enumerate(DocumentItem root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (var child in root.Children.ToList())
                foreach (var nested in Enumerate(child)) yield return nested;
        }
    }
}
