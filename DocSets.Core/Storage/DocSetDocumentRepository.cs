using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DocSets
{
    public interface IDocSetContentService
    {
        Task<string> LoadAsync(string docSetDirectory, DocSetItemStorageDto item,
            CancellationToken cancellationToken = default);
        Task SaveAsync(string docSetDirectory, DocumentItem source, DocSetItemStorageDto target,
            CancellationToken cancellationToken = default);
    }

    public sealed class DocSetContentService : IDocSetContentService
    {
        public Task<string> LoadAsync(string docSetDirectory, DocSetItemStorageDto item,
            CancellationToken cancellationToken = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSupported(item.ContentFormat, item.ContentPath);
            return Task.FromResult(item.Content ?? "");
        }

        public Task SaveAsync(string docSetDirectory, DocumentItem source, DocSetItemStorageDto target,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSupported(source.ContentFormat, source.ContentPath);
            target.ContentFormat = source.ContentFormat;
            target.Content = source.Content ?? "";
            target.ContentPath = "";
            return Task.CompletedTask;
        }

        private static void EnsureSupported(ContentFormat format, string contentPath)
        {
            if (format != ContentFormat.Markdown)
                throw new NotSupportedException("Формат содержимого пока не поддерживается: " + format + ".");
            if (!string.IsNullOrWhiteSpace(contentPath))
                throw new NotSupportedException("Версия формата 1 поддерживает только встроенный Markdown.");
        }
    }

    public sealed class DocSetDocument
    {
        internal DocSetDocument(string directoryPath, DocSetManifest manifest, DocumentSetsState state)
        {
            DirectoryPath = Path.GetFullPath(directoryPath);
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        public string DirectoryPath { get; }
        public DocSetManifest Manifest { get; }
        public DocumentSetsState State { get; private set; }
        public IReadOnlyList<CodeSource> Sources => Manifest.Sources;

        public void ReplaceState(DocumentSetsState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
        }
    }

    public interface IDocSetDocumentRepository
    {
        Task<DocSetDocument> OpenAsync(string directoryPath, CancellationToken cancellationToken = default);
        Task SaveAsync(DocSetDocument document, CancellationToken cancellationToken = default);
    }

    public sealed class DocSetDocumentRepository : IDocSetDocumentRepository
    {
        private readonly IDocSetStore store;
        private readonly IDocSetContentService contentService;
        private readonly IDocSetsLogger logger;

        public DocSetDocumentRepository(
            IDocSetStore store = null,
            IDocSetContentService contentService = null,
            IDocSetsLogger logger = null)
        {
            this.store = store ?? new DirectoryDocSetStore();
            this.contentService = contentService ?? new DocSetContentService();
            this.logger = logger ?? DocSetsLog.Current;
        }

        public async Task<DocSetDocument> OpenAsync(string directoryPath,
            CancellationToken cancellationToken = default)
        {
            var manifest = await store.OpenAsync(directoryPath, cancellationToken).ConfigureAwait(false);
            var state = new DocumentSetsState
            {
                Tags = manifest.Tags.Where(x => x != null).Select(x => x.Clone()).ToList()
            };
            state.Sets = await BuildTreeAsync(directoryPath, manifest.Items, cancellationToken).ConfigureAwait(false);
            logger.Info("Хранилище", "DocSet открыт: " + Path.GetFullPath(directoryPath));
            return new DocSetDocument(directoryPath, manifest, state);
        }

        public async Task SaveAsync(DocSetDocument document,
            CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.State.EnsureReadableIds();
            document.Manifest.Tags = document.State.Tags.Where(x => x != null).Select(x => x.Clone()).ToList();
            document.Manifest.Items = new List<DocSetItemStorageDto>();
            foreach (var root in document.State.Sets.Where(x => x != null && !x.IsLocalOnly))
                await AppendAsync(document.DirectoryPath, root, "", document.Manifest.Items, cancellationToken)
                    .ConfigureAwait(false);
            await store.SaveAsync(document.DirectoryPath, document.Manifest, cancellationToken).ConfigureAwait(false);
            logger.Info("Хранилище", "DocSet сохранён: " + document.DirectoryPath);
        }

        private async Task<ObservableCollection<DocumentItem>> BuildTreeAsync(
            string directoryPath,
            IEnumerable<DocSetItemStorageDto> source,
            CancellationToken cancellationToken)
        {
            var roots = new ObservableCollection<DocumentItem>();
            var entries = (source ?? Enumerable.Empty<DocSetItemStorageDto>()).Where(x => x != null).ToList();
            var map = new Dictionary<string, DocumentItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Id))
                    throw new InvalidDataException("Элемент DocSet не содержит ID.");
                if (map.ContainsKey(entry.Id))
                    throw new InvalidDataException("В DocSet повторяется ID элемента: " + entry.Id + ".");
                map.Add(entry.Id, await CreateRuntimeItemAsync(directoryPath, entry, cancellationToken).ConfigureAwait(false));
            }

            foreach (var entry in entries)
            {
                var item = map[entry.Id];
                if (!string.IsNullOrWhiteSpace(entry.ParentId) && map.TryGetValue(entry.ParentId, out var parent))
                    parent.Children.Add(item);
                else
                    roots.Add(item);
            }
            EnsureNoCycles(roots, map.Count);
            return roots;
        }

        private async Task<DocumentItem> CreateRuntimeItemAsync(
            string directoryPath,
            DocSetItemStorageDto source,
            CancellationToken cancellationToken)
        {
            return new DocumentItem
            {
                Id = source.Id ?? "",
                Name = source.Name ?? "",
                NodeType = source.NodeType,
                Type = source.Type,
                SourceId = source.SourceId ?? "",
                Symbol = source.Symbol ?? "",
                Project = source.Project ?? "",
                Path = source.Path ?? "",
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                ContentFormat = source.ContentFormat,
                Content = await contentService.LoadAsync(directoryPath, source, cancellationToken).ConfigureAwait(false),
                ContentPath = source.ContentPath ?? "",
                Color = source.Color,
                CreatedAtUtc = source.CreatedAtUtc,
                ModifiedAtUtc = source.ModifiedAtUtc,
                TagIds = source.TagIds?.ToList() ?? new List<string>(),
                EditorState = source.EditorState?.Clone(),
                Children = new ObservableCollection<DocumentItem>()
            };
        }

        private async Task AppendAsync(
            string directoryPath,
            DocumentItem item,
            string parentId,
            ICollection<DocSetItemStorageDto> target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = new DocSetItemStorageDto
            {
                Id = item.Id ?? "",
                ParentId = parentId ?? "",
                Name = item.Name ?? "",
                NodeType = item.NodeType,
                Type = item.Type,
                SourceId = item.SourceId ?? "",
                Symbol = item.Symbol ?? "",
                Project = item.Project ?? "",
                Path = item.Path ?? "",
                Line = item.Line,
                Column = item.Column,
                Color = item.Color,
                CreatedAtUtc = item.CreatedAtUtc,
                ModifiedAtUtc = item.ModifiedAtUtc,
                TagIds = item.TagIds?.Count > 0 ? item.TagIds.ToList() : null,
                EditorState = item.EditorState?.Clone()
            };
            await contentService.SaveAsync(directoryPath, item, dto, cancellationToken).ConfigureAwait(false);
            target.Add(dto);
            foreach (var child in item.Children.Where(x => x != null && !x.IsLocalOnly))
                await AppendAsync(directoryPath, child, dto.Id, target, cancellationToken).ConfigureAwait(false);
        }

        private static void EnsureNoCycles(IEnumerable<DocumentItem> roots, int expectedCount)
        {
            var visited = new HashSet<DocumentItem>();
            var active = new HashSet<DocumentItem>();
            foreach (var root in roots) Visit(root, visited, active);
            if (visited.Count != expectedCount)
                throw new InvalidDataException("Дерево DocSet содержит цикл или недоступные элементы.");
        }

        private static void Visit(DocumentItem item, ISet<DocumentItem> visited, ISet<DocumentItem> active)
        {
            if (!active.Add(item)) throw new InvalidDataException("Дерево DocSet содержит цикл.");
            if (!visited.Add(item)) throw new InvalidDataException("Элемент DocSet подключён к нескольким родителям.");
            foreach (var child in item.Children) Visit(child, visited, active);
            active.Remove(item);
        }
    }
}
