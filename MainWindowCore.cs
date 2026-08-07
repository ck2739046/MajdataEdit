using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// using DiscordRPC;
using MajdataEdit.AutoSaveModule;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Extensions;
using Brush = System.Drawing.Brush;
using Color = System.Drawing.Color;
using DashStyle = System.Drawing.Drawing2D.DashStyle;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using Pen = System.Drawing.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Timer = System.Timers.Timer;

namespace MajdataEdit;

public partial class MainWindow : Window
{
    private const string majSettingFilename = "majSetting.json";
    private const string editorSettingFilename = "EditorSetting.json";
    public static readonly string MAJDATA_VERSION_STRING = $"v{Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)}";

    public static string maidataDir = "";
    public static string currentTrackFilename = "";
    public static string? currentMovieFilename = null;
    private ControlFileWatcher? _controlFileWatcher;

    /// <summary>
    ///     When true, the close was requested via control file.
    ///     AskSave() will use YesNo instead of YesNoCancel to prevent blocking the external caller indefinitely.
    /// </summary>
    public bool IsExitFromControlFile { get; set; }

    //float[] wavedBs;
    private readonly short[][] waveRaws = new short[3][];
    public Timer chartChangeTimer = new(1000); // 谱面变更延迟解析]\
    private readonly Timer currentTimeRefreshTimer = new(100);

    // public DiscordRpcClient DCRPCclient = new("1068882546932326481");

    private float deltatime = 4f;
    public EditorSetting? editorSetting;

    private bool fumenOverwriteMode; //谱面文本覆盖模式
    private float ghostCusorPositionTime;
    private bool isDrawing;
    private bool isLoading;
    private bool isReplaceConformed;

    private bool isSaved = true;
    public bool IsSaved => isSaved;
    private EditorControlMethod lastEditorState;

    private double lastMousePointX; //Used for drag scroll

    private int selectedDifficulty = -1;
    private double songLength;

    private SoundSetting soundSetting = new();


    //*UI DRAWING
    private readonly Timer visualEffectRefreshTimer = new(1);

    private WriteableBitmap? WaveBitmap;

    //*TEXTBOX CONTROL
    private string GetRawFumenText()
    {
        return FumenContent.Document.Text.Replace("\r", "");
    }

    private void SetRawFumenText(string content)
    {
        isLoading = true;
        if (content == null)
        {
            FumenContent.Document.Text = "";
            isLoading = false;
            return;
        }

        FumenContent.Document.Text = content;
        isLoading = false;
    }

    private long GetRawFumenPosition()
    {
        return FumenContent.CaretOffset;
    }

    private void SeekTextFromTime()
    {
        var time = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
        var timingList = new List<SimaiTimingPoint>();
        timingList.AddRange(SimaiProcess.timinglist);
        var noteList = SimaiProcess.notelist;
        if (SimaiProcess.timinglist.Count <= 0) return;
        timingList.Sort((x, y) => Math.Abs(time - x.time).CompareTo(Math.Abs(time - y.time)));
        var theNote = timingList[0];
        timingList.Clear();
        timingList.AddRange(SimaiProcess.timinglist);
        FumenContent.CaretOffset = FumenContent.Document.GetOffset(
            theNote.rawTextPositionY + 1, theNote.rawTextPositionX + 1);
        FumenContent.ScrollToLine(theNote.rawTextPositionY + 1);
    }

    private void SeekTextFromIndex(int noteGroupIndex)
    {
        if (SimaiProcess.notelist.Count > noteGroupIndex + 1 && noteGroupIndex >= 0)
        {
            var theNote = SimaiProcess.notelist[noteGroupIndex];
            FumenContent.CaretOffset = FumenContent.Document.GetOffset(
                theNote.rawTextPositionY + 1, theNote.rawTextPositionX + 1);
            FumenContent.ScrollToLine(theNote.rawTextPositionY + 1);
        }
    }

    public void ScrollToFumenContentSelection(int positionX, int positionY)
    {
        FumenContent.CaretOffset = FumenContent.Document.GetOffset(positionY + 1, positionX + 1);
        FumenContent.Focus();
        Focus();

        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING && (bool)FollowPlayCheck.IsChecked!)
            return;
        var time = SimaiProcess.Serialize(GetRawFumenText(), GetRawFumenPosition());
        SetBgmPosition(time);
        //Console.WriteLine("SelectionChanged");
        SimaiProcess.ClearNoteListPlayedState();
        ghostCusorPositionTime = (float)time;
    }

    //*FIND AND REPLACE
    private void Find_icon_MouseDown(object? sender, MouseButtonEventArgs e)
    {
        FindAndScroll();
    }

    private void Replace_icon_MouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (!isReplaceConformed)
        {
            FindAndScroll();
            return;
        }

        if (lastFindStart >= 0 && lastFindLength > 0 &&
            FumenContent.SelectionStart == lastFindStart &&
            FumenContent.SelectionLength == lastFindLength)
        {
            FumenContent.SelectedText = ReplaceText.Text;
            FindAndScroll();
        }
        else
        {
            isReplaceConformed = false;
        }
    }

    private int lastFindStart = -1;
    private int lastFindLength;

    public void FindAndScroll()
    {
        var docText = GetRawFumenText();
        var input = InputText.Text;
        if (string.IsNullOrEmpty(input))
        {
            isReplaceConformed = false;
            return;
        }

        var startIndex = FumenContent.CaretOffset;
        var foundIndex = docText.IndexOf(input, Math.Min(startIndex, docText.Length),
            StringComparison.CurrentCultureIgnoreCase);

        if (foundIndex < 0)
        {
            foundIndex = docText.IndexOf(input, 0, StringComparison.CurrentCultureIgnoreCase);
            if (foundIndex < 0)
            {
                isReplaceConformed = false;
                return;
            }
        }

        FumenContent.Select(foundIndex, input.Length);
        lastFindStart = foundIndex;
        lastFindLength = input.Length;
        FumenContent.Focus();
        isReplaceConformed = true;
    }

    //*FILE CONTROL
    public void initFromFile(string path, string? maidataFilename = null, string? trackFilename = null, string? movieFilename = null) //file name should not be included in path
    {
        if (soundSetting != null) soundSetting.Close();
        if (editorSetting == null) ReadEditorSetting();

        // Use provided filenames or fall back to defaults
        var actualMaidataFilename = maidataFilename ?? "maidata.txt";

        bool useOgg;
        if (trackFilename != null)
        {
            useOgg = string.Equals(
                Path.GetFileNameWithoutExtension(trackFilename),
                "ogg",
                StringComparison.OrdinalIgnoreCase
            );
            currentTrackFilename = trackFilename;
        }
        else
        {
            useOgg = File.Exists(path + "/" + "track.ogg");
            currentTrackFilename = "track" + (useOgg ? ".ogg" : ".mp3");
        }

        currentMovieFilename = movieFilename;

        var audioPath = path + "/" + currentTrackFilename;
        var dataPath = path + "/" + actualMaidataFilename;

        Console.WriteLine("Loading from " + dataPath + " and " + audioPath);
        if (!File.Exists(audioPath))
        {
            MessageBox.Show(GetLocalizedString("NoTrack"), GetLocalizedString("Error"));
            return;
        }

        if (!File.Exists(dataPath))
        {
            MessageBox.Show(GetLocalizedString("NoMaidata_txt"), GetLocalizedString("Error"));
            return;
        }

        maidataDir = path;
        SafeTerminationDetector.Of().ChangePath(maidataDir);
        SetRawFumenText("");
        // 在加载新谱面前停止所有旧播放状态：
        // 退出 SE 线程、停循环音效(touchHold_riser)、停刷新 timer、通知 MajdataView 停止
        isPlaying = false;
        isPlan2Stop = false;
        if (holdRiserStream > 0) Bass.BASS_ChannelStop(holdRiserStream);
        waveStopMonitorTimer.Stop();
        visualEffectRefreshTimer.Stop();
        sendRequestStop(silentOnFailure: true);
        FreeBgmStream();

        // soundSetting.Close();
        // 以内存方式加载音频：把整个文件读入字节数组并固定(pin)，再用 BASS 的内存流重载创建解码流。
        // 这样 BASS 全程不持有任何音频文件句柄 —— 关谱面/换谱面后即可立即删除/移动源文件，
        // 彻底解决 StreamFree 之后 .ogg/.mp3 文件句柄仍残留、被 MajdataEdit.exe 占用无法删除的问题。
        // （上一次的缓冲已在前面的 FreeBgmStream() 中释放。）
        _bgmFileBuffer = File.ReadAllBytes(audioPath);
        _bgmFileHandle = GCHandle.Alloc(_bgmFileBuffer, GCHandleType.Pinned);
        var decodeStream = Bass.BASS_StreamCreateFile(
            _bgmFileHandle.AddrOfPinnedObject(), 0L, _bgmFileBuffer.LongLength, BASSFlag.BASS_STREAM_DECODE);
        bgmSourceStream = decodeStream; // 显式跟踪源解码流，便于释放
        bgmStream = BassFx.BASS_FX_TempoCreate(decodeStream, BASSFlag.BASS_DEFAULT);
        //Bass.BASS_StreamCreateFile(audioPath, 0L, 0L, BASSFlag.BASS_SAMPLE_FLOAT);

        Bass.BASS_ChannelSetAttribute(bgmStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_BGM_Level);
        Bass.BASS_ChannelSetAttribute(trackStartStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_BGM_Level);
        Bass.BASS_ChannelSetAttribute(allperfectStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_BGM_Level);
        Bass.BASS_ChannelSetAttribute(fanfareStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_BGM_Level);
        Bass.BASS_ChannelSetAttribute(clockStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_BGM_Level);
        Bass.BASS_ChannelSetAttribute(answerStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Answer_Level);
        Bass.BASS_ChannelSetAttribute(judgeStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Judge_Level);
        Bass.BASS_ChannelSetAttribute(judgeBreakStream, BASSAttribute.BASS_ATTRIB_VOL,
            editorSetting!.Default_Break_Level);
        Bass.BASS_ChannelSetAttribute(judgeBreakSlideStream, BASSAttribute.BASS_ATTRIB_VOL,
            editorSetting!.Default_Break_Slide_Level);
        Bass.BASS_ChannelSetAttribute(slideStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Slide_Level);
        Bass.BASS_ChannelSetAttribute(breakSlideStartStream, BASSAttribute.BASS_ATTRIB_VOL,
            editorSetting!.Default_Slide_Level);
        Bass.BASS_ChannelSetAttribute(breakStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Break_Level);
        Bass.BASS_ChannelSetAttribute(breakSlideStream, BASSAttribute.BASS_ATTRIB_VOL,
            editorSetting!.Default_Break_Slide_Level);
        Bass.BASS_ChannelSetAttribute(judgeExStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Ex_Level);
        Bass.BASS_ChannelSetAttribute(touchStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Touch_Level);
        Bass.BASS_ChannelSetAttribute(hanabiStream, BASSAttribute.BASS_ATTRIB_VOL, editorSetting!.Default_Hanabi_Level);
        Bass.BASS_ChannelSetAttribute(holdRiserStream, BASSAttribute.BASS_ATTRIB_VOL,
            editorSetting!.Default_Hanabi_Level);
        var info = Bass.BASS_ChannelGetInfo(bgmStream);
        if (info.freq != 44100 && info.freq != 48000) MessageBox.Show(GetLocalizedString("Warn44100Hz"), GetLocalizedString("Attention"));
        ReadWaveFromFile();
        SimaiProcess.ClearData();

        if (!SimaiProcess.ReadData(dataPath))
        {
            // ReadData 失败时，释放本次刚分配的 BGM 资源
            FreeBgmStream();
            return;
        }


        if (LevelSelector.Items.Count > 0)
            LevelSelector.SelectedItem = LevelSelector.Items[0];
        ReadSetting();
        if (selectedDifficulty >= 0 && selectedDifficulty < SimaiProcess.fumens.Length)
            SetRawFumenText(SimaiProcess.fumens[selectedDifficulty]);
        SeekTextFromTime();
        SimaiProcess.Serialize(GetRawFumenText());
        FumenContent.Focus();
        DrawWave();

        OffsetTextBox.Text = SimaiProcess.first.ToString();

        Cover.Visibility = Visibility.Collapsed;
        MenuEdit.IsEnabled = true;
        VolumnSetting.IsEnabled = true;
        MenuMuriCheck.IsEnabled = true;
        Menu_ExportRender.IsEnabled = true;
        AutoSaveManager.Of().SetAutoSaveEnable(true);
        SetSavedState(true);
    }

    private void ReadWaveFromFile()
    {
        // 复用 initFromFile 已加载并固定的 _bgmFileBuffer，基于内存创建独立临时解码流读取波形。
        // 不复用 bgmSourceStream：它正被 Tempo 流 bgmStream 占用，复用会破坏其播放位置。
        var bgmDecode = Bass.BASS_StreamCreateFile(
            _bgmFileHandle.AddrOfPinnedObject(), 0L, _bgmFileBuffer!.LongLength, BASSFlag.BASS_STREAM_DECODE);
        try
        {
            songLength = Bass.BASS_ChannelBytes2Seconds(bgmDecode,
                Bass.BASS_ChannelGetLength(bgmDecode, BASSMode.BASS_POS_BYTE));
            var bgmInfo = Bass.BASS_ChannelGetInfo(bgmDecode);
            var freq = bgmInfo.freq;
            var sampleCount = (long)(songLength * freq * 2);
            var bgmRAW = new short[sampleCount];
            // 从内存解码流读取 16-bit PCM
            var rawHandle = GCHandle.Alloc(bgmRAW, GCHandleType.Pinned);
            try
            {
                Bass.BASS_ChannelGetData(bgmDecode, rawHandle.AddrOfPinnedObject(), (int)(sampleCount * 2));
            }
            finally
            {
                rawHandle.Free();
            }

            waveRaws[0] = new short[sampleCount / 20 + 1];
            for (var i = 0; i < sampleCount; i = i + 20) waveRaws[0][i / 20] = bgmRAW[i];
            waveRaws[1] = new short[sampleCount / 50 + 1];
            for (var i = 0; i < sampleCount; i = i + 50) waveRaws[1][i / 50] = bgmRAW[i];
            waveRaws[2] = new short[sampleCount / 100 + 1];
            for (var i = 0; i < sampleCount; i = i + 100) waveRaws[2][i / 100] = bgmRAW[i];
        }
        catch (Exception e)
        {
            MessageBox.Show("mp3/ogg解码失败。\nMP3/OGG Decode fail.\n" + e.Message + Bass.BASS_ErrorGetCode());
            Process.Start("https://github.com/LingFeng-bbben/MajdataEdit/issues/26");
        }
        finally
        {
            Bass.BASS_StreamFree(bgmDecode);
        }
    }

    /// <summary>
    ///     Clear all chart-related data and optionally reset UI to empty state.
    ///     Does NOT check if file is saved. Always call AskSave() before this if needed.
    /// </summary>
    /// <param name="setEmpty">Whether to reset UI elements to empty state</param>
    public void ClearWindow(bool setEmpty = false)
    {
        ToggleStop();
        FreeBgmStream();

        SaveSetting();

        // clear data
        soundSetting?.Close();
        FumenContent.Document.Text = "";
        SimaiProcess.ClearData();
        LevelSelector.SelectedIndex = -1;
        // suppress SelectionChanged from firing during clear
        selectedDifficulty = -1;
        OffsetTextBox.Text = "";

        // about save
        AutoSaveManager.Of().SetAutoSaveEnable(false);
        SetSavedState(true);

        if (setEmpty) set_empty();
    }

    /// <summary>
    ///     Release the BGM (chart audio) stream if one is loaded.
    ///     BGM 的源解码流基于内存缓冲创建，因此释放 BASS 流后还要解除缓冲的固定。
    /// </summary>
    private void FreeBgmStream()
    {
        // 先释放 Tempo 流，再显式释放源解码流，最后解除内存缓冲的固定。
        if (bgmStream > 0)
        {
            Bass.BASS_ChannelStop(bgmStream);
            Bass.BASS_StreamFree(bgmStream);
            bgmStream = -114514;
        }
        if (bgmSourceStream > 0)
        {
            Bass.BASS_StreamFree(bgmSourceStream);
            bgmSourceStream = -114514;
        }
        // 必须在两个流都释放之后再解除固定：流对象在存活期间引用这段内存。
        if (_bgmFileHandle.IsAllocated)
        {
            _bgmFileHandle.Free();
        }
        _bgmFileBuffer = null;
    }

    /// <summary>
    ///     Reset window UI to empty/idle state.
    ///     Only configures program-logic-unrelated UI elements like availability and title bar text.
    /// </summary>
    public void set_empty()
    {
        isLoading = false;

        // show cover
        Cover.Visibility = Visibility.Visible;

        // ready for play
        Op_Button.IsEnabled = true;
        PlayAndPauseButton.Content = "▶";

        // limit for menu
        MenuEdit.IsEnabled = false;
        VolumnSetting.IsEnabled = false;
        Menu_ExportRender.IsEnabled = false;
        MenuMuriCheck.IsEnabled = false;

        // window title
        TheWindow.Title = GetWindowsTitleString();

        // focus
        Cover.Focus();
    }

    private void SetSavedState(bool state)
    {
        if (state)
        {
            isSaved = true;
            LevelSelector.IsEnabled = true;
            TheWindow.Title = GetWindowsTitleString(SimaiProcess.title!);
        }
        else
        {
            isSaved = false;
            LevelSelector.IsEnabled = false;
            TheWindow.Title = GetWindowsTitleString(GetLocalizedString("Unsaved") + SimaiProcess.title!);
            AutoSaveManager.Of().SetFileChanged();
        }
    }

    /// <summary>
    ///     Ask the user and save fumen.
    /// </summary>
    /// <returns>Return false if user cancel the action</returns>
    private bool AskSave()
    {
        var buttons = IsExitFromControlFile ? MessageBoxButton.YesNo : MessageBoxButton.YesNoCancel;
        
        var result = MessageBox.Show(GetLocalizedString("AskSave"), GetLocalizedString("Warning"),
            buttons);

        if (result == MessageBoxResult.Yes)
        {
            SaveFumen(true);
            return true;
        }

        if (result == MessageBoxResult.Cancel) return false;
        return true;
    }

    public void SaveFumen(bool writeToDisk = false)
    {
        if (selectedDifficulty == -1) return;
        SimaiProcess.fumens[selectedDifficulty] = GetRawFumenText();
        SimaiProcess.first = float.Parse(OffsetTextBox.Text);
        if (maidataDir == "")
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "maidata.txt|maidata.txt",
                OverwritePrompt = true
            };
            if ((bool)saveDialog.ShowDialog()!) maidataDir = new FileInfo(saveDialog.FileName).DirectoryName!;
        }

        SimaiProcess.SaveData(maidataDir + "/maidata.bak.txt");
        SaveSetting();
        if (writeToDisk)
        {
            SimaiProcess.SaveData(maidataDir + "/maidata.txt");
            SetSavedState(true);
        }
    }

    private void SaveSetting()
    {
        if (maidataDir == "") return;
        var setting = new MajSetting
        {
            lastEditDiff = selectedDifficulty,
            lastEditTime = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream))
        };
        Bass.BASS_ChannelGetAttribute(bgmStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.BGM_Level);
        Bass.BASS_ChannelGetAttribute(answerStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Answer_Level);
        Bass.BASS_ChannelGetAttribute(judgeStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Judge_Level);
        Bass.BASS_ChannelGetAttribute(judgeBreakStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Break_Level);
        Bass.BASS_ChannelGetAttribute(breakSlideStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Break_Slide_Level);
        Bass.BASS_ChannelGetAttribute(judgeExStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Ex_Level);
        Bass.BASS_ChannelGetAttribute(touchStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Touch_Level);
        Bass.BASS_ChannelGetAttribute(slideStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Slide_Level);
        Bass.BASS_ChannelGetAttribute(hanabiStream, BASSAttribute.BASS_ATTRIB_VOL, ref setting.Hanabi_Level);
        var json = JsonConvert.SerializeObject(setting);
        File.WriteAllText(maidataDir + "/" + majSettingFilename, json);
    }

    private void ReadSetting()
    {
        var path = maidataDir + "/" + majSettingFilename;
        if (!File.Exists(path)) return;
        var setting = JsonConvert.DeserializeObject<MajSetting>(File.ReadAllText(path));
        var diff = setting!.lastEditDiff;
        if (diff >= 0 && diff < LevelSelector.Items.Count)
        {
            LevelSelector.SelectedIndex = diff;
            selectedDifficulty = diff;
        }
        SetBgmPosition(setting.lastEditTime);
        Bass.BASS_ChannelSetAttribute(bgmStream, BASSAttribute.BASS_ATTRIB_VOL, setting.BGM_Level);
        Bass.BASS_ChannelSetAttribute(trackStartStream, BASSAttribute.BASS_ATTRIB_VOL, setting.BGM_Level);
        Bass.BASS_ChannelSetAttribute(allperfectStream, BASSAttribute.BASS_ATTRIB_VOL, setting.BGM_Level);
        Bass.BASS_ChannelSetAttribute(fanfareStream, BASSAttribute.BASS_ATTRIB_VOL, setting.BGM_Level);
        Bass.BASS_ChannelSetAttribute(clockStream, BASSAttribute.BASS_ATTRIB_VOL, setting.BGM_Level);
        Bass.BASS_ChannelSetAttribute(answerStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Answer_Level);
        Bass.BASS_ChannelSetAttribute(judgeStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Judge_Level);
        Bass.BASS_ChannelSetAttribute(judgeBreakStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Break_Level);
        Bass.BASS_ChannelSetAttribute(judgeBreakSlideStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Break_Slide_Level);
        Bass.BASS_ChannelSetAttribute(slideStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Slide_Level);
        Bass.BASS_ChannelSetAttribute(breakSlideStartStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Slide_Level);
        Bass.BASS_ChannelSetAttribute(breakStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Break_Level);
        Bass.BASS_ChannelSetAttribute(breakSlideStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Break_Slide_Level);
        Bass.BASS_ChannelSetAttribute(judgeExStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Ex_Level);
        Bass.BASS_ChannelSetAttribute(touchStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Touch_Level);
        Bass.BASS_ChannelSetAttribute(hanabiStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Hanabi_Level);
        Bass.BASS_ChannelSetAttribute(holdRiserStream, BASSAttribute.BASS_ATTRIB_VOL, setting.Hanabi_Level);

        SaveSetting(); // 覆盖旧版本setting
    }

    private void CreateNewFumen(string path)
    {
        if (File.Exists(path + "/maidata.txt"))
            MessageBox.Show(GetLocalizedString("MaidataExist"));
        else
            File.WriteAllText(path + "/maidata.txt",
                "&title=" + GetLocalizedString("SetTitle") + "\n" +
                "&artist=" + GetLocalizedString("SetArtist") + "\n" +
                "&des=" + GetLocalizedString("SetDes") + "\n" +
                "&first=0\n");
    }

    private void CreateEditorSetting()
    {
        editorSetting = new EditorSetting
        {
            RenderMode =
            RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly ? 1 : 0 // 使用命令行指定强制软件渲染时，同步修改配置值
        };

        File.WriteAllText(editorSettingFilename, JsonConvert.SerializeObject(editorSetting, Formatting.Indented));

        var esp = new EditorSettingPanel(true)
        {
            Owner = this
        };
        esp.ShowDialog();
    }

    private void ReadEditorSetting()
    {
        if (!File.Exists(editorSettingFilename)) CreateEditorSetting();
        var json = File.ReadAllText(editorSettingFilename);
        editorSetting = JsonConvert.DeserializeObject<EditorSetting>(json)!;

        if (RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly)
            //如果没有通过命令行预先指定渲染模式，则使用设置项的渲染模式
            RenderOptions.ProcessRenderMode =
                editorSetting.RenderMode == 0 ? RenderMode.Default : RenderMode.SoftwareOnly;
        else
            //如果通过命令行指定了使用软件渲染模式，则覆盖设置项
            editorSetting.RenderMode = 1;

        LocalizeDictionary.Instance.Culture = new CultureInfo(editorSetting.Language);
        AddGesture(editorSetting.PlayPauseKey, "PlayAndPause");
        AddGesture(editorSetting.PlayStopKey, "StopPlaying");
        AddGesture(editorSetting.SaveKey, "SaveFile");
        AddGesture(editorSetting.SendViewerKey, "SendToView");
        AddGesture(editorSetting.IncreasePlaybackSpeedKey, "IncreasePlaybackSpeed");
        AddGesture(editorSetting.DecreasePlaybackSpeedKey, "DecreasePlaybackSpeed");
        AddGesture("Ctrl+f", "Find");
        AddGesture(editorSetting.MirrorLeftRightKey, "MirrorLR");
        AddGesture(editorSetting.MirrorUpDownKey, "MirrorUD");
        AddGesture(editorSetting.Mirror180Key, "Mirror180");
        AddGesture(editorSetting.Mirror45Key, "Mirror45");
        AddGesture(editorSetting.MirrorCcw45Key, "MirrorCcw45");
        FumenContent.FontSize = editorSetting.FontSize;

        ViewerCover.Content = editorSetting.backgroundCover.ToString();
        ViewerSpeed.Content = editorSetting.playSpeed.ToString("F1"); // 转化为形如"7.0", "9.5"这样的速度
        ViewerTouchSpeed.Content = editorSetting.touchSpeed.ToString("F1");

        chartChangeTimer.Interval = editorSetting.ChartRefreshDelay; // 设置更新延迟

        SaveEditorSetting(); // 覆盖旧版本setting
    }

    public void SaveEditorSetting()
    {
        File.WriteAllText(editorSettingFilename, JsonConvert.SerializeObject(editorSetting, Formatting.Indented));
    }

    private void AddGesture(string keyGusture, string command)
    {
        var gesture = (InputGesture) new KeyGestureConverter().ConvertFromString(keyGusture)!;
        var inputBinding = new InputBinding((ICommand)Resources[command], gesture);
        InputBindings.Add(inputBinding);
    }

    // This update very freqently to Draw FFT wave.
    private void VisualEffectRefreshTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            DrawFFT();
            DrawWave();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }

    // 谱面变更延迟解析
    private void ChartChangeTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        Console.WriteLine("TextChanged");
        Dispatcher.Invoke(
            delegate
            {
                SimaiProcess.Serialize(GetRawFumenText(), GetRawFumenPosition());
                DrawWave();
            }
        );
    }

    private void DrawFFT()
    {
        Dispatcher.InvokeAsync(() =>
        {
            //Scroll WaveView
            var currentTime = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
            //MusicWave.Margin = new Thickness(-currentTime / sampleTime * zoominPower, Margin.Left, MusicWave.Margin.Right, Margin.Bottom);
            //MusicWaveCusor.Margin = new Thickness(-currentTime / sampleTime * zoominPower, Margin.Left, MusicWave.Margin.Right, Margin.Bottom);

            var writableBitmap = new WriteableBitmap(255, 255, 72, 72, PixelFormats.Pbgra32, null);
            FFTImage.Source = writableBitmap;
            writableBitmap.Lock();
            var backBitmap = new Bitmap(255, 255, writableBitmap.BackBufferStride,
                PixelFormat.Format32bppArgb, writableBitmap.BackBuffer);

            var graphics = Graphics.FromImage(backBitmap);
            graphics.Clear(Color.Transparent);

            var fft = new float[1024];
            Bass.BASS_ChannelGetData(bgmStream, fft, (int)BASSData.BASS_DATA_FFT1024);
            var points = new PointF[1024];
            for (var i = 0; i < fft.Length; i++)
                points[i] = new PointF((float)Math.Log10(i + 1) * 100f, 240 - fft[i] * 256); //semilog

            graphics.DrawCurve(new Pen(Color.LightSkyBlue, 1), points);


            //no please
            /*
            var isSuccess = new Visuals().CreateSpectrumWave(bgmStream, graphics, new System.Drawing.Rectangle(0, 0, 255, 255),
                System.Drawing.Color.White, System.Drawing.Color.Red,
                System.Drawing.Color.Black, 1,
                false, false, false);
            Console.WriteLine(isSuccess);
            */
            graphics.Flush();
            graphics.Dispose();
            backBitmap.Dispose();

            writableBitmap.AddDirtyRect(new Int32Rect(0, 0, 255, 255));
            writableBitmap.Unlock();
        });
    }

    private void InitWave()
    {
        // 使用 TopMenu 的实际宽度，它跨越整个窗口宽度（Grid.ColumnSpan="3"）
        // 在嵌入场景下，ActualWidth 会反映真实的渲染宽度，而 Window.Width 可能不准确
        // 经过实测，ActualWidth+16才与Width相等 (1920x1080, windows缩放100%)
        var new_width = TopMenu.ActualWidth > 10 ? (int)TopMenu.ActualWidth + 16 - 2 : (int)Width - 2;
        var height = (int)MusicWave.Height;
        Console.WriteLine($"InitWave - Window.Width: {Width}, TopMenu.ActualWidth: {TopMenu.ActualWidth}, Using width: {new_width}, embed_mode: {MainWindow.embed_mode}");
        WaveBitmap = new WriteableBitmap(new_width, height, 72, 72, PixelFormats.Pbgra32, null);
        MusicWave.Source = WaveBitmap;
    }

    private void DrawWave()
    {
        if (isDrawing) return;
        if (WaveBitmap == null) return;

        Dispatcher.Invoke(() =>
        {
            isDrawing = true;
            var width = WaveBitmap.PixelWidth;
            var height = WaveBitmap.PixelHeight;

            if (waveRaws[0] == null)
            {
                isDrawing = false;
                return;
            }

            WaveBitmap.Lock();

            //the process starts
            var backBitmap = new Bitmap(width, height, WaveBitmap.BackBufferStride,
                PixelFormat.Format32bppArgb, WaveBitmap.BackBuffer);
            var graphics = Graphics.FromImage(backBitmap);
            var currentTime = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));

            graphics.Clear(Color.FromArgb(100, 0, 0, 0));

            var resample = (int)deltatime - 1;
            if (resample > 1 && resample <= 3) resample = 1;
            if (resample > 3) resample = 2;
            var waveLevels = waveRaws[resample];

            var step = songLength / waveLevels.Length;
            var startindex = (int)((currentTime - deltatime) / step);
            var stopindex = (int)((currentTime + deltatime) / step);
            var linewidth = backBitmap.Width / (float)(stopindex - startindex);
            var pen = new Pen(Color.Green, linewidth);
            var points = new List<PointF>();
            for (var i = startindex; i < stopindex; i = i + 1)
            {
                if (i < 0) i = 0;
                if (i >= waveLevels.Length - 1) break;

                var x = (i - startindex) * linewidth;
                var y = waveLevels[i] / 65535f * height + height / 2;

                points.Add(new PointF(x, y));
            }

            graphics.DrawLines(pen, points.ToArray());

            //Draw Bpm lines
            var lastbpm = -1f;
            var bpmChangeTimes = new List<double>(); //在什么时间变成什么值
            var bpmChangeValues = new List<float>();
            bpmChangeTimes.Clear();
            bpmChangeValues.Clear();
            foreach (var timing in SimaiProcess.timinglist)
                if (timing.currentBpm != lastbpm)
                {
                    bpmChangeTimes.Add(timing.time);
                    bpmChangeValues.Add(timing.currentBpm);
                    lastbpm = timing.currentBpm;
                }

            bpmChangeTimes.Add(Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetLength(bgmStream)));

            double time = SimaiProcess.first;
            var signature = 4; //预留拍号
            var currentBeat = 1;
            var timePerBeat = 0d;
            pen = new Pen(Color.Yellow, 1);
            var strongBeat = new List<double>();
            var weakBeat = new List<double>();
            for (var i = 1; i < bpmChangeTimes.Count; i++)
            {
                while (time - bpmChangeTimes[i] < -0.05) //在那个时间之前都是之前的bpm
                {
                    if (currentBeat > signature) currentBeat = 1;
                    timePerBeat = 1d / (bpmChangeValues[i - 1] / 60d);
                    if (currentBeat == 1)
                        strongBeat.Add(time);
                    else
                        weakBeat.Add(time);
                    currentBeat++;
                    time += timePerBeat;
                }

                time = bpmChangeTimes[i];
                currentBeat = 1;
            }

            foreach (var btime in strongBeat)
            {
                if (btime - currentTime > deltatime) continue;
                var x = ((float)(btime / step) - startindex) * linewidth;
                graphics.DrawLine(pen, x, 0, x, 75);
            }

            foreach (var btime in weakBeat)
            {
                if (btime - currentTime > deltatime) continue;
                var x = ((float)(btime / step) - startindex) * linewidth;
                graphics.DrawLine(pen, x, 0, x, 15);
            }

            //Draw timing lines
            pen = new Pen(Color.White, 1);
            foreach (var note in SimaiProcess.timinglist)
            {
                if (note == null) break;
                if (note.time - currentTime > deltatime) continue;
                var x = ((float)(note.time / step) - startindex) * linewidth;
                graphics.DrawLine(pen, x, 60, x, 75);
            }

            //Draw notes                    
            foreach (var note in SimaiProcess.notelist)
            {
                if (note == null) break;
                if (note.time - currentTime > deltatime) continue;
                var notes = note.getNotes();
                var isEach = notes.Count(o => !o.isSlideNoHead) > 1;

                var x = ((float)(note.time / step) - startindex) * linewidth;

                foreach (var noteD in notes)
                {
                    var y = noteD.startPosition * 6.875f + 8f; //与键位有关

                    if (noteD.isHanabi)
                    {
                        var xDeltaHanabi = (float)(1f / step) * linewidth; //Hanabi is 1s due to frame analyze
                        var rectangleF = new RectangleF(x, 0, xDeltaHanabi, 75);
                        if (noteD.noteType == SimaiNoteType.TouchHold)
                            rectangleF.X += (float)(noteD.holdTime / step) * linewidth;
                        var gradientBrush = new LinearGradientBrush(
                            rectangleF,
                            Color.FromArgb(100, 255, 0, 0),
                            Color.FromArgb(0, 255, 0, 0),
                            LinearGradientMode.Horizontal
                        );
                        graphics.FillRectangle(gradientBrush, rectangleF);
                    }

                    if (noteD.noteType == SimaiNoteType.Tap)
                    {
                        if (noteD.isForceStar)
                        {
                            pen.Width = 3;
                            if (noteD.isBreak)
                                pen.Color = Color.OrangeRed;
                            else if (isEach)
                                pen.Color = Color.Gold;
                            else
                                pen.Color = Color.DeepSkyBlue;
                            Brush brush = new SolidBrush(pen.Color);
                            graphics.DrawString("*", new Font("Consolas", 12, System.Drawing.FontStyle.Bold), brush,
                                new PointF(x - 7f, y - 7f));
                        }
                        else
                        {
                            pen.Width = 2;
                            if (noteD.isBreak)
                                pen.Color = Color.OrangeRed;
                            else if (isEach)
                                pen.Color = Color.Gold;
                            else
                                pen.Color = Color.LightPink;
                            graphics.DrawEllipse(pen, x - 2.5f, y - 2.5f, 5, 5);
                        }
                    }

                    if (noteD.noteType == SimaiNoteType.Touch)
                    {
                        pen.Width = 2;
                        pen.Color = isEach ? Color.Gold : Color.DeepSkyBlue;
                        graphics.DrawRectangle(pen, x - 2.5f, y - 2.5f, 5, 5);
                    }

                    if (noteD.noteType == SimaiNoteType.Hold)
                    {
                        pen.Width = 3;
                        if (noteD.isBreak)
                            pen.Color = Color.OrangeRed;
                        else if (isEach)
                            pen.Color = Color.Gold;
                        else
                            pen.Color = Color.LightPink;

                        var xRight = x + (float)(noteD.holdTime / step) * linewidth;
                        if (xRight - x < 1f) xRight = x + 5;
                        graphics.DrawLine(pen, x, y, xRight, y);
                    }

                    if (noteD.noteType == SimaiNoteType.TouchHold)
                    {
                        pen.Width = 3;
                        var xDelta = (float)(noteD.holdTime / step) * linewidth / 4f;
                        //Console.WriteLine("HoldPixel"+ xDelta);
                        if (xDelta < 1f) xDelta = 1;

                        pen.Color = Color.FromArgb(200, 255, 75, 0);
                        graphics.DrawLine(pen, x, y, x + xDelta * 4f, y);
                        pen.Color = Color.FromArgb(200, 255, 241, 0);
                        graphics.DrawLine(pen, x, y, x + xDelta * 3f, y);
                        pen.Color = Color.FromArgb(200, 2, 165, 89);
                        graphics.DrawLine(pen, x, y, x + xDelta * 2f, y);
                        pen.Color = Color.FromArgb(200, 0, 140, 254);
                        graphics.DrawLine(pen, x, y, x + xDelta, y);
                    }

                    if (noteD.noteType == SimaiNoteType.Slide)
                    {
                        pen.Width = 3;
                        if (!noteD.isSlideNoHead)
                        {
                            if (noteD.isBreak)
                                pen.Color = Color.OrangeRed;
                            else if (isEach)
                                pen.Color = Color.Gold;
                            else
                                pen.Color = Color.DeepSkyBlue;
                            Brush brush = new SolidBrush(pen.Color);
                            graphics.DrawString("*", new Font("Consolas", 12, System.Drawing.FontStyle.Bold), brush,
                                new PointF(x - 7f, y - 7f));
                        }

                        if (noteD.isSlideBreak)
                            pen.Color = Color.OrangeRed;
                        else if (notes.Count(o => o.noteType == SimaiNoteType.Slide) >= 2)
                            pen.Color = Color.Gold;
                        else
                            pen.Color = Color.SkyBlue;
                        pen.DashStyle = DashStyle.Dot;
                        var xSlide = (float)(noteD.slideStartTime / step - startindex) * linewidth;
                        var xSlideRight = (float)(noteD.slideTime / step) * linewidth + xSlide;
                        graphics.DrawLine(pen, xSlide, y, xSlideRight, y);
                        pen.DashStyle = DashStyle.Solid;
                    }
                }
            }

            if (playStartTime - currentTime <= deltatime)
            {
                //Draw play Start time
                pen = new Pen(Color.Red, 5);
                var x1 = (float)(playStartTime / step - startindex) * linewidth;
                PointF[] tranglePoints = { new(x1 - 2, 0), new(x1 + 2, 0), new(x1, 3.46f) };
                graphics.DrawPolygon(pen, tranglePoints);
            }

            if (ghostCusorPositionTime - currentTime <= deltatime)
            {
                //Draw ghost cusor
                pen = new Pen(Color.Orange, 5);
                var x2 = (float)(ghostCusorPositionTime / step - startindex) * linewidth;
                PointF[] tranglePoints2 = { new(x2 - 2, 0), new(x2 + 2, 0), new(x2, 3.46f) };
                graphics.DrawPolygon(pen, tranglePoints2);
            }

            graphics.Flush();
            graphics.Dispose();
            backBitmap.Dispose();

            //MusicWave.Width = waveLevels.Length * zoominPower;
            WaveBitmap.AddDirtyRect(new Int32Rect(0, 0, WaveBitmap.PixelWidth, WaveBitmap.PixelHeight));
            WaveBitmap.Unlock();
            isDrawing = false;
        });
    }

    // This update less frequently. set the time text.
    private void CurrentTimeRefreshTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        UpdateTimeDisplay();
    }

    private void UpdateTimeDisplay()
    {
        var currentPlayTime = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
        var minute = (int)currentPlayTime / 60;
        double second = (int)(currentPlayTime - 60 * minute);
        Dispatcher.Invoke(() => { TimeLabel.Content = string.Format("{0}:{1:00}", minute, second); });
    }

    private void ScrollWave(double delta)
    {
        if (Bass.BASS_ChannelIsActive(bgmStream) == BASSActive.BASS_ACTIVE_PLAYING)
            TogglePause();
        var new_width = TopMenu.ActualWidth > 10 ? (float)TopMenu.ActualWidth + 16 : (float)Width;
        // Console.WriteLine($"ScrollWave - Window.Width: {Width}, TopMenu.ActualWidth: {TopMenu.ActualWidth}");
        delta = delta * deltatime / (new_width / 2);
        var time = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
        SetBgmPosition(time + delta);
        SimaiProcess.ClearNoteListPlayedState();
        SeekTextFromTime();
        Task.Run(() => DrawWave());
    }

    public static string GetLocalizedString(string key, string resourceFileName = "Langs", bool addSpaceAfter = false)
    {

        // Build up the fully-qualified name of the key

        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var fullKey = assemblyName + ":" + resourceFileName + ":" + key;
        var locExtension = new LocExtension(fullKey);
        locExtension.ResolveLocalizedValue(out string? localizedString);

        // Add a space to the end, if requested
        if (addSpaceAfter) localizedString += " ";

        return localizedString ?? key;
    }

    private void TogglePlay(PlayMethod playMethod = PlayMethod.Normal)
    {
        if (Op_Button.IsEnabled == false) return;

        if (lastEditorState == EditorControlMethod.Start || playMethod != PlayMethod.Normal)
            if (!sendRequestStop())
                return;

        FumenContent.Focus();
        SaveFumen();
        if (CheckAndStartView()) return;
        Op_Button.IsEnabled = false;
        isPlaying = true;
        isPlan2Stop = false;

        PlayAndPauseButton.Content = "  ▌▌ ";
        var CusorTime = SimaiProcess.Serialize(GetRawFumenText(), GetRawFumenPosition()); //scan first

        //TODO: Moeying改一下你的generateSoundEffect然后把下面这行删了
        var isOpIncluded = playMethod == PlayMethod.Normal ? false : true;

        var startAt = DateTime.Now;
        switch (playMethod)
        {
            case PlayMethod.Record:
                Bass.BASS_ChannelSetPosition(bgmStream, 0);
                startAt = DateTime.Now.AddSeconds(5d);
                //TODO: i18n
                MessageBox.Show(GetLocalizedString("AskRender"), GetLocalizedString("Attention"));
                InternalSwitchWindow(false);
                generateSoundEffectList(0.0, isOpIncluded);
                var task = new Task(() => renderSoundEffect(5d));
                try
                {
                    task.Start();
                    task.Wait();
                }
                catch (AggregateException)
                {
                    MessageBox.Show(task.Exception!.InnerException!.Message + "\n" +
                                    task.Exception.InnerException.StackTrace);
                    return;
                }

                if (!sendRequestRun(startAt, playMethod)) return;
                break;
            case PlayMethod.Op:
                generateSoundEffectList(0.0, isOpIncluded);
                InternalSwitchWindow(false);
                Bass.BASS_ChannelSetPosition(bgmStream, 0);
                startAt = DateTime.Now.AddSeconds(5d);
                Bass.BASS_ChannelPlay(trackStartStream, true);
                Task.Run(() =>
                {
                    if (!sendRequestRun(startAt, playMethod)) return;
                    while (DateTime.Now.Ticks < startAt.Ticks)
                        if (lastEditorState != EditorControlMethod.Start)
                            return;
                    Dispatcher.Invoke(() =>
                    {
                        playStartTime =
                            Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
                        SimaiProcess.ClearNoteListPlayedState();
                        StartSELoop();
                        //soundEffectTimer.Start();
                        waveStopMonitorTimer.Start();
                        visualEffectRefreshTimer.Start();
                        Bass.BASS_ChannelPlay(bgmStream, false);
                    });
                });
                break;
            case PlayMethod.Normal:
                playStartTime = Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
                generateSoundEffectList(playStartTime, isOpIncluded);
                SimaiProcess.ClearNoteListPlayedState();
                StartSELoop();
                //soundEffectTimer.Start();
                waveStopMonitorTimer.Start();
                visualEffectRefreshTimer.Start();
                startAt = DateTime.Now;
                Bass.BASS_ChannelPlay(bgmStream, false);
                Task.Run(() =>
                {
                    if (lastEditorState == EditorControlMethod.Pause)
                    {
                        if (!sendRequestContinue(startAt)) return;
                    }
                    else
                    {
                        if (!sendRequestRun(startAt, playMethod)) return;
                    }
                });
                break;
        }

        ghostCusorPositionTime = (float)CusorTime;
        DrawWave();
    }

    public void TogglePause()
    {
        Op_Button.IsEnabled = true;
        isPlaying = false;
        isPlan2Stop = false;

        FumenContent.Focus();
        PlayAndPauseButton.Content = "▶";
        Bass.BASS_ChannelStop(bgmStream);
        Bass.BASS_ChannelStop(holdRiserStream);
        //soundEffectTimer.Stop();
        waveStopMonitorTimer.Stop();
        visualEffectRefreshTimer.Stop();
        sendRequestPause();
        DrawWave();
    }

    private void ToggleStop()
    {
        Op_Button.IsEnabled = true;
        isPlaying = false;
        isPlan2Stop = false;

        FumenContent.Focus();
        PlayAndPauseButton.Content = "▶";
        Bass.BASS_ChannelStop(bgmStream);
        Bass.BASS_ChannelStop(holdRiserStream);
        //soundEffectTimer.Stop();
        waveStopMonitorTimer.Stop();
        visualEffectRefreshTimer.Stop();
        sendRequestStop();
        Bass.BASS_ChannelSetPosition(bgmStream, playStartTime);
        DrawWave();
    }

    private void TogglePlayAndPause(PlayMethod playMethod = PlayMethod.Normal)
    {
        if (isPlaying)
            TogglePause();
        else
            TogglePlay(playMethod);
    }

    private void TogglePlayAndStop(PlayMethod playMethod = PlayMethod.Normal)
    {
        if (isPlaying)
            ToggleStop();
        else
            TogglePlay(playMethod);
    }

    private void SetPlaybackSpeed(float speed)
    {
        var scale = (speed - 1) * 100f;
        Bass.BASS_ChannelSetAttribute(bgmStream, BASSAttribute.BASS_ATTRIB_TEMPO, scale);
    }

    private float GetPlaybackSpeed()
    {
        var speed = 0f;
        Bass.BASS_ChannelGetAttribute(bgmStream, BASSAttribute.BASS_ATTRIB_TEMPO, ref speed);
        return speed / 100f + 1f;
    }

    private void SetBgmPosition(double time)
    {
        if (lastEditorState == EditorControlMethod.Pause) sendRequestStop();
        Bass.BASS_ChannelSetPosition(bgmStream, time);

        // Broadcast position to App in embed mode
        var payload = new { control = 273, position = time };
        var json = JsonConvert.SerializeObject(payload);
        BroadcastToApp(json);
    }


    //*VIEW COMMUNICATION
    
    /// <summary>
    /// Broadcast message to App (port 8014) in embed mode
    /// </summary>
    private void BroadcastToApp(string json)
    {
        if (!embed_mode) return;
        
        Task.Run(() =>
        {
            try
            {
                using (var udpClient = new System.Net.Sockets.UdpClient())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 8014);
                    udpClient.Send(bytes, bytes.Length, endpoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MajdataEdit] Failed to send to App: {ex.Message}");
            }
        });
    }
    
    private bool sendRequestStop(bool silentOnFailure = false)
    {
        var requestStop = new EditRequestjson
        {
            control = EditorControlMethod.Stop
        };
        var json = JsonConvert.SerializeObject(requestStop);
        
        // Broadcast to App in embed mode
        BroadcastToApp(json);
        
        var response = WebControl.RequestPOST("http://localhost:8013/", json);
        if (response == "ERROR")
        {
            // 换谱面/自动加载场景下 MajdataView 可能未在监听，静默忽略，避免干扰用户
            if (!silentOnFailure)
                MessageBox.Show(GetLocalizedString("PortClear"));
            return false;
        }

        lastEditorState = EditorControlMethod.Stop;
        return true;
    }

    private bool sendRequestPause()
    {
        var requestStop = new EditRequestjson
        {
            control = EditorControlMethod.Pause,
            appPort = embed_mode ? 8014 : -1 // 将app端口告诉majdataview
        };
        var json = JsonConvert.SerializeObject(requestStop);
        
        var response = WebControl.RequestPOST("http://localhost:8013/", json);
        if (response == "ERROR")
        {
            MessageBox.Show(GetLocalizedString("PortClear"));
            return false;
        }

        lastEditorState = EditorControlMethod.Pause;
        return true;
    }

    private bool sendRequestContinue(DateTime StartAt)
    {
        var request = new EditRequestjson
        {
            control = EditorControlMethod.Continue,
            startAt = StartAt.Ticks,
            startTime = (float)Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream)),
            audioSpeed = GetPlaybackSpeed()
        };
        var json = JsonConvert.SerializeObject(request);
        
        // Broadcast to App in embed mode
        BroadcastToApp(json);
        
        var response = WebControl.RequestPOST("http://localhost:8013/", json);
        if (response == "ERROR")
        {
            MessageBox.Show(GetLocalizedString("PortClear"));
            return false;
        }

        lastEditorState = EditorControlMethod.Start;
        return true;
    }

    private bool sendRequestRun(DateTime StartAt, PlayMethod playMethod)
    {
        var jsonStruct = new Majson();
        foreach (var note in SimaiProcess.notelist)
        {
            note.noteList = note.getNotes();
            jsonStruct.timingList.Add(note);
        }

        jsonStruct.title = SimaiProcess.title!;
        jsonStruct.artist = SimaiProcess.artist!;
        jsonStruct.level = SimaiProcess.levels[selectedDifficulty];
        jsonStruct.designer = SimaiProcess.designer!;
        jsonStruct.difficulty = SimaiProcess.GetDifficultyText(selectedDifficulty);
        jsonStruct.diffNum = selectedDifficulty;

        var json = JsonConvert.SerializeObject(jsonStruct);
        var path = Path.Combine(AppContext.BaseDirectory, "majdata.json");
        File.WriteAllText(path, json);

        var request = new EditRequestjson();
        if (playMethod == PlayMethod.Op)
            request.control = EditorControlMethod.OpStart;
        else if (playMethod == PlayMethod.Normal)
            request.control = EditorControlMethod.Start;
        else
            request.control = EditorControlMethod.Record;

        Dispatcher.Invoke(() =>
        {
            request.jsonPath = path;
            request.maidataPath = maidataDir;
            request.startAt = StartAt.Ticks;
            request.startTime =
                (float)Bass.BASS_ChannelBytes2Seconds(bgmStream, Bass.BASS_ChannelGetPosition(bgmStream));
            // request.playSpeed = float.Parse(ViewerSpeed.Text);
            // 将maimaiDX速度换算为View中的单位速度 MajSpeed = 107.25 / (71.4184491 * (MaiSpeed + 0.9975) ^ -0.985558604)
            request.noteSpeed = editorSetting!.playSpeed;
            request.touchSpeed = editorSetting!.touchSpeed;
            request.backgroundCover = editorSetting!.backgroundCover;
            request.comboStatusType = editorSetting!.comboStatusType;
            request.audioSpeed = GetPlaybackSpeed();
            request.smoothSlideAnime = editorSetting!.SmoothSlideAnime;
            request.moviePath = currentMovieFilename != null ? Path.Combine(maidataDir, currentMovieFilename) : null;
        });

        json = JsonConvert.SerializeObject(request);
        
        // Broadcast to App in embed mode
        BroadcastToApp(json);
        
        var response = WebControl.RequestPOST("http://localhost:8013/", json);
        if (response == "ERROR")
        {
            MessageBox.Show(GetLocalizedString("PortClear"));
            return false;
        }

        lastEditorState = EditorControlMethod.Start;
        return true;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", EntryPoint = "MoveWindow")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    private bool CheckAndStartView()
    {
        // 在 embed_mode 不启动 MajdataView
        if (MainWindow.embed_mode) return false;

        if (Process.GetProcessesByName("MajdataView").Length == 0 && Process.GetProcessesByName("Unity").Length == 0)
        {
            var viewProcess = Process.Start("MajdataView.exe");
            var setWindowPosTimer = new Timer(2000)
            {
                AutoReset = false
            };
            setWindowPosTimer.Elapsed += SetWindowPosTimer_Elapsed;
            setWindowPosTimer.Start();
            return true;
        }

        return false;
    }

    private string GetViewerWorkingDirectory()
    {
        return Environment.CurrentDirectory + "/MajdataView_Data/StreamingAssets";
        /*string tempPath = "";
        Process baseProc;
        Process[] viewProcs;
        viewProcs = Process.GetProcessesByName("MajdataView");
        // Prioritize Majdata First
        if (viewProcs.Length > 0)
        {
            baseProc = viewProcs.First();
            string pwd;
            pwd = baseProc.StartInfo.WorkingDirectory.TrimEnd('/');
            if (pwd.Length == 0) pwd = ".";
            tempPath = pwd + "/MajdataView_Data/StreamingAssets";
        }
        else
        {
            viewProcs = Process.GetProcessesByName("Unity");
        }
        if (viewProcs.Length <= 0)
            throw new Exception("Unable to find MajdataView instance!");

        return (tempPath.Length == 0) ?
            Environment.CurrentDirectory + "/SFX" :
            tempPath;*/
    }

    private void InternalSwitchWindow(bool moveToPlace = true)
    {
        var windowPtr = FindWindow(null, "MajdataView");
        //var thisWindow = FindWindow(null, this.Title);
        ShowWindow(windowPtr, 5); //还原窗口
        SwitchToThisWindow(windowPtr, true);
        //SwitchToThisWindow(thisWindow, true);
        if (moveToPlace) InternalMoveWindow();
    }

    private void InternalMoveWindow()
    {
        var windowPtr = FindWindow(null, "MajdataView");
        var source = PresentationSource.FromVisual(this);

        double dpiX = 1, dpiY = 1;
        if (source != null)
        {
            dpiX = 96.0 * source.CompositionTarget.TransformToDevice.M11;
            dpiY = 96.0 * source.CompositionTarget.TransformToDevice.M22;
        }

        //Console.WriteLine(dpiX+" "+dpiY);
        dpiX /= 96d;
        dpiY /= 96d;

        var Height = this.Height * dpiY;
        var Left = this.Left * dpiX;
        var Top = this.Top * dpiY;
        MoveWindow(windowPtr,
            (int)(Left - Height + 20),
            (int)Top,
            (int)Height - 20,
            (int)Height, true);
    }

    private void SetWindowGoldenPosition()
    {
        // 属于你的独享黄金位置
        var ScreenWidth = SystemParameters.PrimaryScreenWidth;
        var ScreenHeight = SystemParameters.PrimaryScreenHeight;
        var new_width = TopMenu.ActualWidth > 10 ? (float)TopMenu.ActualWidth + 16 : (float)Width;

        Left = (ScreenWidth - new_width + Height) / 2 - 10;
        Top = (ScreenHeight - Height) / 2;
    }

    private void SwitchFumenOverwriteMode()
    {
        fumenOverwriteMode = !fumenOverwriteMode;
        FumenContent.TextArea.OverstrikeMode = fumenOverwriteMode;
        OverrideModeTipsPopup.Visibility = fumenOverwriteMode ? Visibility.Visible : Visibility.Collapsed;
    }

    public string GetWindowsTitleString()
    {
        return $"MajdataEdit ({MAJDATA_VERSION_STRING})";
    }

    public string GetWindowsTitleString(string info)
    {
        // Discord RPC disabled to prevent connection timeout errors
        // try
        // {
        //     var details = "Editing: " + SimaiProcess.title;
        //     if (details.Length > 50)
        //         details = details[..50];
        //     DCRPCclient.SetPresence(new RichPresence
        //     {
        //         Details = details,
        //         State = "With note count of " + SimaiProcess.notelist.Count,
        //         Assets = new Assets
        //         {
        //             LargeImageKey = "salt",
        //             LargeImageText = "Majdata",
        //             SmallImageKey = "None"
        //         }
        //     });
        // }
        // catch
        // {
        // }

        return GetWindowsTitleString() + " - " + info;
    }

    public void OpenFile(string path)
    {
        initFromFile(path);
    }


    //*PLAY CONTROL

    private enum PlayMethod
    {
        Normal,
        Op,
        Record
    }
}