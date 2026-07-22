using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class MilkdownMarkdownCodecTests
    {
        [TestMethod]
        public void Обычное_изображение_проходит_через_Milkdown_без_потери_alt()
        {
            const string source = "Текст\r\n\r\n![Схема классов](asset:images/schema.png)\r\n";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);
            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            StringAssert.Contains(editor, "![1.00](asset:images/schema.png#docsets-alt=");
            Assert.Equal(source, restored);
        }

        [TestMethod]
        public void Размер_блочного_изображения_сохраняется_в_fragment_DocSets()
        {
            const string source = "![Диаграмма](asset:images/diagram.png#docsets-scale=0.50 \"Архитектура\")";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);
            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            StringAssert.StartsWith(editor, "![0.50](asset:images/diagram.png#docsets-alt=");
            StringAssert.EndsWith(editor, " \"Архитектура\")");
            Assert.Equal(source, restored);
        }

        [TestMethod]
        public void Изменённый_в_Milkdown_размер_становится_масштабом_DocSets()
        {
            const string source = "![Изображение](asset:images/photo.png)";
            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source)
                .Replace("![1.00]", "![0.75]");

            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            Assert.Equal("![Изображение](asset:images/photo.png#docsets-scale=0.75)", restored);
        }

        [TestMethod]
        public void Inline_изображение_не_превращается_в_ImageBlock()
        {
            const string source = "До ![иконка](asset:images/icon.png) после";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);
            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            Assert.Equal(source, editor);
            Assert.Equal(source, restored);
        }

        [TestMethod]
        public void Внутренние_ссылки_проходят_через_безопасный_https_адрес()
        {
            const string source = "[Метод](symbol:test|DocSets.DocumentItem.Name)";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);
            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            StringAssert.StartsWith(editor, "[Метод](https://docsets.local/symbol/");
            Assert.Equal(source, restored);
        }

        [TestMethod]
        public void Вставленный_https_asset_возвращается_в_канонический_asset()
        {
            const string editor = "Текст ![image](https://docsets.assets/images/a.png)";

            var restored = MilkdownMarkdownCodec.FromEditorMarkdown(editor);

            Assert.Equal("Текст ![image](asset:images/a.png)", restored);
        }

        [TestMethod]
        public void Старая_взорванная_таблица_OneNote_становится_блоком_кода()
        {
            const string source =
                "До\n\n" +
                "| <br /> | <br /> | <br /> | **wital-zip.py** |\n" +
                "| :--- | :--- | :--- | :--- |\n" +
                "| import os | import glob | def find\\_files(path): | return path |\n\nПосле";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);

            StringAssert.Contains(editor, "**wital-zip.py**\n\n```python");
            StringAssert.Contains(editor,
                "import os\nimport glob\ndef find_files(path):\nreturn path\n```");
            Assert.False(editor.Contains("| <br /> |"));
        }

        [TestMethod]
        public void Обычная_ранее_созданная_таблица_не_изменяется()
        {
            const string source =
                "| Это первая таблица |  |\n" +
                "| ------------------ | --- |\n" +
                "| Значение A | Значение B |";

            var editor = MilkdownMarkdownCodec.ToEditorMarkdown(source);

            Assert.Equal(source, editor);
        }
    }
}
