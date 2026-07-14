using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace DocSets
{
    internal sealed class BookmarkColorDefinition
    {
        public BookmarkColor Value { get; set; }
        public string Name { get; set; }
        public Color DrawingColor { get; set; }
    }

    internal static class BookmarkColorService
    {
        private static readonly IReadOnlyList<BookmarkColorDefinition> Definitions = Enum.GetValues(typeof(BookmarkColor)).Cast<BookmarkColor>().Select(Create).ToList();
        public static IReadOnlyList<BookmarkColorDefinition> All => Definitions;
        public static BookmarkColorDefinition Get(BookmarkColor value) => Definitions.FirstOrDefault(x => x.Value == value) ?? Definitions[0];
        public static string GetName(BookmarkColor value) => Get(value).Name;
        public static Color GetColor(BookmarkColor value) => Get(value).DrawingColor;

        private static BookmarkColorDefinition Create(BookmarkColor value)
        {
            var field = typeof(BookmarkColor).GetField(value.ToString());
            var info = field?.GetCustomAttribute<BookmarkColorInfoAttribute>();
            return new BookmarkColorDefinition { Value = value, Name = info?.Name ?? value.ToString(), DrawingColor = info == null ? Color.White : Color.FromArgb(info.Red, info.Green, info.Blue) };
        }
    }
}