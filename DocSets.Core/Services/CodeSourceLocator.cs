using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DocSets
{
    public sealed class CodeSourceStatus
    {
        public CodeSource Source { get; internal set; }
        public string ResolvedRoot { get; internal set; } = "";
        public string ResolvedPath { get; internal set; } = "";
        public bool RootExists { get; internal set; }
        public bool Exists { get; internal set; }
    }

    public sealed class CodeSourceLocator
    {
        private readonly IDocSetsLogger logger;

        public CodeSourceLocator(IDocSetsLogger logger = null)
        {
            this.logger = logger ?? DocSetsLog.Current;
        }

        public IReadOnlyList<CodeSourceStatus> LocateAll(string docSetDirectory, IEnumerable<CodeSource> sources)
        {
            if (string.IsNullOrWhiteSpace(docSetDirectory))
                throw new ArgumentException("Необходимо указать каталог DocSet.", nameof(docSetDirectory));

            var result = new List<CodeSourceStatus>();
            foreach (var source in sources ?? Array.Empty<CodeSource>())
            {
                if (source == null) continue;
                result.Add(Locate(docSetDirectory, source));
            }
            return result;
        }

        public CodeSourceStatus Locate(string docSetDirectory, CodeSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var baseDirectory = Path.GetFullPath(docSetDirectory);
            var root = Path.GetFullPath(Path.Combine(baseDirectory,
                string.IsNullOrWhiteSpace(source.Root) ? "." : source.Root));
            var path = string.IsNullOrWhiteSpace(source.Path)
                ? root
                : Path.GetFullPath(Path.Combine(root, source.Path));
            var rootExists = Directory.Exists(root);
            var exists = source.Type == CodeSourceType.Directory && string.IsNullOrWhiteSpace(source.Path)
                ? rootExists
                : File.Exists(path) || (source.Type == CodeSourceType.Directory && Directory.Exists(path));
            var displayName = string.IsNullOrWhiteSpace(source.Name) ? source.Id : source.Name;

            var status = new CodeSourceStatus
            {
                Source = source,
                ResolvedRoot = root,
                ResolvedPath = path,
                RootExists = rootExists,
                Exists = exists
            };

            if (exists)
                logger.Info("Sources", "Источник «" + displayName + "» найден: " + path);
            else
                logger.Warning("Sources", "Источник «" + displayName + "» не найден. Ожидался путь: " + path);
            return status;
        }

        public CodeSourceStatus FindForFile(IEnumerable<CodeSourceStatus> statuses, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var fullPath = Path.GetFullPath(filePath);
            return (statuses ?? Array.Empty<CodeSourceStatus>())
                .Where(status => status?.Source != null && IsInside(status.ResolvedRoot, fullPath))
                .OrderByDescending(status => status.ResolvedRoot?.Length ?? 0)
                .FirstOrDefault();
        }

        public string ResolveItemPath(IEnumerable<CodeSourceStatus> statuses, string sourceId,
            string itemPath, string fallbackRoot)
        {
            if (string.IsNullOrWhiteSpace(itemPath) || Path.IsPathRooted(itemPath)) return itemPath ?? "";
            var available = (statuses ?? Array.Empty<CodeSourceStatus>())
                .Where(status => status?.Source != null).ToList();
            var source = !string.IsNullOrWhiteSpace(sourceId)
                ? available.FirstOrDefault(status => string.Equals(status.Source.Id, sourceId,
                    StringComparison.OrdinalIgnoreCase))
                : GetDefault(available);
            var root = source?.ResolvedRoot;
            if (string.IsNullOrWhiteSpace(root)) root = fallbackRoot;
            return string.IsNullOrWhiteSpace(root) ? itemPath : Path.GetFullPath(Path.Combine(root, itemPath));
        }

        public CodeSourceStatus GetDefault(IEnumerable<CodeSourceStatus> statuses)
        {
            var available = (statuses ?? Array.Empty<CodeSourceStatus>())
                .Where(status => status?.Source != null).ToList();
            return available.FirstOrDefault(status => string.Equals(status.Source.Id, "default",
                       StringComparison.OrdinalIgnoreCase))
                   ?? (available.Count == 1 ? available[0] : null);
        }

        public string MakeRelativePath(CodeSourceStatus source, string filePath)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.ResolvedRoot) ||
                string.IsNullOrWhiteSpace(filePath)) return filePath ?? "";
            var baseUri = new Uri(AppendSeparator(Path.GetFullPath(source.ResolvedRoot)));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(Path.GetFullPath(filePath))).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
            return Path.GetFullPath(path).StartsWith(AppendSeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string AppendSeparator(string path)
            => path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path : path + Path.DirectorySeparatorChar;
    }
}
