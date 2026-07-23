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
        public void SameExternalDropPipelineInsertsMarkdownAndHtmlLinks()
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
                await TestToastAsync(root);
                await TestCkEditorAsync(root);
                await TestJoditAsync(root);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static async Task TestToastAsync(string root)
        {
            using (var form = new Form { Width = 800, Height = 500, ShowInTaskbar = false })
            using (var editor = new ToastCommentControl(Path.Combine(root, "Toast")) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(editor);
                form.Show();
                await WaitUntilAsync(() => editor.IsReady, "Toast не инициализирован.");
                editor.LoadComment("BeforeAfter");
                await WaitUntilAsync(async () => (await editor.GetCurrentCommentAsync()).Contains("BeforeAfter"),
                    "Toast не загрузил исходный текст.");
                await editor.SetTestSelectionAsync(6);
                var received = false;
                editor.ExternalSymbolDropRequested += text =>
                {
                    received = text == "Display";
                    editor.InsertResolvedLink(CreateLink());
                };
                var result = await editor.SimulateExternalTextDropAsync("Display");
                AssertDropAccepted(result, "Toast");
                await WaitUntilAsync(async () => received &&
                    (await editor.GetCurrentCommentAsync()).Contains(
                        "Before [DocumentItem.Display](symbol:test|DocSets.DocumentItem.Display) After"),
                    "Toast вставил ссылку без требуемых пробелов либо не в формате Markdown.");
            }
        }

        private static async Task TestCkEditorAsync(string root)
        {
            using (var form = new Form { Width = 800, Height = 500, ShowInTaskbar = false })
            using (var editor = new CkEditorCommentControl(Path.Combine(root, "CKEditor")) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(editor);
                form.Show();
                await WaitUntilAsync(() => editor.IsReady, "CKEditor не инициализирован.");
                editor.LoadComment("<p>BeforeAfter</p>");
                await WaitUntilAsync(async () => (await editor.GetCurrentCommentAsync()).Contains("BeforeAfter"),
                    "CKEditor не загрузил исходный HTML.");
                await editor.SetTestSelectionAsync(6);
                var received = false;
                editor.ExternalSymbolDropRequested += text =>
                {
                    received = text == "Display";
                    editor.InsertResolvedLink(CreateLink());
                };
                var result = await editor.SimulateExternalTextDropAsync("Display");
                AssertDropAccepted(result, "CKEditor");
                await WaitUntilAsync(() => received, "CKEditor не передал Drop в общий C#-конвейер.");
                await Task.Delay(500);
                var html = await editor.GetCurrentCommentAsync();
                Assert.True(html.Contains("Before ") && html.Contains("DocumentItem.Display") &&
                            html.Contains(" After") && html.Contains("symbol:test|DocSets.DocumentItem.Display"),
                    "CKEditor не вставил HTML-ссылку с пробелами. Получено: " + html);
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
