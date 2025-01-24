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

    private static readonly object _consoleLock = new object();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        
        string videosDir = @"Episodes";
        string audiosDir = @"Audio";
        string outputDir = @"Output";

        
        if (!IsMkvmergeInstalled())
        {
            lock (_consoleLock)
            {
                Console.WriteLine("No mkvmerge in the PATH");
            }
            return;
        }

        
        Directory.CreateDirectory(outputDir);

        
        DirectoryInfo videosDirectoryInfo = new DirectoryInfo(videosDir);
        FileInfo[] mp4Files = videosDirectoryInfo.GetFiles("*.mp4");

        
        SemaphoreSlim concurrency = new SemaphoreSlim(2, 2);

        
        var tasks = new List<Task>();

        
        foreach (FileInfo videoFile in mp4Files)
        {
            tasks.Add(Task.Run(async () =>
            {
                await concurrency.WaitAsync();
                try
                {
                    
                    await ProcessVideoAsync(videoFile, audiosDir, outputDir);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
        }

        
        await Task.WhenAll(tasks);

        lock (_consoleLock)
        {
            Console.WriteLine("Merge done");
        }

        
        Console.ReadKey();
    }

   
    private static async Task ProcessVideoAsync(FileInfo videoFile, string audiosDir, string outputDir)
    {
        string baseName = Path.GetFileNameWithoutExtension(videoFile.Name);

        lock (_consoleLock)
        {
            Console.WriteLine($"Merge processing: {videoFile.Name}");
        }

        
        Match match = Regex.Match(baseName, @"E(\d{2})");
        if (!match.Success)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"Episode file doesnt found '{baseName}'. Skip.");
            }
            return;
        }

        string episodeNum = match.Groups[1].Value;

        
        FileInfo[] matchedAudios = GetMatchedAudios(episodeNum, audiosDir);
        if (matchedAudios.Length == 0)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"Audio file doesnt found '{episodeNum}'. Skip.");
            }
            return;
        }

        string outputFile = Path.Combine(outputDir, $"{baseName}.mkv");

       
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
            Console.WriteLine($"[{videoFile.Name}] Launch mkvmerge {finalArgs}");
        }

        
        bool success = await RunProcessAsync(
            "mkvmerge",
            finalArgs,
            videoFile.Name 
        );

        if (success)
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[{videoFile.Name}] Merge success: {outputFile}");
            }
        }
        else
        {
            lock (_consoleLock)
            {
                Console.WriteLine($"[{videoFile.Name}] Error in merge.");
            }
        }
    }

   
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

                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"{fileLabel} - OUT {e.Data}");
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        lock (_consoleLock)
                        {
                            Console.WriteLine($"{fileLabel} - ERR {e.Data}");
                        }
                    }
                };

                process.Start();

                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                
                await Task.Run(() => process.WaitForExit());

                
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
