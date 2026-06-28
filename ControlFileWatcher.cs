using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MajdataEdit
{
    public class ControlFileWatcher : IDisposable
    {
        private const int DebounceMs = 200;

        private readonly string _controlFileName;
        private readonly string _controlFilePath;
        private readonly MainWindow _mainWindow;
        private readonly DispatcherTimer _processingTimer;
        private FileSystemWatcher? _watcher;
        private bool _isProcessing = false;

        public ControlFileWatcher(MainWindow mainWindow, string controlFilePath)
        {
            _mainWindow = mainWindow;
            _controlFilePath = controlFilePath;
            _controlFileName = Path.GetFileName(controlFilePath);

            _processingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
            _processingTimer.Tick += ProcessingTimer_Tick;
        }

        public void StartWatching()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_controlFilePath);
                if (directory == null)
                {
                    Console.WriteLine($"[ControlFileWatcher] Error: invalid control file path: {_controlFilePath}");
                    return;
                }

                _watcher = new FileSystemWatcher(directory)
                {
                    Filter = _controlFileName,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _watcher.Created += ScheduleIfMatch;
                _watcher.Changed += ScheduleIfMatch;
                _watcher.Renamed += (s, e) => ScheduleIfMatch(s, e);

                Console.WriteLine($"[ControlFileWatcher] Started watching for {_controlFileName}");

                if (File.Exists(_controlFilePath))
                {
                    ScheduleProcessing();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlFileWatcher] Error starting watcher: {ex.Message}");
            }
        }

        private void ScheduleIfMatch(object sender, FileSystemEventArgs e)
        {
            if (e.Name == _controlFileName)
            {
                ScheduleProcessing();
            }
        }

        private void ScheduleProcessing()
        {
            _processingTimer.Stop();
            _processingTimer.Start();
        }

        private void ProcessingTimer_Tick(object? sender, EventArgs e)
        {
            _processingTimer.Stop();
            ProcessControlFile();
        }

        private void ProcessControlFile()
        {
            if (_isProcessing)
            {
                return;
            }
            _isProcessing = true;

            try
            {
                if (!File.Exists(_controlFilePath))
                {
                    _isProcessing = false;
                    return;
                }

                string[] lines = File.ReadAllLines(_controlFilePath);

                // exit 合法情形一：整文件仅有一行 "exit"
                if (lines.Length == 1 && lines[0].Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[ControlFileWatcher] Received exit command");
                    TryDeleteControlFile();
                    RunOnUi(() => _mainWindow.Close());
                    return;
                }

                // 其余情况必须为结构化指令（首行带 "folder: " 前缀），否则非法
                if (lines.Length < 1 || ParsePrefixedValue(lines[0], "folder: ") == null)
                {
                    Console.WriteLine($"[ControlFileWatcher] Invalid control file: expected 3+ lines or 'exit', got {lines.Length}");
                    _isProcessing = false;
                    return;
                }

                if (lines.Length < 3)
                {
                    Console.WriteLine($"[ControlFileWatcher] Invalid control file: expected 3+ lines, got {lines.Length}");
                    _isProcessing = false;
                    return;
                }

                string? folderPath = ParsePrefixedValue(lines[0], "folder: ");
                string? maidataFilename = ParsePrefixedValue(lines[1], "maidata: ");
                string? trackFilename = ParsePrefixedValue(lines[2], "track: ");
                if (folderPath == null || maidataFilename == null || trackFilename == null)
                {
                    Console.WriteLine("[ControlFileWatcher] Invalid control file: missing 'folder: '/'maidata: '/'track: ' prefixes");
                    _isProcessing = false;
                    return;
                }

                // 可选的 movie 行（第 4 行）
                string? movieFilename = null;
                if (lines.Length >= 4)
                {
                    movieFilename = ParsePrefixedValue(lines[3], "movie: ");
                    if (movieFilename == null)
                    {
                        Console.WriteLine("[ControlFileWatcher] Warning: fourth line ignored (missing 'movie: ' prefix)");
                    }
                }

                // stop command (all three fields are "---")
                if (folderPath == "---" && maidataFilename == "---" && trackFilename == "---")
                {
                    // exit 合法情形二：出现在 stop 三行之后、可选 movie 行之后的位置
                    bool wantsExit = false;
                    if (lines.Length >= 4)
                    {
                        int exitIdx = movieFilename != null ? 4 : 3;
                        if (exitIdx < lines.Length
                            && lines[exitIdx].Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                        {
                            wantsExit = true;
                        }
                    }

                    Console.WriteLine("[ControlFileWatcher] Received stop command");
                    if (wantsExit)
                    {
                        Console.WriteLine("[ControlFileWatcher] Combined with exit: pause then close");
                    }
                    TryDeleteControlFile();
                    RunOnUi(() =>
                    {
                        if (_mainWindow.isPlaying)
                        {
                            _mainWindow.TogglePause();
                        }
                        if (wantsExit)
                        {
                            _mainWindow.Close();
                        }
                    });
                    return;
                }

                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"[ControlFileWatcher] Folder does not exist: {folderPath}");
                    _isProcessing = false;
                    return;
                }

                Console.WriteLine($"[ControlFileWatcher] Loading data from folder: {folderPath}");
                TryDeleteControlFile();

                RunOnUi(() =>
                {
                    if (!_mainWindow.IsSaved)
                    {
                        var result = MessageBox.Show(
                            MainWindow.GetLocalizedString("AskSave"),
                            MainWindow.GetLocalizedString("Warning"),
                            MessageBoxButton.YesNo);
                        if (result == MessageBoxResult.Yes)
                        {
                            _mainWindow.SaveFumen(true);
                        }
                    }
                    _mainWindow.initFromFile(folderPath, maidataFilename, trackFilename, movieFilename);
                    Console.WriteLine($"[ControlFileWatcher] Successfully loaded data from {folderPath}");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlFileWatcher] Error processing control file: {ex.Message}");
                _isProcessing = false;
            }
        }

        private static string? ParsePrefixedValue(string line, string prefix)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith(prefix) ? trimmed.Substring(prefix.Length).Trim() : null;
        }

        private bool TryDeleteControlFile()
        {
            try
            {
                File.Delete(_controlFilePath);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlFileWatcher] Warning: could not delete control file: {ex.Message}");
                return false;
            }
        }

        private void RunOnUi(Action action)
        {
            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ControlFileWatcher] Error in UI action: {ex.Message}");
                }
                finally
                {
                    _isProcessing = false;
                }
            }), DispatcherPriority.Normal);
        }

        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            _processingTimer.Stop();
            _isProcessing = false;
        }

        public void Dispose()
        {
            StopWatching();
            _processingTimer.Stop();
        }
    }
}