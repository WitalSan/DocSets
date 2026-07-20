using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DocSets.Import
{
    internal sealed class ImportOptions
    {
        public string InputPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string Name { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string SourceRoot { get; set; } = "";
        public string SolutionPath { get; set; } = "";
    }

    internal static class LegacyDocSetConverter
    {
        public static DocSetManifest ReadAndConvert(ImportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.InputPath))
                throw new ArgumentException("Необходимо указать InputPath.", nameof(options));

            var json = File.ReadAllText(Path.GetFullPath(options.InputPath));
            var legacy = JsonConvert.DeserializeObject<DocumentSetsState>(json)
                ?? throw new InvalidDataException("Исходный файл DocSets пуст.");
            return Convert(legacy, options);
        }

        public static DocSetManifest Convert(DocumentSetsState legacy, ImportOptions options)
        {
            if (legacy == null) throw new ArgumentNullException(nameof(legacy));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var name = string.IsNullOrWhiteSpace(options.Name)
                ? GetDefaultName(options.InputPath)
                : options.Name.Trim();
            var manifest = new DocSetManifest
            {
                Id = CreateId(name),
                Name = name,
                Tags = (legacy.Tags ?? new List<TagDefinition>())
                    .Where(x => x != null)
                    .Select(x => x.Clone())
                    .ToList()
            };

            var sourceId = AddSourceIfSpecified(manifest, options);
            foreach (var item in legacy.Items ?? new List<DocumentItemStorageDto>())
            {
                if (item == null) continue;
                manifest.Items.Add(new DocSetItemStorageDto
                {
                    Id = item.Id ?? "",
                    ParentId = item.ParentId ?? "",
                    Name = item.Name ?? "",
                    NodeType = item.NodeType == NodeType.Set ? NodeType.Folder : item.NodeType,
                    Type = item.Type,
                    SourceId = sourceId,
                    Symbol = item.Symbol ?? "",
                    Project = item.Project ?? "",
                    Path = item.Path ?? "",
                    Line = item.Line < 1 ? 1 : item.Line,
                    Column = item.Column < 1 ? 1 : item.Column,
                    ContentFormat = ContentFormat.Markdown,
                    Content = item.Comment ?? "",
                    Color = item.Color,
                    CreatedAtUtc = item.CreatedAtUtc,
                    ModifiedAtUtc = item.ModifiedAtUtc,
                    TagIds = item.TagIds?.ToList(),
                    EditorState = item.EditorState?.Clone()
                });
            }

            return manifest;
        }

        private static string AddSourceIfSpecified(DocSetManifest manifest, ImportOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.SolutionPath) &&
                string.IsNullOrWhiteSpace(options.SourceRoot))
                return "";

            var id = string.IsNullOrWhiteSpace(options.SourceId) ? "source" : CreateId(options.SourceId);
            manifest.Sources.Add(new CodeSource
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(options.SourceName) ? id : options.SourceName.Trim(),
                Type = string.IsNullOrWhiteSpace(options.SolutionPath)
                    ? CodeSourceType.Directory
                    : CodeSourceType.Solution,
                Root = options.SourceRoot?.Trim() ?? "",
                Path = options.SolutionPath?.Trim() ?? ""
            });
            return id;
        }

        private static string GetDefaultName(string inputPath)
        {
            var fileName = Path.GetFileName(inputPath ?? "") ?? "Imported";
            const string suffix = ".docsets.json";
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - suffix.Length)
                : Path.GetFileNameWithoutExtension(fileName);
        }

        private static string CreateId(string value)
        {
            var chars = (value ?? "").Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var id = new string(chars).Trim('-');
            while (id.Contains("--")) id = id.Replace("--", "-");
            return string.IsNullOrWhiteSpace(id) ? "imported" : id;
        }
    }
}
