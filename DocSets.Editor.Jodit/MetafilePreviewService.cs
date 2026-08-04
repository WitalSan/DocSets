using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace DocSets
{
    public sealed class MetafilePreview
    {
        public byte[] PngBytes { get; set; }
        public int LogicalWidth { get; set; }
        public int LogicalHeight { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
    }

    public sealed class MetafilePreviewService
    {
        public const int RendererVersion = 1;
        public const int DefaultDensity = 2;

        public MetafilePreview Render(byte[] source, int logicalWidth, int logicalHeight,
            int density = DefaultDensity)
        {
            if (source == null || source.Length == 0) throw new ArgumentException("Metafile is empty.", nameof(source));
            using (var stream = new MemoryStream(source, false))
            using (var image = Image.FromStream(stream, true, true))
            {
                var width = logicalWidth > 0 ? logicalWidth : Math.Max(1, image.Width);
                var height = logicalHeight > 0 ? logicalHeight : Math.Max(1, image.Height);
                if (logicalWidth <= 0 && logicalHeight > 0) width = Math.Max(1, (int)Math.Round(height * image.Width / (double)Math.Max(1, image.Height)));
                if (logicalHeight <= 0 && logicalWidth > 0) height = Math.Max(1, (int)Math.Round(width * image.Height / (double)Math.Max(1, image.Width)));
                density = Math.Max(1, density);
                var pixelWidth = checked(width * density);
                var pixelHeight = checked(height * density);
                using (var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppPArgb))
                {
                    bitmap.SetResolution(96f * density, 96f * density);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.White);
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.DrawImage(image, new Rectangle(0, 0, pixelWidth, pixelHeight));
                    }
                    using (var output = new MemoryStream())
                    {
                        bitmap.Save(output, ImageFormat.Png);
                        return new MetafilePreview { PngBytes = output.ToArray(), LogicalWidth = width,
                            LogicalHeight = height, PixelWidth = pixelWidth, PixelHeight = pixelHeight };
                    }
                }
            }
        }

        public static string BuildHtml(byte[] original, string originalReference,
            string previewReference, string format, MetafilePreview preview)
        {
            var assetId = Hash(original).Substring(0, 24);
            string E(string value) => WebUtility.HtmlEncode(value ?? "");
            return "<span class=\"docsets-metafile\" contenteditable=\"false\"" +
                " data-docsets-asset-id=\"" + assetId + "\"" +
                " data-docsets-original-src=\"" + E(originalReference) + "\"" +
                " data-docsets-original-format=\"" + E(format) + "\"" +
                " data-docsets-width=\"" + preview.LogicalWidth + "\"" +
                " data-docsets-height=\"" + preview.LogicalHeight + "\"" +
                " data-docsets-renderer-version=\"" + RendererVersion + "\"" +
                " data-docsets-preview-src=\"" + E(previewReference) + "\">" +
                "<img src=\"" + E(previewReference) + "\" width=\"" + preview.LogicalWidth +
                "\" height=\"" + preview.LogicalHeight + "\" draggable=\"false\"></span>";
        }

        public static string BuildPlaceholderHtml(byte[] original, string originalReference,
            string format, int logicalWidth, int logicalHeight)
        {
            var assetId = Hash(original).Substring(0, 24);
            string E(string value) => WebUtility.HtmlEncode(value ?? "");
            return "<span class=\"docsets-metafile docsets-metafile-placeholder\" contenteditable=\"false\"" +
                " data-docsets-asset-id=\"" + assetId + "\"" +
                " data-docsets-original-src=\"" + E(originalReference) + "\"" +
                " data-docsets-original-format=\"" + E(format) + "\"" +
                " data-docsets-width=\"" + Math.Max(1, logicalWidth) + "\"" +
                " data-docsets-height=\"" + Math.Max(1, logicalHeight) + "\"" +
                " data-docsets-renderer-version=\"0\" title=\"Preview is unavailable\">" +
                "<span class=\"docsets-metafile-placeholder-text\">" +
                E((format ?? "metafile").ToUpperInvariant() + " preview unavailable") + "</span></span>";
        }

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }
    }
}
