using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class JoditIntegrationTests
    {
        [TestMethod]
        public void HtmlTableFormattingAndAssetLinkRoundTripThroughJodit()
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
                throw new TimeoutException("Интеграционный тест Jodit не завершился за две минуты.");
            if (failure != null) throw new Exception("Ошибка интеграционного теста Jodit.", failure);
        }

        private static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.JoditIntegrationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using (var form = new Form { Width = 900, Height = 600, ShowInTaskbar = false })
                using (var editor = new JoditCommentControl(Path.Combine(root, "WebView2"))
                {
                    Dock = DockStyle.Fill
                })
                {
                    form.Controls.Add(editor);
                    form.Show();
                    await WaitUntilAsync(() => editor.IsReady, "Jodit не инициализирован.");

                    var html =
                        "<h2 style=\"color:#c00000\">Проверка Jodit</h2>" +
                        "<table><tbody><tr><td><strong>Ячейка</strong></td><td>2</td></tr></tbody></table>" +
                        "<p><a href=\"symbol:test|DocSets.DocumentItem.Display\">Display</a></p>" +
                        "<p><img src=\"asset:images/test.png\" alt=\"test\"></p>";
                    editor.LoadComment(html);
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("Проверка Jodit") &&
                               value.Contains("<table") &&
                               value.Contains("symbol:test|DocSets.DocumentItem.Display") &&
                               value.Contains("asset:images/test.png");
                    }, "Jodit не сохранил таблицу, ссылку или asset-ссылку.");

                    var imageRequested = false;
                    editor.ImageInsertionRequested += (data, mime, name, requestId) =>
                    {
                        imageRequested = data == "AQID" && mime == "image/png" &&
                                         name == "clipboard.png";
                        editor.CompleteImage("asset:images/clipboard.png", requestId);
                    };
                    await editor.SimulateImageInsertionAsync("AQID", "image/png", "clipboard.png");
                    await WaitUntilAsync(async () => imageRequested &&
                        (await editor.GetCurrentCommentAsync()).Contains(
                            "asset:images/clipboard.png"),
                        "Jodit не провёл изображение через общее asset-хранилище.");

                    editor.LoadComment("<p>Начало</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Начало"),
                        "Jodit не подготовился к проверке смешанного буфера.");
                    await editor.SimulateMixedPasteAsync(
                        "<table><tbody><tr><td style=\"color:#c00000\">OneNote</td></tr></tbody></table>",
                        "OneNote", "AQID", "image/png", "onenote.png", "formatted");
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("<table") && value.Contains("OneNote") &&
                               !value.Contains("onenote.png");
                    }, "При смешанном буфере Jodit выбрал изображение вместо HTML.");
                    editor.LoadComment("<p>Сессия A</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Сессия A"),
                        "Jodit не загрузил первую тестовую сессию.");
                    await editor.SimulateMixedPasteAsync(
                        string.Empty, " изменение", string.Empty,
                        "image/png", "unused.png", "text");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не создал изменение для истории.");

                    var sessionHtml = await editor.GetCurrentCommentAsync();
                    var session = await editor.CaptureEditingSessionAsync();
                    Assert.True(!string.IsNullOrWhiteSpace(session));

                    editor.LoadComment("<p>Сессия B</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Сессия B"),
                        "Jodit не переключился на вторую сессию.");
                    await editor.LoadEditingSessionAsync(sessionHtml, session);
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не восстановил содержимое первой сессии.");

                    await editor.SimulateHistoryCommandAsync("undo");
                    await editor.SimulateHistoryCommandAsync("undo");
                    await WaitUntilAsync(async () =>
                        !(await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не восстановил Undo первой сессии.");

                    var redoHtml = await editor.GetCurrentCommentAsync();
                    var redoSession = await editor.CaptureEditingSessionAsync();
                    editor.LoadComment("<p>Сессия B после Undo</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Сессия B после Undo"),
                        "Jodit не переключился после Undo.");
                    await editor.LoadEditingSessionAsync(redoHtml, redoSession);
                    await WaitUntilAsync(async () =>
                        !(await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не восстановил сессию с доступным Redo.");

                    await editor.SimulateHistoryCommandAsync("redo");
                    await editor.SimulateHistoryCommandAsync("redo");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не восстановил Redo первой сессии.");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
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
