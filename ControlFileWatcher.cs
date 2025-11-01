using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace MajdataEdit
{
    public class ControlFileWatcher : IDisposable
    {
        private readonly string _controlFileName = "HachimiDX-Convert-Majdata-Control.txt";
        private readonly string _controlFilePath;
        private FileSystemWatcher? _watcher;
        private readonly MainWindow _mainWindow;
        private readonly DispatcherTimer _processingTimer;
        private bool _isProcessing = false;

        public ControlFileWatcher(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            _controlFilePath = Path.Combine(Environment.CurrentDirectory, _controlFileName);
            
            Console.WriteLine($"[ControlFileWatcher] Initialized with control file path: {_controlFilePath}");
            
            // Use a timer to debounce file system events
            _processingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _processingTimer.Tick += ProcessingTimer_Tick;
        }

        public void StartWatching()
        {
            try
            {
                // Create watcher for the current directory
                _watcher = new FileSystemWatcher(Environment.CurrentDirectory)
                {
                    Filter = _controlFileName,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                _watcher.Created += OnControlFileChanged;
                _watcher.Changed += OnControlFileChanged;
                _watcher.Renamed += OnControlFileRenamed;

                Console.WriteLine($"[ControlFileWatcher] Started watching for {_controlFileName}");
                
                // Check if file already exists
                if (File.Exists(_controlFilePath))
                {
                    Console.WriteLine($"[ControlFileWatcher] Control file already exists, processing...");
                    ProcessControlFile();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlFileWatcher] Error starting watcher: {ex.Message}");
            }
        }

        private void OnControlFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name == _controlFileName)
            {
                Console.WriteLine($"[ControlFileWatcher] Control file {e.ChangeType}: {e.FullPath}");
                
                // Debounce the processing to avoid multiple rapid triggers
                _processingTimer.Stop();
                _processingTimer.Start();
            }
        }

        private void OnControlFileRenamed(object sender, RenamedEventArgs e)
        {
            if (e.Name == _controlFileName)
            {
                Console.WriteLine($"[ControlFileWatcher] Control file renamed to: {e.Name}");
                
                // Debounce the processing to avoid multiple rapid triggers
                _processingTimer.Stop();
                _processingTimer.Start();
            }
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
                Console.WriteLine("[ControlFileWatcher] Already processing, skipping...");
                return;
            }

            _isProcessing = true;
            
            try
            {
                if (!File.Exists(_controlFilePath))
                {
                    Console.WriteLine($"[ControlFileWatcher] Control file not found: {_controlFilePath}");
                    _isProcessing = false;
                    return;
                }

                Console.WriteLine($"[ControlFileWatcher] Reading control file: {_controlFilePath}");
                
                // Read all lines from the control file
                string[] lines = File.ReadAllLines(_controlFilePath);
                
                if (lines.Length < 3)
                {
                    Console.WriteLine($"[ControlFileWatcher] Invalid control file format: expected 3 lines, got {lines.Length}");
                    _isProcessing = false;
                    return;
                }

                // Parse the three required lines
                string folderLine = lines[0].Trim();
                string maidataLine = lines[1].Trim();
                string trackLine = lines[2].Trim();

                // Validate format
                if (!folderLine.StartsWith("folder: ") || 
                    !maidataLine.StartsWith("maidata: ") || 
                    !trackLine.StartsWith("track: "))
                {
                    Console.WriteLine($"[ControlFileWatcher] Invalid control file format: missing required prefixes");
                    Console.WriteLine($"  Expected: 'folder: xxx', 'maidata: xxx', 'track: xxx'");
                    Console.WriteLine($"  Got: '{folderLine}', '{maidataLine}', '{trackLine}'");
                    _isProcessing = false;
                    return;
                }

                // Extract values
                string folderPath = folderLine.Substring("folder: ".Length).Trim();
                string maidataFilename = maidataLine.Substring("maidata: ".Length).Trim();
                string trackFilename = trackLine.Substring("track: ".Length).Trim();

                Console.WriteLine($"[ControlFileWatcher] Parsed control file:");
                Console.WriteLine($"  Folder: {folderPath}");
                Console.WriteLine($"  Maidata: {maidataFilename}");
                Console.WriteLine($"  Track: {trackFilename}");

                // Validate folder exists
                if (!Directory.Exists(folderPath))
                {
                    Console.WriteLine($"[ControlFileWatcher] Folder does not exist: {folderPath}");
                    _isProcessing = false;
                    return;
                }

                // Delete the control file to prevent repeated processing
                try
                {
                    File.Delete(_controlFilePath);
                    Console.WriteLine($"[ControlFileWatcher] Control file deleted: {_controlFilePath}");
                }
                catch (Exception deleteEx)
                {
                    Console.WriteLine($"[ControlFileWatcher] Warning: Could not delete control file: {deleteEx.Message}");
                }

                // Load the data using the main window's method
                Console.WriteLine($"[ControlFileWatcher] Loading data from folder: {folderPath}");
                
                // Use dispatcher to call the main window method on the UI thread
                _mainWindow.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _mainWindow.initFromFile(folderPath, maidataFilename, trackFilename);
                        Console.WriteLine($"[ControlFileWatcher] Successfully loaded data from {folderPath}");
                    }
                    catch (Exception loadEx)
                    {
                        Console.WriteLine($"[ControlFileWatcher] Error loading data: {loadEx.Message}");
                    }
                    finally
                    {
                        _isProcessing = false;
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlFileWatcher] Error processing control file: {ex.Message}");
                _isProcessing = false;
            }
        }

        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
                Console.WriteLine($"[ControlFileWatcher] Stopped watching for {_controlFileName}");
            }
            
            _processingTimer.Stop();
        }

        public void Dispose()
        {
            StopWatching();
            _processingTimer.Stop();
        }
    }
}