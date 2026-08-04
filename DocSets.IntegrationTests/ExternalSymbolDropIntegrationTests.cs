using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class ExternalSymbolDropIntegrationTests
    {
        [TestMethod]
        public void ExternalDropPipelineInsertsJoditHtmlLink()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    var task = RunAsync();
                    while (!task.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    task.GetAwaiter().GetResult();
                }
                catch (Exception exception) { failure = exception; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Тест общего DragDrop-конвейера не завершился за две минуты.");
            if (failure != null) throw new Exception("Ошибка общего DragDrop-конвейера.", failure);
        }

        private static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.ExternalDropTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await TestJoditAsync(root);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static async Task TestJoditAsync(string root)
        {
            using (var form = new Form { Width = 800, Height = 500, ShowInTaskbar = false })
            using (var editor = new JoditCommentControl(Path.Combine(root, "Jodit")) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(editor);
                form.Show();
                await WaitUntilAsync(() => editor.IsReady, "Jodit не инициализирован.");
                await TestEmbeddedCodeBookmarkAsync(editor);
                editor.LoadComment("<p>BeforeAfter</p>");
                await WaitUntilAsync(async () => (await editor.GetCurrentCommentAsync()).Contains("BeforeAfter"),
                    "Jodit не загрузил исходный HTML.");
                await editor.SetTestSelectionAsync(6);
                var received = false;
                editor.ExternalSymbolDropRequested += text =>
                {
                    received = text == "Display";
                    editor.InsertResolvedLink(CreateLink());
                };
                var result = await editor.SimulateExternalTextDropAsync("Display");
                AssertDropAccepted(result, "Jodit");
                await WaitUntilAsync(() => received, "Jodit не передал Drop в общий C#-конвейер.");
                await Task.Delay(300);
                var html = await editor.GetCurrentCommentAsync();
                Assert.True(html.Contains("Before ") && html.Contains("DocumentItem.Display") &&
                            html.Contains(" After") && html.Contains("symbol:test|DocSets.DocumentItem.Display"),
                    "Jodit не вставил HTML-ссылку с пробелами. Получено: " + html);
            }
        }

        private static async Task TestEmbeddedCodeBookmarkAsync(JoditCommentControl editor)
        {
            const string selectedCode = "if (ready)\r\n    Run();";
            editor.LoadComment("<p>Code:</p>");
            await WaitUntilAsync(async () =>
                (await editor.GetCurrentCommentAsync()).Contains("Code:"),
                "Jodit не загрузил заметку для проверки ссылки на выделенный код.");
            await editor.SetTestSelectionAsync(5);
            var codeLink = DocumentLinkService.CreateEmbeddedBookmarkLink(
                CreateCodeBookmark(selectedCode));
            string activatedLink = null;
            editor.LinkActivated += target => activatedLink = target;
            editor.InsertEmbeddedBookmarkLink(codeLink);
            await WaitUntilAsync(async () =>
            {
                var value = await editor.GetCurrentCommentAsync();
                return value.Contains("bookmark:embedded-v1") &&
                       value.Contains("data-docsets-code-bookmark=") &&
                       value.Contains("if (ready)") &&
                       !value.Contains(codeLink.Target);
            }, "Jodit не сохранил короткую ссылку и payload минимальной кодовой закладки.");
            var savedHtml = await editor.GetCurrentCommentAsync();
            editor.LoadComment(savedHtml);
            await WaitUntilAsync(async () =>
                (await editor.GetCurrentCommentAsync()).Contains(
                    "data-docsets-code-bookmark="),
                "Jodit потерял payload кодовой закладки после повторной загрузки заметки.");
            await editor.SimulateFirstLinkClickAsync();
            await WaitUntilAsync(() => string.Equals(activatedLink,
                    "bookmark:" + codeLink.Target, StringComparison.Ordinal),
                "Jodit не вернул payload кодовой закладки при клике.");
            Assert.True(DocumentLinkService.TryGetEmbeddedBookmark(
                activatedLink.Substring("bookmark:".Length), out var restored));
            Assert.Equal(selectedCode, restored.EditorState.SelectedText);
        }

        private static DocumentItem CreateCodeBookmark(string selectedText) => new DocumentItem
        {
            Name = "Run",
            NodeType = NodeType.Item,
            Type = BookmarkType.Symbol,
            Symbol = "Sample.Worker.Run()",
            Project = "Sample",
            Path = "src/Worker.cs",
            Line = 40,
            Column = 9,
            EditorState = new EditorState
            {
                CaretLineOffset = 2,
                CaretColumn = 15,
                HasSelection = true,
                SelectionStartLineOffset = 1,
                SelectionStartColumn = 9,
                SelectionEndLineOffset = 2,
                SelectionEndColumn = 11,
                FirstVisibleLineOffset = -5,
                SelectedText = selectedText,
                CodePreview = new string('x', 10000)
            }
        };

        private static DocumentLink CreateLink() => new DocumentLink
        {
            Kind = DocumentLinkKind.Symbol,
            Caption = "DocumentItem.Display",
            Target = "DocSets.DocumentItem.Display",
            Project = "test"
        };

        private static void AssertDropAccepted(ExternalDropTestResult result, string editor)
        {
            Assert.NotNull(result, editor + ": нет результата DragDrop.");
            Assert.True(result.Accepted, editor + ": события DragOver/Drop не были отменены.");
            Assert.Equal("copy", result.DropEffect, editor + ": операция не объявлена как Copy.");
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string message)
            => await WaitUntilAsync(() => Task.FromResult(condition()), message);

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
        {
            var limit = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < limit)
            {
                Application.DoEvents();
                if (await condition()) return;
                await Task.Delay(50);
            }
            throw new TimeoutException(message);
        }
    }
}
