using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal static class TagIconProvider
    {
        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        public static readonly string[] Names = { "", "bug", "check", "warning", "review", "star", "bookmark", "idea" };

        public static Image Get(string name, int size)
        {
            name = name ?? ""; var key = name + ":" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var bitmap = new Bitmap(size, size); bitmap.SetResolution(96, 96);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                var pen = new Pen(Color.FromArgb(70, 70, 70), Math.Max(2f, size / 12f));
                var accent = Color.FromArgb(55, 125, 210);
                if (name == "check") { pen.Color = Color.SeaGreen; g.DrawLines(pen, new[] { P(size,.18,.55), P(size,.42,.78), P(size,.84,.24) }); }
                else if (name == "warning") { using (var b = new SolidBrush(Color.Goldenrod)) g.FillPolygon(b, new[] { P(size,.5,.08), P(size,.94,.88), P(size,.06,.88) }); using (var font = new Font("Segoe UI", size*.55f, FontStyle.Bold, GraphicsUnit.Pixel)) g.DrawString("!", font, Brushes.White, size*.37f, size*.28f); }
                else if (name == "bug") { using (var b = new SolidBrush(Color.IndianRed)) g.FillEllipse(b, size*.22f,size*.20f,size*.56f,size*.68f); for(int i=0;i<3;i++){ var y=size*(.34f+i*.16f); g.DrawLine(pen,size*.08f,y,size*.25f,y); g.DrawLine(pen,size*.75f,y,size*.92f,y); } }
                else if (name == "review") { using (var b = new SolidBrush(accent)) g.FillEllipse(b,size*.08f,size*.25f,size*.84f,size*.5f); g.FillEllipse(Brushes.White,size*.31f,size*.31f,size*.38f,size*.38f); g.FillEllipse(Brushes.DimGray,size*.43f,size*.43f,size*.14f,size*.14f); }
                else if (name == "star") { using (var b = new SolidBrush(Color.Goldenrod)) g.FillPolygon(b, Star(size)); }
                else if (name == "bookmark") { using (var b = new SolidBrush(accent)) g.FillPolygon(b,new[]{P(size,.25,.1),P(size,.75,.1),P(size,.75,.9),P(size,.5,.68),P(size,.25,.9)}); }
                else if (name == "idea") { using (var b = new SolidBrush(Color.Gold)) g.FillEllipse(b,size*.2f,size*.08f,size*.6f,size*.6f); g.DrawLine(pen,size*.38f,size*.72f,size*.62f,size*.72f); g.DrawLine(pen,size*.4f,size*.84f,size*.6f,size*.84f); }
                else { using (var b = new SolidBrush(Color.Silver)) g.FillEllipse(b,size*.28f,size*.28f,size*.44f,size*.44f); }
                pen.Dispose();
            }
            Cache[key] = bitmap; return bitmap;
        }

        public static Image GetStrip(IEnumerable<string> names, int size, int maximum = 4)
        {
            var all = (names ?? Enumerable.Empty<string>()).Select(x => x ?? "").ToList();
            if (all.Count == 0) return null;
            var visible = all.Take(maximum).ToList();
            var overflow = all.Count - visible.Count;
            var overflowWidth = overflow > 0 ? Math.Max(size, size * 3 / 2) : 0;
            var gap = Math.Max(2, size / 8);
            var width = visible.Count * size + Math.Max(0, visible.Count - 1) * gap + (overflow > 0 ? gap + overflowWidth : 0);
            var key = "strip:" + size + ":" + maximum + ":" + string.Join("|", all);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var bitmap = new Bitmap(Math.Max(1, width), size);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                var x = 0;
                foreach (var name in visible)
                {
                    graphics.DrawImage(Get(name, size), x, 0, size, size);
                    x += size + gap;
                }
                if (overflow > 0)
                {
                    using (var font = new Font("Segoe UI", Math.Max(8, size * .48f), FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        TextRenderer.DrawText(graphics, "+" + overflow, font, new Rectangle(x, 0, overflowWidth, size), Color.DimGray,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
            }
            Cache[key] = bitmap;
            return bitmap;
        }
        private static PointF P(int s, double x, double y) => new PointF((float)(s*x),(float)(s*y));
        private static PointF[] Star(int s) { var p=new PointF[10]; for(int i=0;i<10;i++){var a=-Math.PI/2+i*Math.PI/5;var r=(i%2==0?.45:.2)*s;p[i]=new PointF((float)(s/2+Math.Cos(a)*r),(float)(s/2+Math.Sin(a)*r));} return p; }
    }
}