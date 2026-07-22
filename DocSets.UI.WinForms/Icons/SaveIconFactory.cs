using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Создаёт DPI-зависимую иконку сохранения без привязки к теме WebView2.
    /// Она используется всеми редакторами заметок, включая интеграционные тесты.
    /// </summary>
    internal static class SaveIconFactory
    {
        internal static Image Create(Control owner, int logicalSize = 18)
        {
            var dpi = owner?.DeviceDpi ?? 96;
            var size = Math.Max(12, logicalSize * Math.Max(96, dpi) / 96);
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var scale = size / 18f;
                using (var body = new SolidBrush(Color.FromArgb(54, 116, 178)))
                using (var border = new Pen(Color.FromArgb(38, 73, 108), Math.Max(1f, scale)))
                using (var paper = new SolidBrush(Color.White))
                using (var slot = new SolidBrush(Color.FromArgb(220, 231, 241)))
                {
                    graphics.FillRectangle(body, 2 * scale, 1 * scale, 14 * scale, 16 * scale);
                    graphics.DrawRectangle(border, 2 * scale, 1 * scale, 14 * scale, 16 * scale);
                    graphics.FillRectangle(slot, 5 * scale, 2 * scale, 8 * scale, 5 * scale);
                    graphics.FillRectangle(paper, 5 * scale, 10 * scale, 8 * scale, 6 * scale);
                    graphics.DrawRectangle(border, 5 * scale, 10 * scale, 8 * scale, 6 * scale);
                }
            }
            return bitmap;
        }
    }
}
