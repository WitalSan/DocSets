using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class MetafilePreviewTests
    {
        [TestMethod]
        public void OriginalMetafileAndPreviewAreStoredSeparatelyAndHtmlKeepsBothReferences()
        {
            var source = CreateEmf();
            var preview = new MetafilePreviewService().Render(source, 120, 80);
            Assert.Equal(240, preview.PixelWidth);
            Assert.Equal(160, preview.PixelHeight);
            Assert.True(preview.PngBytes.Length > 8);
            Assert.Equal((byte)137, preview.PngBytes[0]);

            var directory = Path.Combine(Path.GetTempPath(), "docsets-metafile-" + Guid.NewGuid().ToString("N") + ".docsets");
            try
            {
                Directory.CreateDirectory(directory);
                var storage = new AssetStorageService();
                var original = storage.SaveFileAsync(directory, source, "test.emf").GetAwaiter().GetResult();
                var png = storage.SaveImageAsync(directory, preview.PngBytes, "image/png", "preview.png").GetAwaiter().GetResult();
                Assert.SequenceEqual(source, storage.Read(directory, original));
                var html = MetafilePreviewService.BuildHtml(source, original, png, "emf", preview);
                Assert.True(html.Contains("class=\"docsets-metafile\""));
                Assert.True(html.Contains("data-docsets-original-src=\"" + original + "\""));
                Assert.True(html.Contains("src=\"" + png + "\""));
                Assert.True(html.Contains("data-docsets-renderer-version=\"" + MetafilePreviewService.RendererVersion + "\""));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void SameMetafileSizeAndRendererProduceDeterministicPreview()
        {
            var source = CreateEmf();
            var service = new MetafilePreviewService();
            var first = service.Render(source, 90, 60);
            var second = service.Render(source, 90, 60);
            Assert.SequenceEqual(first.PngBytes, second.PngBytes);
            Assert.Equal(first.PixelWidth, second.PixelWidth);
            Assert.Equal(first.PixelHeight, second.PixelHeight);
        }

        [TestMethod]
        public void LargerLogicalSizeCreatesLargerPreviewWithoutChangingOriginal()
        {
            var source = CreateEmf();
            var original = (byte[])source.Clone();
            var service = new MetafilePreviewService();
            var small = service.Render(source, 60, 40);
            var large = service.Render(source, 180, 120);
            Assert.True(large.PixelWidth > small.PixelWidth);
            Assert.True(large.PixelHeight > small.PixelHeight);
            Assert.SequenceEqual(original, source);
        }

        [TestMethod]
        public void PreviewFailurePlaceholderRetainsOriginalIdentityAndDimensions()
        {
            var source = CreateEmf();
            var html = MetafilePreviewService.BuildPlaceholderHtml(source,
                "asset:files/original.emf", "emf", 321, 123);
            Assert.True(html.Contains("docsets-metafile-placeholder"));
            Assert.True(html.Contains("data-docsets-original-src=\"asset:files/original.emf\""));
            Assert.True(html.Contains("data-docsets-width=\"321\""));
            Assert.True(html.Contains("data-docsets-height=\"123\""));
            Assert.True(html.Contains("data-docsets-renderer-version=\"0\""));
        }

        private static byte[] CreateEmf()
        {
            using (var reference = new Bitmap(1, 1))
            using (var referenceGraphics = Graphics.FromImage(reference))
            using (var stream = new MemoryStream())
            {
                var hdc = referenceGraphics.GetHdc();
                try
                {
                    using (var metafile = new Metafile(stream, hdc, EmfType.EmfPlusDual))
                    using (var graphics = Graphics.FromImage(metafile))
                    {
                        graphics.Clear(Color.White);
                        using (var pen = new Pen(Color.Gray, 2)) graphics.DrawRectangle(pen, 4, 4, 90, 50);
                    }
                }
                finally { referenceGraphics.ReleaseHdc(hdc); }
                return stream.ToArray();
            }
        }
    }
}
