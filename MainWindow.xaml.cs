using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
// using DiscordRPC.Logging;
using MajdataEdit.AutoSaveModule;
using Microsoft.Win32;
using Newtonsoft.Json;
using Un4seen.Bass;
using Timer = System.Timers.Timer;

namespace MajdataEdit;

/// <summary>
///     MainWindow.xaml 的交互逻辑
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();
    
    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();
    
    public static bool embed_mode = false;
    public static string? controlFilePath = null;
    
    public MainWindow()
    {
        // 解析命令行参数
        var args = Environment.GetCommandLineArgs();
        
        // 检查 embed_mode
        embed_mode = args.Contains("--embed_mode");

        // 只在非 embed_mode 时分配控制台
        if (!embed_mode) AllocConsole();
        
        // 解析 control-file 参数
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].ToLower().Contains("control"))
            {
                controlFilePath = args[i];
                break;
            }
        }
        
        InitializeComponent();
        if (args.Contains("--ForceSoftwareRender"))
        {
            MessageBox.Show("正在以软件渲染模式运行\nソフトウェア・レンダリング・モードで動作\nBooting as software rendering mode.");
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CheckAndStartView();

        TheWindow.Title = GetWindowsTitleString();

        SetWindowGoldenPosition();

        // Discord RPC disabled to prevent connection timeout errors
        // DCRPCclient.Logger = new ConsoleLogger { Level = LogLevel.Warning };
        // DCRPCclient.Initialize();

        var handle = new WindowInteropHelper(this).Handle;
        Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_CPSPEAKERS, handle);
        InitWave();

        ReadSoundEffect();
        ReadEditorSetting();

        // 注册 Simai 语法高亮着色器（AvalonEdit 渲染层着色，不影响文字模型）
        FumenContent.TextArea.TextView.LineTransformers.Add(new SimaiColorizer());
        // 注册多倍行距生成器
        FumenContent.TextArea.TextView.ElementGenerators.Add(new LineSpacingGenerator(1.6));
        // 禁用矩形选择（Alt+拖拽 和 Alt+Shift 方向键）
        FumenContent.Options.EnableRectangularSelection = false;

        // 钩子 AvalonEdit 事件
        FumenContent.TextArea.Caret.PositionChanged += FumenContent_SelectionChanged;
        FumenContent.Document.TextChanged += FumenContent_TextChanged;

        chartChangeTimer.Elapsed += ChartChangeTimer_Elapsed;
        chartChangeTimer.AutoReset = false;
        currentTimeRefreshTimer.Elapsed += CurrentTimeRefreshTimer_Elapsed;
        currentTimeRefreshTimer.Start();
        visualEffectRefreshTimer.Elapsed += VisualEffectRefreshTimer_Elapsed;
        waveStopMonitorTimer.Elapsed += WaveStopMonitorTimer_Elapsed;
        playbackSpeedHideTimer.Elapsed += PlbHideTimer_Elapsed;

        // Initialize and start the control file watcher if control file path is provided
        if (controlFilePath != null)
        {
            _controlFileWatcher = new ControlFileWatcher(this, controlFilePath);
            _controlFileWatcher.StartWatching();
        }

        #region 异常退出处理

        if (!SafeTerminationDetector.Of().IsLastTerminationSafe())
        {
            // 若上次异常退出，则询问打开恢复窗口
            var result = MessageBox.Show(GetLocalizedString("AbnormalTerminationInformation"),
                GetLocalizedString("Attention"), MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                var lastEditPath = File.ReadAllText(SafeTerminationDetector.Of().RecordPath).Trim();
                if (lastEditPath.Length != 0)
                    // 尝试打开上次未正常关闭的谱面 然后再打开恢复页面
                    try
                    {
                        initFromFile(lastEditPath);
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine(error.StackTrace);
                    }

                Menu_AutosaveRecover_Click(new object(), new RoutedEventArgs());
            }
        }

        SafeTerminationDetector.Of().RecordProgramClose();

        #endregion
    }


    //start the view and wait for boot, then set window pos
    private void SetWindowPosTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        var setWindowPosTimer = (Timer)sender!;
        Dispatcher.Invoke(() => { InternalSwitchWindow(); });
        setWindowPosTimer.Stop();
        setWindowPosTimer.Dispose();
    }

    //Window events
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 当窗口尺寸改变时，立即重新初始化波形显示
        // 这确保了在嵌入场景或手动调整窗口大小时都能正确更新
        if (WaveBitmap != null)
        {
            InitWave();
            DrawWave();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
            {
                e.Cancel = true;
                return;
            }

        if (!embed_mode)
        {
            var process = Process.GetProcessesByName("MajdataView");
            if (process.Length > 0)
            {
                var result = MessageBox.Show(GetLocalizedString("AskCloseView"), GetLocalizedString("Attention"),
                    MessageBoxButton.YesNo);
                if (result == MessageBoxResult.Yes)
                    process[0].Kill();
            }
        }

        currentTimeRefreshTimer.Stop();
        visualEffectRefreshTimer.Stop();
        waveStopMonitorTimer.Stop();

        soundSetting.Close();
        //if (bpmtap != null) { bpmtap.Close(); }
        //if (muriCheck != null) { muriCheck.Close(); }
        SaveSetting();

        // 1. 先停止播放状态，使 SE 线程退出 while(isPlaying) 循环，
        //    避免在线程内继续访问即将被释放的 BGM 流。
        isPlaying = false;
        // 2. 统一释放 BGM 三层资源（bgmStream + bgmSourceStream + pinned GCHandle）。
        FreeBgmStream();
        // 3. 显式释放全部音效流，保证句柄不残留（BASS_Free() 会兜底，但显式释放更安全）。
        foreach (var s in new[]
                 {
                     answerStream, judgeStream, judgeBreakStream, judgeExStream,
                     breakStream, breakSlideStream, breakSlideStartStream, judgeBreakSlideStream,
                     slideStream, touchStream, holdRiserStream, allperfectStream,
                     fanfareStream, clockStream, trackStartStream, hanabiStream
                 })
        {
            if (s > 0)
            {
                Bass.BASS_ChannelStop(s);
                Bass.BASS_StreamFree(s);
            }
        }

        Bass.BASS_Stop();
        Bass.BASS_Free();

        // 4. 释放所有 System.Timers.Timer（实现了 IDisposable，仅 Stop 不够干净）。
        currentTimeRefreshTimer.Dispose();
        visualEffectRefreshTimer.Dispose();
        waveStopMonitorTimer.Dispose();
        chartChangeTimer.Dispose();
        playbackSpeedHideTimer.Dispose();

        // 正常退出
        SafeTerminationDetector.Of().RecordProgramClose();

        // Stop the control file watcher
        _controlFileWatcher?.Dispose();
        
        // 只在非 embed_mode 时释放控制台
        embed_mode = Environment.GetCommandLineArgs().Contains("--embed_mode");
        if (!embed_mode)
        {
            FreeConsole();
        }
    }

    //Window grid events
    private void Grid_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            //Console.WriteLine(e.Data.GetData(DataFormats.FileDrop).ToString());
            if (e.Data.GetData(DataFormats.FileDrop).ToString() == "System.String[]")
            {
                var path = ((string[])e.Data.GetData(DataFormats.FileDrop))[0];
                if (path.ToLower().Contains("maidata.txt"))
                {
                    if (!isSaved)
                        if (!AskSave())
                            return;
                    var fileInfo = new FileInfo(path);
                    initFromFile(fileInfo.DirectoryName!);
                }
            }
    }

    private void FindClose_MouseDown(object sender, MouseButtonEventArgs e)
    {
        FindGrid.Visibility = Visibility.Collapsed;
        FumenContent.Focus();
    }

    #region MENU BARS

    private void Menu_New_Click(object sender, RoutedEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
                return;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "track.mp3, track.ogg|track.mp3;track.ogg"
        };
        if ((bool)openFileDialog.ShowDialog()!)
        {
            var fileInfo = new FileInfo(openFileDialog.FileName);
            CreateNewFumen(fileInfo.DirectoryName!);
            initFromFile(fileInfo.DirectoryName!);
        }
    }

    private void Menu_Open_Click(object sender, RoutedEventArgs e)
    {
        if (!isSaved)
            if (!AskSave())
                return;
        var openFileDialog = new OpenFileDialog
        {
            Filter = "maidata.txt|maidata.txt"
        };
        if ((bool)openFileDialog.ShowDialog()!)
        {
            var fileInfo = new FileInfo(openFileDialog.FileName);
            initFromFile(fileInfo.DirectoryName!);
        }
    }

    private void Menu_Save_Click(object sender, RoutedEventArgs e)
    {
        SaveFumen(true);
        SystemSounds.Beep.Play();
    }

    private void Menu_SaveAs_Click(object sender, RoutedEventArgs e)
    {
    }

    private void Menu_CloseChart_Click(object sender, RoutedEventArgs e)
    {
        if (!isSaved) if (!AskSave()) return;
        ClearWindow(true);

        // Broadcast to App in embed mode
        if (embed_mode)
        {
            var payload = new { control = 274 };
            var json = JsonConvert.SerializeObject(payload);
            BroadcastToApp(json);
        }
    }

    private void Menu_ExportRender_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndPause(PlayMethod.Record);
    }

    private void MirrorLeftRight_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.SelectedText, Mirror.HandleType.LRMirror);
        FumenContent.SelectedText = result;
    }

    private void MirrorUpDown_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.SelectedText, Mirror.HandleType.UDMirror);
        FumenContent.SelectedText = result;
    }

    private void Mirror180_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.SelectedText, Mirror.HandleType.HalfRotation);
        FumenContent.SelectedText = result;
    }

    private void Mirror45_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.SelectedText, Mirror.HandleType.Rotation45);
        FumenContent.SelectedText = result;
    }

    private void MirrorCcw45_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var result = Mirror.NoteMirrorHandle(FumenContent.SelectedText, Mirror.HandleType.CcwRotation45);
        FumenContent.SelectedText = result;
    }

    private void BPMtap_MenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var tap = new BPMtap();
        tap.Owner = this;
        tap.Show();
    }

    private void MenuItem_InfomationEdit_Click(object? sender, RoutedEventArgs e)
    {
        var infoWindow = new Infomation();
        SetSavedState(false);
        infoWindow.ShowDialog();
        TheWindow.Title = GetWindowsTitleString(SimaiProcess.title!);
    }

    private void MenuItem_Majnet_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo() { FileName = "https://majdata.net", UseShellExecute = true });
        //maidata.txtの譜面書式
    }

    private void MenuItem_GitHub_Click(object? sender, RoutedEventArgs e) => OpenGitHub();

    /// <summary>
    /// 打开 GitHub 仓库页面。URL 取自 csproj 的 AssemblyTitle
    /// </summary>
    internal static void OpenGitHub()
    {
        var attr = (System.Reflection.AssemblyTitleAttribute?)Attribute.GetCustomAttribute(
            System.Reflection.Assembly.GetExecutingAssembly(),
            typeof(System.Reflection.AssemblyTitleAttribute));
        var url = attr?.Title;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo() { FileName = url, UseShellExecute = true });
    }

    private void MenuItem_SoundSetting_Click(object? sender, RoutedEventArgs e)
    {
        soundSetting = new SoundSetting
        {
            Owner = this
        };
        soundSetting.ShowDialog();
    }

    private void MuriCheck_Click_1(object? sender, RoutedEventArgs e)
    {
        var muriCheck = new MuriCheck
        {
            Owner = this
        };
        muriCheck.Show();
    }

    private void MenuItem_EditorSetting_Click(object? sender, RoutedEventArgs e)
    {
        var esp = new EditorSettingPanel
        {
            Owner = this
        };
        esp.ShowDialog();
    }

    private void MenuItem_Help_Click(object? sender, RoutedEventArgs e)
    {
        var win = new HelpWindow { Owner = this };
        win.ShowDialog();
    }

    private void Menu_ResetViewWindow(object? sender, RoutedEventArgs e)
    {
        if (CheckAndStartView()) return;
        InternalSwitchWindow();
    }

    private void MenuFind_Click(object? sender, RoutedEventArgs e)
    {
        if (FindGrid.Visibility == Visibility.Collapsed)
        {
            FindGrid.Visibility = Visibility.Visible;
            InputText.Focus();
        }
        else
        {
            FindGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void Menu_AutosaveRecover_Click(object? sender, RoutedEventArgs e)
    {
        var asr = new AutoSaveRecover
        {
            Owner = this
        };
        asr.ShowDialog();
    }

    #endregion

    #region 快捷键

    private void PlayAndPause_Executed(object? sender, ExecutedRoutedEventArgs e) { TogglePlayAndPause(); }

    private void StopPlaying_Executed(object? sender, ExecutedRoutedEventArgs e) { TogglePlayAndStop(); }

    private void SaveFile_Executed(object? sender, ExecutedRoutedEventArgs e)
    {
        SaveFumen(true);
        SystemSounds.Beep.Play();
    }

    private void SendToView_Executed(object? sender, ExecutedRoutedEventArgs e) { TogglePlayAndStop(PlayMethod.Op); }

    private void IncreasePlaybackSpeed_Executed(object? sender, ExecutedRoutedEventArgs e)
    {
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING) return;
        var speed = GetPlaybackSpeed();
        Console.WriteLine(speed);
        speed += 0.25f;
        PlbSpdLabel.Content = speed * 100 + "%";
        SetPlaybackSpeed(speed);
        PlbSpdAdjGrid.Visibility = Visibility.Visible;
        playbackSpeedHideTimer.Stop();
        playbackSpeedHideTimer.Start();
    }

    private void DecreasePlaybackSpeed_Executed(object? sender, ExecutedRoutedEventArgs e)
    {
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING) return;
        var speed = GetPlaybackSpeed();
        Console.WriteLine(speed);
        speed -= 0.25f;
        if (speed < 1e-6) return;
        PlbSpdLabel.Content = speed * 100 + "%";
        SetPlaybackSpeed(speed);
        PlbSpdAdjGrid.Visibility = Visibility.Visible;
        playbackSpeedHideTimer.Stop();
        playbackSpeedHideTimer.Start();
    }

    private readonly Timer playbackSpeedHideTimer = new(1000);

    private void PlbHideTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        Dispatcher.Invoke(() => { PlbSpdAdjGrid.Visibility = Visibility.Collapsed; });
        ((Timer)sender!).Stop();
    }

    private void Find_Executed(object? sender, ExecutedRoutedEventArgs e)
    {
        if (FindGrid.Visibility == Visibility.Collapsed)
        {
            FindGrid.Visibility = Visibility.Visible;
            InputText.Focus();
        }
        else
        {
            FindGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void MirrorLR_Executed(object? sender, ExecutedRoutedEventArgs e) { MirrorLeftRight_MenuItem_Click(sender, null); }

    private void MirrorUD_Executed(object? sender, ExecutedRoutedEventArgs e) { MirrorUpDown_MenuItem_Click(sender, null); }

    private void Mirror180_Executed(object? sender, ExecutedRoutedEventArgs e) { Mirror180_MenuItem_Click(sender, null); }

    private void Mirror45_Executed(object? sender, ExecutedRoutedEventArgs e) { Mirror45_MenuItem_Click(sender, null); }

    private void MirrorCcw45_Executed(object? sender, ExecutedRoutedEventArgs e) { MirrorCcw45_MenuItem_Click(sender, null); }

    #endregion

    #region Left componients

    private void PlayAndPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndPause();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleStop();
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = LevelSelector.SelectedIndex;
        if (i < 0 || i >= SimaiProcess.fumens.Length) return;
        SetRawFumenText(SimaiProcess.fumens[i]);
        selectedDifficulty = i;
        LevelTextBox.Text = SimaiProcess.levels[selectedDifficulty];
        SetSavedState(true);
        SimaiProcess.Serialize(GetRawFumenText());
        DrawWave();
    }

    private void LevelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetSavedState(false);
        if (selectedDifficulty == -1) return;
        SimaiProcess.levels[selectedDifficulty] = LevelTextBox.Text;
    }

    private void OffsetTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetSavedState(false);
        try
        {
            SimaiProcess.first = float.Parse(OffsetTextBox.Text);
            SimaiProcess.Serialize(GetRawFumenText());
            DrawWave();
        }
        catch
        {
            SimaiProcess.first = 0f;
        }
    }

    private void OffsetTextBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var offset = float.Parse(OffsetTextBox.Text);
        offset += e.Delta > 0 ? 0.01f : -0.01f;
        OffsetTextBox.Text = offset.ToString();
    }

    private void FollowPlayCheck_Click(object sender, RoutedEventArgs e)
    {
        FumenContent.Focus();
    }

    private void Op_Button_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayAndStop(PlayMethod.Op);
    }

    private void SettingLabel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 单击设置的时候也可以进入设置界面
        var esp = new EditorSettingPanel();
        esp.Owner = this;
        esp.ShowDialog();
    }

    #endregion

    #region Text editor events

    private void FumenContent_SelectionChanged(object? sender, EventArgs e)
    {
        var currentLine = FumenContent.TextArea.Caret.Line;
        NoteNowText.Content = currentLine + " 行";
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING && (bool)FollowPlayCheck.IsChecked!)
            return;
        var time = SimaiProcess.Serialize(GetRawFumenText(), GetRawFumenPosition());

        if (Keyboard.Modifiers == ModifierKeys.Control && (
                Mouse.LeftButton == MouseButtonState.Pressed ||
                Keyboard.IsKeyDown(Key.Left) ||
                Keyboard.IsKeyDown(Key.Right) ||
                Keyboard.IsKeyDown(Key.Up) ||
                Keyboard.IsKeyDown(Key.Down)
            ))
        {
            if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING)
                TogglePause();
            SetBgmPosition(time);
        }

        SimaiProcess.ClearNoteListPlayedState();
        ghostCusorPositionTime = (float)time;
        if (!isPlaying) DrawWave();
    }

    private void FumenContent_TextChanged(object? sender, EventArgs e)
    {
        if (GetRawFumenText() == "" || isLoading) return;
        SetSavedState(false);

        if (chartChangeTimer.Interval < 33)
        {
            SimaiProcess.Serialize(GetRawFumenText(), GetRawFumenPosition());
            DrawWave();
        }
        else
        {
            chartChangeTimer.Stop();
            chartChangeTimer.Start();
        }
    }

    private void FumenContent_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mod = Keyboard.Modifiers;
        var caret = FumenContent.TextArea.Caret;
        var doc = FumenContent.Document;

        // Ctrl+←/→ 逐字符移动光标（替代 AvalonEdit 的按词移动），触发音频定位
        if ((e.Key == Key.Left || e.Key == Key.Right) && mod == ModifierKeys.Control)
        {
            if (e.Key == Key.Left && caret.Offset > 0)
                caret.Offset--;
            else if (e.Key == Key.Right && caret.Offset < doc.TextLength)
                caret.Offset++;
            e.Handled = true;
            return;
        }

        // Ctrl+↑/↓ 逐行移动光标（替代 WPF ScrollViewer 的滚动），触发音频定位
        if ((e.Key == Key.Up || e.Key == Key.Down) && mod == ModifierKeys.Control)
        {
            if (e.Key == Key.Up && caret.Line > 1)
                caret.Line--;
            else if (e.Key == Key.Down && caret.Line < doc.LineCount)
                caret.Line++;
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Z 触发 Redo（替代 Ctrl+Y）
        if (e.Key == Key.Z && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            FumenContent.Document.UndoStack.Redo();
            e.Handled = true;
            return;
        }

        // 禁用 Ctrl+Shift+←/→ （按词扩展选择）
        if ((e.Key == Key.Left || e.Key == Key.Right) && mod == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            return;
        }

        // 禁用所有矩形选择快捷键（Alt+Shift 方向键 / Ctrl+Alt+Shift 方向键）
        // 注：EnableRectangularSelection=false 已阻止 Alt+拖拽，这里处理键盘组合
        if (mod.HasFlag(ModifierKeys.Alt) && mod.HasFlag(ModifierKeys.Shift)
            && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down
                || e.Key == Key.Home || e.Key == Key.End))
        {
            e.Handled = true;
            return;
        }

        // Ctrl + +/-（含主键盘 = 与数字小键盘）调整编辑器字号，并持久化到设置文件
        if (mod == ModifierKeys.Control
            && (e.Key == Key.OemPlus || e.Key == Key.Add
                || e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            const double MinFont = 6;
            const double MaxFont = 64;
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
                FumenContent.FontSize = Math.Min(MaxFont, FumenContent.FontSize + 1);
            else
                FumenContent.FontSize = Math.Max(MinFont, FumenContent.FontSize - 1);
            editorSetting!.FontSize = (float)FumenContent.FontSize;
            SaveEditorSetting();
            e.Handled = true;
            return;
        }

        // 按下Insert键，同时未按下任何组合键，切换覆盖模式
        if (e.Key == Key.Insert && mod == ModifierKeys.None)
        {
            SwitchFumenOverwriteMode();
            e.Handled = true;
        }
    }

    #endregion

    #region Wave displayer

    private void WaveViewZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (deltatime > 1)
            deltatime -= 1;
        DrawWave();
        FumenContent.Focus();
    }

    private void WaveViewZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (deltatime < 10)
            deltatime += 1;
        DrawWave();
        FumenContent.Focus();
    }

    private void MusicWave_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollWave(-e.Delta);
    }

    private void MusicWave_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        lastMousePointX = e.GetPosition(this).X;
    }

    private void MusicWave_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = e.GetPosition(this).X - lastMousePointX;
            lastMousePointX = e.GetPosition(this).X;
            ScrollWave(-delta);
        }

        lastMousePointX = e.GetPosition(this).X;
    }

    private void MusicWave_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InitWave();
        DrawWave();
    }

    #endregion

    #region 速度控制按钮事件处理

    private void DecreaseSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        // 直接调用减速命令，模拟按下Ctrl+o快捷键
        DecreasePlaybackSpeed_Executed(this, null);
    }

    private void IncreaseSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        // 直接调用加速命令，模拟按下Ctrl+p快捷键
        IncreasePlaybackSpeed_Executed(this, null);
    }

    private void JumpToStartButton_Click(object sender, RoutedEventArgs e)
    {
        // 如果正在播放，则不执行跳转操作
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING) return;

        ToggleStop(); // 通过stop清空majdataview
        SetBgmPosition(0); // 将BGM位置设置为0
        SimaiProcess.ClearNoteListPlayedState(); // 清除已播放状态
        DrawWave(); // 强制重绘波形图
        FumenContent.Focus(); // 返回焦点
    }

    #endregion
}