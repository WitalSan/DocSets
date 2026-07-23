using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class BookmarkToolTipFormatterTests
    {
        [TestMethod]
        public void HtmlIsConvertedToReadableText()
        {
            var text = BookmarkToolTipFormatter.Format(
                "<style>hidden</style><h2>Заголовок</h2><p>Текст&nbsp;&amp;&nbsp;данные<br>Вторая строка</p>",
                ContentFormat.Html);

            Assert.Equal(
                "Заголовок\r\nТекст & данные\r\nВторая строка",
                text);
        }

        [TestMethod]
        public void LongContentIsLimitedToFiveLinesOfTwoHundredCharacters()
        {
            var text = BookmarkToolTipFormatter.Format(
                new string('a', 1200),
                ContentFormat.Markdown);
            var lines = text.Split('\n');

            Assert.Equal(BookmarkToolTipFormatter.MaximumLines, lines.Length);
            foreach (var line in lines)
            {
                Assert.IsTrue(line.TrimEnd('\r').Length <= BookmarkToolTipFormatter.MaximumCharactersPerLine);
            }

            Assert.IsTrue(text.EndsWith("\u2026"));
        }

        [TestMethod]
        public void EmptyContentUsesBookmarkDisplay()
        {
            var item = new DocumentItem
            {
                Name = "Закладка",
                Display = "Program.cs:10",
                Content = string.Empty
            };

            Assert.Equal("Program.cs:10", BookmarkToolTipFormatter.Format(item));
        }
    }
}
