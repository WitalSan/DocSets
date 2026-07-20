using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace DocSets
{
    public sealed class SourceReferenceRoot
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("root")]
        public string Root { get; set; } = "";

        [JsonProperty("isDefault", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsDefault { get; set; }
    }

    public sealed class SourceReferenceContext
    {
        [JsonProperty("sources")]
        public List<SourceReferenceRoot> Sources { get; set; } = new List<SourceReferenceRoot>();

        public static SourceReferenceContext Create(IEnumerable<CodeSourceStatus> statuses,
            CodeSourceLocator locator = null)
        {
            locator = locator ?? new CodeSourceLocator();
            var available = (statuses ?? Array.Empty<CodeSourceStatus>())
                .Where(status => status?.Source != null).ToList();
            var defaultSource = locator.GetDefault(available);
            return new SourceReferenceContext
            {
                Sources = available.Select(status => new SourceReferenceRoot
                {
                    Id = status.Source.Id ?? "",
                    Root = status.ResolvedRoot ?? "",
                    IsDefault = ReferenceEquals(status, defaultSource)
                }).ToList()
            };
        }
    }

    public sealed class SourceReferenceService
    {
        private static readonly Regex FileLinkPattern = new Regex(
            @"(?<prefix>\]\(file:)(?<target>[^\)]+)(?<suffix>\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string Resolve(SourceReferenceContext context, string sourceId, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path ?? "";
            var source = FindSource(context, sourceId);
            return source == null || string.IsNullOrWhiteSpace(source.Root)
                ? path
                : Path.GetFullPath(Path.Combine(source.Root, path));
        }

        public void Rebase(SourceReferenceContext sourceContext, SourceReferenceContext targetContext,
            ref string sourceId, ref string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var absolutePath = Resolve(sourceContext, sourceId, path);
            var target = FindContainingSource(targetContext, absolutePath);
            if (target == null)
            {
                sourceId = "";
                path = absolutePath;
                return;
            }

            sourceId = target.IsDefault ? "" : target.Id ?? "";
            path = MakeRelative(target.Root, absolutePath);
        }

        public string RebaseMarkdownFileLinks(string markdown, SourceReferenceContext sourceContext,
            SourceReferenceContext targetContext)
        {
            return FileLinkPattern.Replace(markdown ?? "", match =>
            {
                ParseTarget(match.Groups["target"].Value, out var sourceId, out var path);
                Rebase(sourceContext, targetContext, ref sourceId, ref path);
                var target = string.IsNullOrWhiteSpace(sourceId) ? path : sourceId + "|" + path;
                return match.Groups["prefix"].Value + target + match.Groups["suffix"].Value;
            });
        }

        private static SourceReferenceRoot FindSource(SourceReferenceContext context, string sourceId)
        {
            var sources = context?.Sources ?? new List<SourceReferenceRoot>();
            if (!string.IsNullOrWhiteSpace(sourceId))
                return sources.FirstOrDefault(source => string.Equals(source.Id, sourceId,
                    StringComparison.OrdinalIgnoreCase));
            return sources.FirstOrDefault(source => source.IsDefault)
                   ?? (sources.Count == 1 ? sources[0] : null);
        }

        private static SourceReferenceRoot FindContainingSource(SourceReferenceContext context, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var fullPath = Path.GetFullPath(path);
            return (context?.Sources ?? new List<SourceReferenceRoot>())
                .Where(source => IsInside(source.Root, fullPath))
                .OrderByDescending(source => source.Root?.Length ?? 0)
                .FirstOrDefault();
        }

        private static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static string MakeRelative(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static void ParseTarget(string target, out string sourceId, out string path)
        {
            var separator = (target ?? "").IndexOf('|');
            sourceId = separator > 0 ? target.Substring(0, separator) : "";
            path = separator > 0 ? target.Substring(separator + 1) : target ?? "";
        }
    }
}
