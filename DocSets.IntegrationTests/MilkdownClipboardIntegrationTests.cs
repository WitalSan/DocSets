using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class MilkdownClipboardIntegrationTests
    {
        private const string InitialMarkdown =
            "# Проверка Milkdown\r\n\r\n" +
            "[Метод](symbol:test|DocSets.DocumentItem.Name)\r\n\r\n" +
            "| Колонка | Значение |\r\n| --- | --- |\r\n| A | B |";

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MarkdownAndImageRoundTripThroughWindowsClipboardAndTwoMilkdownEditors()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var scenario = RunScenarioAsync();
                    while (!scenario.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    scenario.GetAwaiter().GetResult();
                }
                catch (Exception exception) { failure = exception; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Интеграционный тест Milkdown не завершился за две минуты.");
            if (failure != null)
                throw new Exception("Ошибка интеграционного теста Milkdown.", failure);
        }

        private static async Task RunScenarioAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.MilkdownIntegrationTests",
                Guid.NewGuid().ToString("N"));
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(Path.Combine(assets, "images"));

            try
            {
                using (var firstForm = CreateForm())
                using (var first = CreateEditor(root, "WebView2-1", assets))
                {
                    var saveRequests = 0;
                    first.SaveRequested += (_, __) => saveRequests++;
                    firstForm.Controls.Add(first);
                    firstForm.Show();
                    await WaitUntilAsync(() => first.IsReady,
                        "Первый Milkdown не инициализирован.");
                    var commandBar = (ToolStrip)typeof(MilkdownCommentControl)
                        .GetField("commandBar", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic)
                        .GetValue(first);
                    Assert.True(commandBar.Visible && commandBar.Height > 0,
                        "Панель сохранения Milkdown не отображается.");

                    first.LoadComment(InitialMarkdown);
                    await WaitUntilAsync(async () =>
                    {
                        var markdown = Normalize(await first.GetCurrentCommentAsync());
                        return markdown.Contains("Проверка Milkdown") &&
                            markdown.Contains("Колонка") && markdown.Contains("Значение");
                    },
                        "Milkdown не загрузил таблицу.");

                    PutOneNoteTableOnClipboard();
                    first.FocusEditorFromHost();
                    await first.PasteFromClipboardAsync();
                    await WaitUntilAsync(async () =>
                    {
                        var markdown = await first.GetCurrentCommentAsync();
                        return markdown.Contains("wital-zip.py") &&
                            markdown.Contains("import os") &&
                            markdown.Contains("def find_and_zip_files");
                    }, "Milkdown не импортировал таблицу OneNote.");
                    var oneNoteMarkdown = await first.GetCurrentCommentAsync();
                    Assert.False(ContainsExplodedTableRow(oneNoteMarkdown),
                        "Абзацы одной ячейки OneNote превратились в отдельные колонки.");

                    using (var bitmap = new Bitmap(48, 32))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                            graphics.Clear(Color.CornflowerBlue);
                        Clipboard.SetImage(bitmap);
                    }
                    first.FocusEditorFromHost();
                    await first.PasteFromClipboardAsync();
                    var firstMarkdown = await WaitForCompleteMarkdownAsync(first);

                    Assert.True(firstMarkdown.Contains("asset:images/clipboard.png"));
                    Assert.True(File.Exists(Path.Combine(assets, "images", "clipboard.png")));
                    Assert.True(firstMarkdown.Contains("symbol:test|DocSets.DocumentItem.Name"));
                    await WaitUntilAsync(() => first.SaveEnabled,
                        "Кнопка сохранения Milkdown не включилась после изменения.");
                    var browser = (Microsoft.Web.WebView2.WinForms.WebView2)
                        typeof(MilkdownCommentControl)
                            .GetField("webView", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)
                            .GetValue(first);
                    await browser.ExecuteScriptAsync(
                        "document.dispatchEvent(new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true}))");
                    await WaitUntilAsync(() => saveRequests == 1,
                        "Ctrl+S из Milkdown не дошёл до хоста.");
                    first.SetSaveEnabled(false);
                    Assert.False(first.SaveEnabled);

                    await first.SelectAllAndCopyAsync();
                    await WaitUntilAsync(() => Clipboard.ContainsData(DataFormats.Html),
                        "Milkdown не записал смешанное содержимое в буфер обмена.");

                    using (var secondForm = CreateForm())
                    using (var second = CreateEditor(root, "WebView2-2", assets))
                    {
                        secondForm.Controls.Add(second);
                        secondForm.Show();
                        await WaitUntilAsync(() => second.IsReady,
                            "Второй Milkdown не инициализирован.");
                        second.FocusEditorFromHost();
                        await second.PasteFromClipboardAsync();
                        var secondMarkdown = await WaitForCompleteMarkdownAsync(second);

                        Assert.True(secondMarkdown.Contains("Проверка Milkdown"));
                        Assert.True(secondMarkdown.Contains("[Метод](symbol:test|DocSets.DocumentItem.Name)"));
                        Assert.True(secondMarkdown.Contains("Колонка") && secondMarkdown.Contains("Значение"));
                        Assert.True(secondMarkdown.Contains("![clipboard](asset:images/clipboard.png"),
                            "При копировании через Clipboard потеряны изображение или его alt. Получено: " +
                            secondMarkdown);
                    }
                }
            }
            finally
            {
                Clipboard.Clear();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static Form CreateForm()
            => new Form { Width = 900, Height = 600, ShowInTaskbar = false };

        private static void PutOneNoteTableOnClipboard()
        {
            const string fragment =
                "<div style=\"mso-element:para-border-div\">" +
                "<table><tbody>" +
                "<tr><td><p><b>wital-zip.py</b></p></td></tr>" +
                "<tr><td><p>import os</p><p>import glob</p>" +
                "<p>def find_and_zip_files(directory, archive_name):</p></td></tr>" +
                "</tbody></table></div>";
            var data = new DataObject();
            data.SetData(DataFormats.Html, ToastCommentControl.BuildClipboardHtml(fragment));
            data.SetData(DataFormats.UnicodeText,
                "wital-zip.py\r\nimport os\r\nimport glob\r\ndef find_and_zip_files(directory, archive_name):");
            Clipboard.SetDataObject(data, true);
        }

        private static bool ContainsExplodedTableRow(string markdown)
        {
            foreach (var line in (markdown ?? string.Empty).Split('\n'))
            {
                var separators = 0;
                foreach (var character in line)
                    if (character == '|') separators++;
                if (separators > 6) return true;
            }
            return false;
        }

        private static MilkdownCommentControl CreateEditor(
            string root, string profileName, string assets)
        {
            var editor = new MilkdownCommentControl(Path.Combine(root, profileName))
            {
                Dock = DockStyle.Fill
            };
            editor.SetAssetDirectory(assets);
            editor.ImageInsertionRequested += (data, mime, name, requestId) =>
            {
                var path = Path.Combine(assets, "images", "clipboard.png");
                File.WriteAllBytes(path, Convert.FromBase64String(data));
                editor.InsertImage("asset:images/clipboard.png", "clipboard", requestId);
            };
            return editor;
        }

        private static async Task<string> WaitForCompleteMarkdownAsync(MilkdownCommentControl editor)
        {
            string markdown = null;
            await WaitUntilAsync(async () =>
            {
                markdown = await editor.GetCurrentCommentAsync();
                return markdown.Contains("Проверка Milkdown") &&
                    markdown.Contains("Колонка") && markdown.Contains("Значение") &&
                    markdown.Contains("asset:images/clipboard.png");
            }, "Milkdown не сформировал текст, таблицу и изображение.");
            return markdown;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string message)
            => await WaitUntilAsync(() => Task.FromResult(condition()), message);

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
        {
            var limit = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < limit)
            {
                Application.DoEvents();
                if (await condition()) return;
                await Task.Delay(50);
            }
            throw new TimeoutException(message);
        }

        private static string Normalize(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Trim();
    }
}
