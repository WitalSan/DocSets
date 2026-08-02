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
                var assetDirectory = Path.Combine(root, "assets");
                var imageDirectory = Path.Combine(assetDirectory, "images");
                Directory.CreateDirectory(imageDirectory);
                File.WriteAllBytes(Path.Combine(imageDirectory, "test.png"), Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z9WQAAAAASUVORK5CYII="));
                using (var form = new Form { Width = 900, Height = 600, ShowInTaskbar = false })
                using (var editor = new JoditCommentControl(Path.Combine(root, "WebView2"))
                {
                    Dock = DockStyle.Fill
                })
                {
                    form.Controls.Add(editor);
                    form.Show();
                    editor.SetAssetDirectory(assetDirectory);
                    await WaitUntilAsync(() => editor.IsReady,
                        "Jodit не инициализирован. Этап: " + editor.InitializationStage);

                    editor.SetToolbarVisible(false);
                    await WaitUntilAsync(async () => !await editor.IsToolbarVisibleAsync(),
                        "Jodit не скрыл панель инструментов.");
                    editor.SetToolbarVisible(true);
                    await WaitUntilAsync(async () => await editor.IsToolbarVisibleAsync(),
                        "Jodit не восстановил панель инструментов.");

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
                    await WaitUntilAsync(async () => await editor.AreImagesLoadedAsync(),
                        "Jodit не загрузил существующее изображение из assets DocSets.");

                    string activatedLink = null;
                    editor.LinkActivated += target => activatedLink = target;
                    editor.LoadComment("<p><a href=\"https://docsets.local/bookmark/" +
                        "onenote-test-3#onenote-object-test\">OneNote link</a></p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains(
                            "bookmark:onenote-test-3#onenote-object-test"),
                        "Jodit потерял fragment внутренней ссылки при загрузке.");
                    await editor.SimulateFirstLinkClickAsync();
                    await WaitUntilAsync(() => activatedLink ==
                        "bookmark:onenote-test-3#onenote-object-test",
                        "Клик Jodit не передал bookmark и fragment в Desktop.");

                    editor.LoadComment("<p><span class=\"docsets-attachment\" " +
                        "data-docsets-attachment=\"asset:files/sample.docx\" " +
                        "data-docsets-attachment-name=\"sample.docx\" contenteditable=\"false\">" +
                        "<span class=\"docsets-attachment-icon\">file</span>" +
                        "<span class=\"docsets-attachment-name\">sample.docx</span></span></p>");
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("data-docsets-attachment=\"asset:files/sample.docx\"") &&
                               value.Contains("sample.docx");
                    }, "Jodit потерял относительную asset-ссылку вложенного файла.");

                    editor.LoadComment("<p>До объекта</p><div id=\"onenote-object-test\">" +
                        "Начало целевого объекта</div><p>После объекта</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Начало целевого объекта"),
                        "Jodit не загрузил объект для проверки перехода.");
                    await editor.ScrollToAnchorAsync("onenote-object-test");
                    Assert.Equal("Начало целевого объекта", await editor.GetSelectedTextAsync());

                    editor.LoadComment("<p><span class=\"docsets-note-tag\" " +
                        "data-docsets-note-tag-id=\"tag-1\" data-docsets-tag-style-id=\"todo\" " +
                        "data-docsets-tag-state=\"active\" data-docsets-tag-behavior=\"checkbox\" " +
                        "data-docsets-tag-icon=\"checkbox\" data-docsets-tag-name=\"Todo\">" +
                        "<span class=\"docsets-note-tag-content\">Task</span>" +
                        "</span></p>");
                    await WaitUntilAsync(async () => (await editor.GetCurrentCommentAsync()).Contains(
                        "data-docsets-tag-state=\"active\""),
                        "Jodit did not load the semantic NoteTag span.");
                    await Task.Delay(750);
                    Assert.True(await editor.HasFirstNoteTagIconAsync(),
                        "Jodit removed the NoteTag icon host during delayed DOM normalization.");
                    await editor.SimulateFirstNoteTagClickAsync();
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("data-docsets-tag-state=\"completed\"") &&
                               value.Contains("data-docsets-note-tag-completed-at=") &&
                               value.Contains("☑");
                    }, "Jodit did not complete the NoteTag checkbox or persist its timestamp.");

                    editor.LoadComment("<p>Alpha selected range Omega</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("selected range"),
                        "Jodit did not load text for the range-anchor test.");
                    await editor.SetTestSelectionAsync(6, 14);
                    var rangeAnchor = await editor.SimulateCreateAnchorAsync();
                    Assert.False(string.IsNullOrWhiteSpace(rangeAnchor),
                        "Jodit did not create an anchor for selected text.");
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("id=\"" + rangeAnchor + "\"") &&
                               value.Contains("data-docsets-anchor-end=\"" + rangeAnchor + "\"");
                    }, "Jodit did not persist both range-anchor markers.");
                    await editor.SetTestSelectionAsync(0);
                    await editor.ScrollToAnchorAsync(rangeAnchor);
                    Assert.Equal("selected range", await editor.GetSelectedTextAsync());

                    editor.LoadComment("<p>Empty anchor</p>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Empty anchor"),
                        "Jodit did not load text for the empty-anchor test.");
                    await editor.SetTestSelectionAsync(5);
                    var emptyAnchor = await editor.SimulateCreateAnchorAsync();
                    await editor.SetTestSelectionAsync(0);
                    await editor.ScrollToAnchorAsync(emptyAnchor);
                    Assert.True(await editor.IsCaretAtAnchorStartAsync(emptyAnchor),
                        "Navigation to an empty anchor did not position the caret.");

                    var imageRequested = false;
                    var formattedImageRequested = false;
                    editor.ImageInsertionRequested += (data, mime, name, requestId) =>
                    {
                        imageRequested = data == "AQID" && mime == "image/png" &&
                                         name == "clipboard.png";
                        formattedImageRequested = data == "AQID" && mime == "image/png" &&
                                                  name == "formatted.png";
                        editor.CompleteImage("asset:images/clipboard.png", requestId);
                    };
                    await editor.SimulateImageInsertionAsync("AQID", "image/png", "clipboard.png");
                    await WaitUntilAsync(async () => imageRequested &&
                        (await editor.GetCurrentCommentAsync()).Contains(
                            "asset:images/clipboard.png"),
                        "Jodit не провёл изображение через общее asset-хранилище.");

                    editor.LoadComment("<p>Форматированное изображение</p>");
                    await editor.SimulateMixedPasteAsync(
                        "<p><img src=\"https://example.test/external.png\" " +
                        "alt=\"Picture background\" width=\"184\" height=\"184\"></p>",
                        "Picture background", "AQID", "image/png", "formatted.png", "formatted");
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return formattedImageRequested &&
                               value.Contains("asset:images/clipboard.png") &&
                               !value.Contains("example.test/external.png") &&
                               value.Contains("alt=\"Picture background\"") &&
                               value.Contains("width=\"184\"");
                    }, "Форматированная вставка не перенесла изображение в assets или потеряла атрибуты.");

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

                    const string codeHtml =
                        "<pre class=\"docsets-code-block\" data-language=\"python\">" +
                        "<code class=\"language-python\">" +
                        "def calculate(value):\n" +
                        "    if value &gt; 0:\n" +
                        "        return \"ok\"\n" +
                        "    return \"none\"" +
                        "</code></pre><p><br></p>";
                    editor.LoadComment(codeHtml);
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("def calculate"),
                        "Jodit не загрузил блок Python для проверки подсветки.");
                    var htmlBeforeHighlight = await editor.GetCurrentCommentAsync();
                    Assert.True(await editor.ApplySyntaxHighlightAsync(),
                        "WebView2 не применил визуальную подсветку синтаксиса.");
                    var htmlAfterHighlight = await editor.GetCurrentCommentAsync();
                    Assert.Equal(htmlBeforeHighlight, htmlAfterHighlight,
                        "Подсветка синтаксиса изменила исходный HTML заметки.");
                    StringAssert.Contains(htmlAfterHighlight,
                        "    if value &gt; 0:\n        return \"ok\"",
                        "Подсветка синтаксиса повредила отступы или переводы строк.");
                    Assert.False(htmlAfterHighlight.Contains("token keyword"),
                        "Токены подсветки попали в сохраняемый HTML заметки.");

                    editor.LoadComment(
                        "static void test(CancellationToken token){" +
                        "<div style=\"text-align:left;margin-left:24px\">while(true){" +
                        "<div style=\"font-family:Consolas;margin-left:24px\">Task.Delay(100);</div>" +
                        "<div style=\"margin-left:24px\">if(token.IsCancellationRequested){" +
                        "<div style=\"font-family:Consolas;margin-left:24px\">token.ThrowIfCancellationRequested();</div>" +
                        "</div><div style=\"margin-left:24px\">}</div></div>" +
                        "<div style=\"margin-left:24px\">}</div><div>}</div>");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("Task.Delay(100)"),
                        "Jodit не загрузил OneNote-HTML для проверки отступов.");
                    await editor.SimulateSelectedCodeBlockAsync("csharp");
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains(
                            "static void test(CancellationToken token){\n" +
                            "    while(true){\n" +
                            "        Task.Delay(100);\n" +
                            "        if(token.IsCancellationRequested){\n" +
                            "            token.ThrowIfCancellationRequested();\n" +
                            "        }\n" +
                            "    }\n" +
                            "}");
                    }, "Код/C# потерял визуальные отступы OneNote-HTML.");

                    var clipboardHtml = await editor.BuildCodeClipboardHtmlAsync(
                        "def calculate(value):\n" +
                        "    if value > 0:\n" +
                        "        return \"ok\"",
                        "python");
                    StringAssert.Contains(clipboardHtml, "<br>");
                    StringAssert.Contains(clipboardHtml,
                        "&nbsp;&nbsp;&nbsp;&nbsp;");
                    StringAssert.Contains(clipboardHtml, "color:#0000ff");
                    StringAssert.Contains(clipboardHtml, "font-family:Consolas");
                    Assert.False(clipboardHtml.Contains("class=\"token"),
                        "В HTML-буфер попали внешние классы Prism вместо inline-стилей.");

                    editor.LoadComment("<p>Сессия A</p>");
                    await editor.SimulateMixedPasteAsync(
                        string.Empty, " изменение", string.Empty,
                        "image/png", "unused.png", "text");
                    await WaitUntilAsync(async () =>
                        (await editor.GetCurrentCommentAsync()).Contains("изменение"),
                        "Jodit не восстановил тестовое изменение после проверки подсветки.");

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
