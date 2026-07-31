using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DocSets
{
    public sealed class AssetStorageService
    {
        public const string AssetPrefix = "asset:";
        private static readonly Regex AssetPattern = new Regex(
            @"asset:(?<path>images/[A-Za-z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataImagePattern = new Regex(
            @"data:(?<mime>image/(?:png|jpeg|gif|webp));base64,(?<data>[A-Za-z0-9+/=\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<string> SaveImageAsync(string docSetDirectory, byte[] content,
            string mimeType, string originalName = "", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(docSetDirectory))
                throw new ArgumentException("Необходимо указать каталог DocSet.", nameof(docSetDirectory));
            if (content == null || content.Length == 0)
                throw new ArgumentException("Изображение пусто.", nameof(content));

            var extension = GetImageExtension(mimeType, originalName);
            var hash = ComputeHash(content);
            var relativePath = Path.Combine("images", hash + extension);
            var fullPath = ResolveAssetPath(docSetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            if (!File.Exists(fullPath))
            {
                using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 81920, true))
                    await stream.WriteAsync(content, 0, content.Length, cancellationToken).ConfigureAwait(false);
            }
            return AssetPrefix + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }

        public string ResolveAssetPath(string docSetDirectory, string assetReference)
        {
            var relativePath = (assetReference ?? "").Trim();
            if (relativePath.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
                relativePath = relativePath.Substring(AssetPrefix.Length);
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Некорректный путь вложения.");

            var assetsRoot = Path.GetFullPath(Path.Combine(docSetDirectory, "assets"));
            var fullPath = Path.GetFullPath(Path.Combine(assetsRoot, relativePath));
            var prefix = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь вложения выходит за каталог assets.");
            return fullPath;
        }

        public IReadOnlyList<string> FindReferences(string markdown)
        {
            return AssetPattern.Matches(markdown ?? "").Cast<Match>()
                .Select(match => AssetPrefix + match.Groups["path"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<string> ImportEmbeddedImagesAsync(string docSetDirectory, string markdown,
            CancellationToken cancellationToken = default)
        {
            var result = markdown ?? string.Empty;
            var matches = DataImagePattern.Matches(result).Cast<Match>().ToList();
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var match = matches[index];
                byte[] content;
                try { content = Convert.FromBase64String(match.Groups["data"].Value); }
                catch (FormatException) { continue; }
                var reference = await SaveImageAsync(docSetDirectory, content,
                    match.Groups["mime"].Value, cancellationToken: cancellationToken).ConfigureAwait(false);
                result = result.Remove(match.Index, match.Length).Insert(match.Index, reference);
            }
            return result;
        }

        public byte[] Read(string docSetDirectory, string assetReference)
            => File.ReadAllBytes(ResolveAssetPath(docSetDirectory, assetReference));

        public string GetMimeType(string assetReference)
        {
            switch (Path.GetExtension(assetReference ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: throw new NotSupportedException("Формат вложения не поддерживается.");
            }
        }

        private static string ComputeHash(byte[] content)
        {
            using (var algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(content).Select(value => value.ToString("x2")));
        }

        private static string GetImageExtension(string mimeType, string originalName)
        {
            switch ((mimeType ?? "").Trim().ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
            }
            var extension = Path.GetExtension(originalName ?? "").ToLowerInvariant();
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                extension == ".gif" || extension == ".webp")
                return extension == ".jpeg" ? ".jpg" : extension;
            throw new NotSupportedException("Поддерживаются изображения PNG, JPEG, GIF и WebP.");
        }
    }
}
