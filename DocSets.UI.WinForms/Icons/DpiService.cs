using System;
using System.Drawing;
using System.Windows.Forms;

namespace DocSets
{
    internal static class DpiService
    {
        public const int DefaultDpi = 96;

        public static int GetDpi(Control control)
        {
            if (control == null || control.IsDisposed) return DefaultDpi;
            try { return Math.Max(DefaultDpi, control.DeviceDpi); }
            catch { return DefaultDpi; }
        }

        public static int Scale(int logicalPixels, int dpi)
        {
            if (logicalPixels == 0) return 0;
            return Math.Max(1, (int)Math.Round(logicalPixels * Math.Max(DefaultDpi, dpi) / (double)DefaultDpi));
        }

        public static int ScaleBetween(int pixels, int sourceDpi, int targetDpi)
        {
            if (pixels == 0) return 0;
            sourceDpi = Math.Max(DefaultDpi, sourceDpi);
            targetDpi = Math.Max(DefaultDpi, targetDpi);
            return Math.Max(1, (int)Math.Round(pixels * targetDpi / (double)sourceDpi));
        }

        public static int Scale(Control control, int logicalPixels) => Scale(logicalPixels, GetDpi(control));
        public static float Scale(Control control, float logicalPixels) => logicalPixels * GetDpi(control) / DefaultDpi;
        public static Size Scale(Control control, Size logicalSize) => new Size(Scale(control, logicalSize.Width), Scale(control, logicalSize.Height));
        public static Padding Scale(Control control, Padding logicalPadding) => new Padding(
            Scale(control, logicalPadding.Left), Scale(control, logicalPadding.Top),
            Scale(control, logicalPadding.Right), Scale(control, logicalPadding.Bottom));
        public static int IconSize(Control control, int logicalSize = 16) => Scale(control, logicalSize);
    }
}