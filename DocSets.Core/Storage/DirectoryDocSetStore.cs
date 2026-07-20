using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DocSets
{
    public interface IDocSetStore
    {
        Task<DocSetManifest> CreateAsync(string directoryPath, string id, string name, CancellationToken cancellationToken = default);
        Task<DocSetManifest> OpenAsync(string directoryPath, CancellationToken cancellationToken = default);
        Task SaveAsync(string directoryPath, DocSetManifest manifest, CancellationToken cancellationToken = default);
    }

    public sealed class DirectoryDocSetStore : IDocSetStore
    {
        public const string ManifestFileName = "docsets.json";
        public const string DirectorySuffix = ".DocSets";

        public async Task<DocSetManifest> CreateAsync(
            string directoryPath,
            string id,
            string name,
            CancellationToken cancellationToken = default)
        {
            var directory = ValidateDirectoryPath(directoryPath);
            if (Directory.Exists(directory) && File.Exists(GetManifestPath(directory)))
                throw new IOException("DocSet уже существует: " + directory);

            Directory.CreateDirectory(directory);
            var manifest = new DocSetManifest
            {
                Id = RequireValue(id, nameof(id)),
                Name = RequireValue(name, nameof(name))
            };
            await SaveAsync(directory, manifest, cancellationToken).ConfigureAwait(false);
            return manifest;
        }

        public async Task<DocSetManifest> OpenAsync(
            string directoryPath,
            CancellationToken cancellationToken = default)
        {
            var manifestPath = GetManifestPath(ValidateDirectoryPath(directoryPath));
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Манифест DocSet не найден.", manifestPath);

            string json;
            using (var reader = File.OpenText(manifestPath))
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = JsonConvert.DeserializeObject<DocSetManifest>(json)
                ?? throw new InvalidDataException("Манифест DocSet пуст.");
            ValidateManifest(manifest);
            return manifest;
        }

        public async Task SaveAsync(
            string directoryPath,
            DocSetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            ValidateManifest(manifest);

            var directory = ValidateDirectoryPath(directoryPath);
            Directory.CreateDirectory(directory);
            var manifestPath = GetManifestPath(directory);
            var temporaryPath = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var writer = new StreamWriter(temporaryPath, false))
                    await writer.WriteAsync(json).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var verification = JsonConvert.DeserializeObject<DocSetManifest>(File.ReadAllText(temporaryPath));
                ValidateManifest(verification);

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        File.Replace(temporaryPath, manifestPath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceByCopy(temporaryPath, manifestPath);
                    }
                    catch (IOException)
                    {
                        ReplaceByCopy(temporaryPath, manifestPath);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        ReplaceByCopy(temporaryPath, manifestPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, manifestPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        public static string GetManifestPath(string directoryPath)
            => Path.Combine(directoryPath, ManifestFileName);

        private static string ValidateDirectoryPath(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("Необходимо указать каталог DocSet.", nameof(directoryPath));
            var fullPath = Path.GetFullPath(directoryPath);
            if (!fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .EndsWith(DirectorySuffix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Имя каталога DocSet должно оканчиваться на " + DirectorySuffix + ".", nameof(directoryPath));
            return fullPath;
        }

        private static void ValidateManifest(DocSetManifest manifest)
        {
            if (manifest == null) throw new InvalidDataException("Манифест DocSet пуст.");
            if (!string.Equals(manifest.Format, DocSetManifest.CurrentFormat, StringComparison.Ordinal))
                throw new InvalidDataException("Каталог не содержит поддерживаемый манифест DocSet.");
            if (manifest.FormatVersion != DocSetManifest.CurrentFormatVersion)
                throw new NotSupportedException("Версия формата DocSet не поддерживается: " + manifest.FormatVersion + ".");
            RequireValue(manifest.Id, "manifest.id");
            RequireValue(manifest.Name, "manifest.name");
            manifest.Sources = manifest.Sources ?? new System.Collections.Generic.List<CodeSource>();
            manifest.Tags = manifest.Tags ?? new System.Collections.Generic.List<TagDefinition>();
            manifest.Items = manifest.Items ?? new System.Collections.Generic.List<DocSetItemStorageDto>();

            foreach (var item in manifest.Items)
            {
                if (item == null) throw new InvalidDataException("Манифест содержит пустой элемент.");
                RequireValue(item.Id, "item.id");
                if (!string.IsNullOrWhiteSpace(item.Content) && !string.IsNullOrWhiteSpace(item.ContentPath))
                    throw new InvalidDataException("Элемент '" + item.Id + "' одновременно содержит content и contentPath.");
            }
        }

        private static string RequireValue(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException(name + " не должен быть пустым.");
            return value.Trim();
        }

        private static void ReplaceByCopy(string temporaryPath, string manifestPath)
        {
            File.Copy(temporaryPath, manifestPath, true);
            File.Delete(temporaryPath);
        }
    }
}
