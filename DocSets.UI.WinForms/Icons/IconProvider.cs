using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace DocSets
{
    internal enum AppIcon
    {
        Item,
        Folder,
        LinkSymbol,
        LinkFile,
        FolderLinkSymbol,
        FolderLinkFile,
        Copy,
        Paste,
        Find,
        Properties,
        CollapseAll,
        NavigatePrevious,
        NavigateNext
    }

    internal static class IconProvider
    {
        private const int SourceSize = 32;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<AppIcon, Image> Sources = new Dictionary<AppIcon, Image>();
        private static readonly Dictionary<Tuple<AppIcon, int>, Image> Scaled = new Dictionary<Tuple<AppIcon, int>, Image>();

        public static Image Get(AppIcon icon, int size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            var key = Tuple.Create(icon, size);
            lock (SyncRoot)
            {
                if (Scaled.TryGetValue(key, out var cached)) return cached;
                var result = Scale(GetSource(icon), size);
                Scaled.Add(key, result);
                return result;
            }
        }

        private static Image GetSource(AppIcon icon)
        {
            if (Sources.TryGetValue(icon, out var cached)) return cached;
            var resourceName = "DocSets.Icons." + icon + ".png";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidOperationException("Icon resource was not found: " + resourceName);
                using (var loaded = Image.FromStream(stream))
                {
                    var copy = new Bitmap(loaded.Width, loaded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(copy)) graphics.DrawImageUnscaled(loaded, 0, 0);
                    Sources.Add(icon, copy);
                    return copy;
                }
            }
        }

        private static Image Scale(Image source, int size)
        {
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = size < SourceSize ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            return bitmap;
        }
    }
}
