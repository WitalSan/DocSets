using System.Drawing;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class CodePreviewHighlighterTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void NullControlIsIgnored()
        {
            CodePreviewHighlighter.Apply(null, "class C {}", "a.cs");
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void NullCodeClearsControl()
        {
            using (var box = Box())
            {
                box.Text = "old";
                CodePreviewHighlighter.Apply(box, null, "a.cs");
                Assert.Equal(string.Empty, box.Text);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void PreservesValidSelectionAfterUpdatingText()
        {
            using (var box = Box())
            {
                box.Text = "old text";
                box.Select(2, 3);
                CodePreviewHighlighter.Apply(box, "class Example { }", "a.cs");
                Assert.Equal(2, box.SelectionStart);
                Assert.Equal(3, box.SelectionLength);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ClampsSelectionWhenNewTextIsShorter()
        {
            using (var box = Box())
            {
                box.Text = "0123456789";
                box.Select(8, 2);
                CodePreviewHighlighter.Apply(box, "abc", "a.cs");
                Assert.Equal(3, box.SelectionStart);
                Assert.Equal(0, box.SelectionLength);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void HighlightsCSharpKeywordsButNotPlainTextFiles()
        {
            using (var csharp = Box())
            using (var text = Box())
            {
                csharp.ForeColor = Color.Black;
                text.ForeColor = Color.Black;
                CodePreviewHighlighter.Apply(csharp, "class C {}", "a.cs");
                CodePreviewHighlighter.Apply(text, "class C {}", "a.txt");
                csharp.Select(0, 5);
                text.Select(0, 5);
                Assert.False(csharp.SelectionColor.ToArgb() == Color.Black.ToArgb());
                Assert.Equal(Color.Black.ToArgb(), text.SelectionColor.ToArgb());
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void HandlesCrLfWithoutSpanOffsetErrors()
        {
            using (var box = Box())
            {
                CodePreviewHighlighter.Apply(box, "// comment\r\nclass C\r\n{\r\n    int n = 42;\r\n}", "a.cs");
                var classIndex = box.Text.IndexOf("class");
                var numberIndex = box.Text.IndexOf("42");
                box.Select(classIndex, 5);
                var keyword = box.SelectionColor;
                box.Select(numberIndex, 2);
                var number = box.SelectionColor;
                Assert.False(keyword.ToArgb() == box.ForeColor.ToArgb());
                Assert.False(number.ToArgb() == box.ForeColor.ToArgb());
            }
        }

        private static RichTextBox Box() => new RichTextBox { Font = new Font("Consolas", 9F), BackColor = Color.White, ForeColor = Color.Black };
    }
}
