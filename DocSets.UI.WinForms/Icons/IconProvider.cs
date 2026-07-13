using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

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
        Sync,
        Properties,
        CollapseAll,
        ExpandAll,
        NavigatePrevious,
        NavigateNext,
        Undo,
        Redo,
        PinOverlay
    }

    internal static class IconProvider
    {
        private const int SourceSize = 32;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<AppIcon, Image> Sources = new Dictionary<AppIcon, Image>();
        private static readonly Dictionary<Tuple<AppIcon, int>, Image> Scaled = new Dictionary<Tuple<AppIcon, int>, Image>();
        private static readonly Dictionary<Tuple<AppIcon, AppIcon, int>, Image> Overlaid = new Dictionary<Tuple<AppIcon, AppIcon, int>, Image>();
        private static int? _iconSize;
        public static int PinIconSize => IconSize;
        public static int IconSize
        {
            get
            {
                if (_iconSize == null) _iconSize = ToPhysicalIconSize(16);
                return _iconSize.Value;
            }
        }

        static private int ToPhysicalIconSize(int desiredSize)
        {
            //return Math.Max(
            //    1,
            //    (int)Math.Round(desiredSize / 96f * _toolStrip.DeviceDpi));
            return 24;
        }

        public static Image Get(AppIcon icon, int size = -1)
        {
            if (size == -1)
            {
                size = IconSize;
            }

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

        public static Image GetWithOverlay(AppIcon icon, AppIcon overlay, int size, int overlaySize)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            var key = Tuple.Create(icon, overlay, size);
            lock (SyncRoot)
            {
                if (Overlaid.TryGetValue(key, out var cached)) return cached;
                var result = Overlay(Get(icon, size), Get(overlay, overlaySize), size);
                Overlaid.Add(key, result);
                return result;
            }
        }

        public static Image GetPinned(AppIcon icon, int size)
        {
            return GetWithOverlay(icon, AppIcon.PinOverlay, size, (int)(size*0.7));
        }

        private static Image Overlay(Image source, Image overlay, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImageUnscaled(source, 0, 0);
                graphics.DrawImageUnscaled(overlay, 0, 0);
            }
            return bitmap;
        }

        private static Image GetSource(AppIcon icon)
        {
            if (Sources.TryGetValue(icon, out var cached))
                return cached;

            var resourceName = $"DocSets.Icons.{icon}.png";

            using (var stream = Assembly
                       .GetExecutingAssembly()
                       .GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        $"Icon resource was not found: {resourceName}");
                }

                using (var loaded = new Bitmap(stream))
                {
                    var copy = loaded.Clone(
                        new Rectangle(0, 0, loaded.Width, loaded.Height),
                        PixelFormat.Format32bppArgb);

                    Sources.Add(icon, copy);
                    return copy;
                }
            }
        }
        /// <summary>
        /// Этот вариант глючит подгоняет под dpi и картинка получается образанной справа и снизу
        /// </summary>
        /// <param name="icon"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private static Image GetSource1(AppIcon icon)
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

            if (Debugger.IsAttached)
            {
                var str1 = ImageToBase64(source);
                var str2 = BitmapToBase64(bitmap);
            }

            return bitmap;
        }
        static string BitmapToBase64(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
        static string ImageToBase64(Image image)
        {
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
