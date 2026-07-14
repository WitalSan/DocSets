using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DocSets
{
    internal sealed class TagService
    {
        internal static readonly TagDefinition[] StandardTags =
        {
            new TagDefinition { Id = "bug", Name = "Bug", Color = "#D9534F", Icon = "bug" },
            new TagDefinition { Id = "todo", Name = "Todo", Color = "#F0AD4E", Icon = "check" },
            new TagDefinition { Id = "critical", Name = "Critical", Color = "#C9302C", Icon = "warning" },
            new TagDefinition { Id = "review", Name = "Review", Color = "#5BC0DE", Icon = "review" }
        };

        public void EnsureStandardTags(DocumentSetsState state)
        {
            if (state == null) return;
            state.Tags ??= new List<TagDefinition>();
            if (state.Tags.Count != 0) { EnsureIds(state); return; }
            state.Tags.AddRange(StandardTags.Select(x => x.Clone()));
        }

        public TagDefinition Add(DocumentSetsState state, string name, string color = "", string icon = "")
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tag name is required.", nameof(name));
            state.Tags ??= new List<TagDefinition>();
            if (state.Tags.Any(x => string.Equals(x?.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A tag with this name already exists.");
            var used = new HashSet<string>(state.Tags.Where(x => x != null).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var tag = new TagDefinition { Id = CreateId(name, used), Name = name.Trim(), Color = color ?? "", Icon = icon ?? "" };
            state.Tags.Add(tag);
            return tag;
        }

        public void Toggle(IEnumerable<DocumentItem> items, string tagId)
        {
            var selected = (items ?? Enumerable.Empty<DocumentItem>()).Where(x => x != null && !x.IsLocalOnly).Distinct().ToList();
            if (selected.Count == 0 || string.IsNullOrWhiteSpace(tagId)) return;
            var remove = selected.All(x => x.TagIds.Any(id => string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase)));
            foreach (var item in selected)
            {
                var ids = item.TagIds.ToList();
                ids.RemoveAll(id => string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase));
                if (!remove) ids.Add(tagId);
                item.TagIds = ids;
            }
        }

        public void Delete(DocumentSetsState state, string tagId)
        {
            if (state == null || string.IsNullOrWhiteSpace(tagId)) return;
            state.Tags.RemoveAll(x => string.Equals(x?.Id, tagId, StringComparison.OrdinalIgnoreCase));
            foreach (var item in Enumerate(state.Sets))
                item.TagIds = item.TagIds.Where(x => !string.Equals(x, tagId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public void MergeDefinitions(DocumentSetsState state, IEnumerable<TagDefinition> definitions)
        {
            if (state == null) return;
            state.Tags ??= new List<TagDefinition>();
            foreach (var source in definitions ?? Enumerable.Empty<TagDefinition>())
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Id) || state.Tags.Any(x => string.Equals(x?.Id, source.Id, StringComparison.OrdinalIgnoreCase))) continue;
                state.Tags.Add(source.Clone());
            }
            EnsureIds(state);
        }

        public void EnsureIds(DocumentSetsState state)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in (state?.Tags ?? new List<TagDefinition>()).Where(x => x != null))
            {
                if (string.IsNullOrWhiteSpace(tag.Id) || !used.Add(tag.Id)) tag.Id = CreateId(tag.Name, used);
            }
        }

        internal static string CreateId(string name, ISet<string> used)
        {
            var builder = new StringBuilder(); var separator = false;
            foreach (var c in (name ?? "").Trim().ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) { if (separator && builder.Length > 0) builder.Append('-'); builder.Append(c); separator = false; } else separator = true;
            var baseId = builder.ToString().Trim('-'); if (baseId.Length == 0) baseId = "tag";
            var id = baseId; var index = 2; while (!used.Add(id)) id = baseId + "-" + index++;
            return id;
        }

        private static IEnumerable<DocumentItem> Enumerate(IEnumerable<DocumentItem> items)
        {
            foreach (var item in items ?? Enumerable.Empty<DocumentItem>()) { if (item == null) continue; yield return item; foreach (var child in Enumerate(item.Children)) yield return child; }
        }
    }
}