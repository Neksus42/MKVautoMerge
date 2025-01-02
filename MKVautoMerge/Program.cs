using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // Глобальная «заглушка» для синхронизации вывода в консоль:
    private static readonly object _consoleLock = new object();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Укажите пути к папкам
        string videosDir = @"E:\torrent\Fullmetal.Alchemist.Brotherhood.2009.MVO.STEPonee";
        string audiosDir = @"E:\torrent\Fullmetal.Alchemist.Brotherhood.2009.MVO.STEPonee\Audio";
        string outputDir = @"E:\torrent\Fullmetal.Alchemist.Brotherhood.2009.MVO.STEPonee\right2";

        // Проверяем наличие mkvmerge
        if (!IsMkvmergeInstalled())
        {
            lock (_consoleLock)
            {
                Console.WriteLine("[ОШИБКА] mkvmerge не установлен или не добавлен в PATH.");
            }
            return;
        }

        // Создаём выходную директорию, если её нет
        Directory.CreateDirectory(outputDir);

        // Получаем все .mp4
        DirectoryInfo videosDirectoryInfo = new DirectoryInfo(videosDir);
        FileInfo[] mp4Files = videosDirectoryInfo.GetFiles("*.mp4");

        // Семафор на 2 «пропуска»
        SemaphoreSlim concurrency = new SemaphoreSlim(2, 2);

        // Список задач
        var tasks = new List<Task>();

        // Перебираем все видеофайлы и формируем задачи
        foreach (FileInfo videoFile in mp4Files)
        {
            tasks.Add(Task.Run(async () =>
            {
                await concurrency.WaitAsync();
                try
                {
                    // Выполняем всю логику в отдельном методе
                    await ProcessVideoAsync(videoFile, audiosDir, outputDir);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
        }

        // Ждём, пока все задачи завершатся
        await Task.WhenAll(tasks);

        lock (_consoleLock)
        {
            Console.WriteLine("[ИНФО] Объединение завершено.");
        }

        // Чтобы консоль не закрывалась мгновенно (если запускаете из IDE)
        Console.ReadKey();
    }

    /// <summary>
    /// Выполняет всю логику обработки одного видеофайла:
    /// 1) Определяет номер эпизода;
    /// 2) Находит соответствующие аудио;
    /// 3) Запускает mkvmerge в «поточном» режиме вывода.
    /// </summary>
    private static async Task ProcessVideoAsync(FileInfo videoFile, string audiosDir, string outputDir)
    {
        string baseName = Path.GetFileNameWithoutExtension(videoFile.Name);

        lock (_consoleLock)
        {
            Console.WriteLine($"[ИНФО] Начало обработки: {videoFile.Name}");
        }

        // Пытаемся извлечь номер эпизода по шаблону E##
        Match match = Regex.Match(baseName, @"E(\d{2})");
        if (!match.Success)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Номер эпизода не найден в файле '{baseName}'. Пропускаем.");
            }
            return;
        }

        string episodeNum = match.Groups[1].Value;

        // Находим соответствующие аудиофайлы
        FileInfo[] matchedAudios = GetMatchedAudios(episodeNum, audiosDir);
        if (matchedAudios.Length == 0)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Не найдены аудиофайлы для эпизода '{episodeNum}'. Пропускаем.");
            }
            return;
        }

        // Формируем имя выходного файла
        string outputFile = Path.Combine(outputDir, $"{baseName}.mkv");

        // Собираем аргументы для mkvmerge
        var mkvmergeArgs = new List<string>
        {
            "--output",
            $"\"{outputFile}\"",
            "--no-audio",
            $"\"{videoFile.FullName}\""
        };

        foreach (FileInfo audio in matchedAudios)
        {
            mkvmergeArgs.Add("--language");
            mkvmergeArgs.Add($"0:rus");
            mkvmergeArgs.Add("--track-name");
            mkvmergeArgs.Add($"0:\"Озвучка\"");

            mkvmergeArgs.Add($"\"{audio.FullName}\"");
        }

        string finalArgs = string.Join(" ", mkvmergeArgs);

        lock (_consoleLock)
        {
            Console.WriteLine($"[ИНФО {videoFile.Name}] Запуск mkvmerge {finalArgs}");
        }

        // Запускаем mkvmerge с «поточным» выводом
        bool success = await RunProcessAsync(
            "mkvmerge",
            finalArgs,
            videoFile.Name // Чтобы подписывать вывод
        );

        if (success)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[ИНФО {videoFile.Name}] Успешно объединено: {outputFile}");
            }
        }
        else
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[ОШИБКА {videoFile.Name}] Ошибка при объединении.");
            }
        }
    }

    /// <summary>
    /// Проверяем, установлен ли mkvmerge (пробуем запустить "mkvmerge --version").
    /// Если запустилось без ошибок, считаем, что установлен.
    /// </summary>
    private static bool IsMkvmergeInstalled()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "mkvmerge",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Запускает указанный процесс (filename + arguments) в асинхронном режиме,
    /// перенаправляет вывод построчно и сразу выводит в консоль.
    /// Возвращает true, если код выхода == 0; иначе false.
    /// </summary>
    private static async Task<bool> RunProcessAsync(string fileName, string arguments, string fileLabel)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = psi;

                // Подписываемся на события вывода
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"[{fileLabel} - OUT] {e.Data}");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"[{fileLabel} - ERR] {e.Data}");
                        }
                    }
                };

                process.Start();

                // Начинаем асинхронное чтение потока вывода и ошибок
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Асинхронно ждём завершения
                await Task.Run(() => process.WaitForExit());

                // Возвращаем true, если код выхода 0
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[ОШИБКА {fileLabel}] Не удалось запустить '{fileName}': {ex.Message}");
            }
            return false;
        }
    }

    /// <summary>
    /// Возвращает массив файлов *.mka, в чьём BaseName содержится "- [номер эпизода]".
    /// </summary>
    private static FileInfo[] GetMatchedAudios(string episodeNum, string audiosDir)
    {
        DirectoryInfo audioDirectoryInfo = new DirectoryInfo(audiosDir);
        FileInfo[] allMkaFiles = audioDirectoryInfo.GetFiles("*.mka");

        List<FileInfo> matched = new List<FileInfo>();
        Regex regex = new Regex("-\\s*" + Regex.Escape(episodeNum) + "\\b");

        foreach (FileInfo mkaFile in allMkaFiles)
        {
            string baseName = Path.GetFileNameWithoutExtension(mkaFile.Name);
            if (regex.IsMatch(baseName))
            {
                matched.Add(mkaFile);
            }
        }
        return matched.ToArray();
    }
}
