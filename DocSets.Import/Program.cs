using System;
using System.Collections.Generic;
using System.IO;

namespace DocSets.Import
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || HasOption(args, "--help") || HasOption(args, "-h"))
                {
                    PrintUsage();
                    return args.Length == 0 ? 1 : 0;
                }

                var options = Parse(args);
                DocSetsLog.Current.Info("Импорт", "Начат импорт: " + Path.GetFullPath(options.InputPath));
                var manifest = LegacyDocSetConverter.ReadAndConvert(options);
                var store = new DirectoryDocSetStore();
                store.CreateAsync(options.OutputDirectory, manifest.Id, manifest.Name).GetAwaiter().GetResult();
                store.SaveAsync(options.OutputDirectory, manifest).GetAwaiter().GetResult();
                var sourceStatuses = new CodeSourceLocator().LocateAll(options.OutputDirectory, manifest.Sources);

                Console.WriteLine("Импортирован: " + Path.GetFullPath(options.InputPath));
                Console.WriteLine("Создан:        " + Path.GetFullPath(options.OutputDirectory));
                Console.WriteLine("Элементов:     " + manifest.Items.Count);
                foreach (var status in sourceStatuses)
                    Console.WriteLine("Источник:      " + (status.Source.Name ?? status.Source.Id) +
                        " — " + (status.Exists ? "найден" : "не найден") + ": " + status.ResolvedPath);
                Console.WriteLine("Файл журнала:  " + DocSetsLog.Current.CurrentFilePath);
                DocSetsLog.Current.Info("Импорт", "Импорт завершён. Элементов: " + manifest.Items.Count);
                return 0;
            }
            catch (Exception ex)
            {
                DocSetsLog.Current.Error("Импорт", "Импорт завершился ошибкой.", ex);
                Console.Error.WriteLine("Ошибка импорта: " + ex.Message);
                return 2;
            }
        }

        private static ImportOptions Parse(IReadOnlyList<string> args)
        {
            var options = new ImportOptions { InputPath = args[0] };
            for (var i = 1; i < args.Count; i++)
            {
                var option = args[i];
                if (i + 1 >= args.Count) throw new ArgumentException("Не указано значение параметра " + option + ".");
                var value = args[++i];
                switch (option.ToLowerInvariant())
                {
                    case "--output": options.OutputDirectory = value; break;
                    case "--name": options.Name = value; break;
                    case "--source-id": options.SourceId = value; break;
                    case "--source-name": options.SourceName = value; break;
                    case "--source-root": options.SourceRoot = value; break;
                    case "--solution": options.SolutionPath = value; break;
                    default: throw new ArgumentException("Неизвестный параметр: " + option + ".");
                }
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                var input = Path.GetFullPath(options.InputPath);
                var fileName = Path.GetFileName(input);
                const string suffix = ".docsets.json";
                var name = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - suffix.Length)
                    : Path.GetFileNameWithoutExtension(fileName);
                options.OutputDirectory = Path.Combine(Path.GetDirectoryName(input), name + ".DocSets");
            }
            return options;
        }

        private static bool HasOption(IEnumerable<string> args, string option)
        {
            foreach (var value in args)
                if (string.Equals(value, option, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DocSets.Import <legacy.docsets.json> [options]");
            Console.WriteLine();
            Console.WriteLine("  --output <Name.DocSets>  Целевой каталог (по умолчанию рядом с исходным файлом)");
            Console.WriteLine("  --name <name>            Отображаемое имя нового DocSet");
            Console.WriteLine("  --source-id <id>         Стабильный ID необязательного источника кода");
            Console.WriteLine("  --source-name <name>     Отображаемое имя источника кода");
            Console.WriteLine("  --source-root <path>     Корень исходников относительно DocSet");
            Console.WriteLine("  --solution <path>        Путь solution относительно корня исходников");
        }
    }
}
