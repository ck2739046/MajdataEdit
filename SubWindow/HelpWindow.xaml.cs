using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using WPFLocalizeExtension.Extensions;

namespace MajdataEdit;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        ShortcutList.ItemsSource = new List<ShortcutItem>
        {
            Make("SK_PlayPause", "Ctrl + Shift + C"),
            Make("SK_StopPlaying", "Ctrl + Shift + X"),
            Make("SK_SaveFile", "Ctrl + S"),
            Make("SK_SendToView", "Ctrl + Shift + A"),
            Make("SK_IncreaseSpeed", "Ctrl + P"),
            Make("SK_DecreaseSpeed", "Ctrl + O"),
            Make("SK_Find", "Ctrl + F"),
            Make("SK_MirrorLR", "Ctrl + J"),
            Make("SK_MirrorUD", "Ctrl + K"),
            Make("SK_Mirror180", "Ctrl + L"),
            Make("SK_Mirror45", "Ctrl + ;"),
            Make("SK_MirrorCcw45", "Ctrl + '"),
            Make("SK_CtrlArrow", "Ctrl + ↑/↓/←/→"),
            Make("SK_CtrlClick", "SK_CtrlClick_Key", true),
            Make("SK_FontSize", "Ctrl + +/-"),
        };
        FillAboutInfo();
    }

    /// <summary>
    /// 反射读取程序集版本与编译时间戳(嵌入在 AssemblyCopyright 内)填充「关于」面板。
    /// </summary>
    private void FillAboutInfo()
    {
        var asm = Assembly.GetExecutingAssembly();
        var product = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        AppNameText.Text = product ?? asm.GetName().Name ?? string.Empty;

        var authors = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        AuthorText.Text = authors ?? string.Empty;

        var ver = asm.GetName().Version;
        VersionText.Text = ver != null ? $"v{ver.ToString(3)}" : string.Empty;

        var copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        BuildDateText.Text = ExtractBuildDate(copyright) ?? "-";

        var repositoryUrl = Attribute.GetCustomAttributes(asm, typeof(AssemblyMetadataAttribute))
            .OfType<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;
        if (!string.IsNullOrWhiteSpace(repositoryUrl))
            GithubLink.Text = repositoryUrl;
    }

    /// <summary>
    /// 从 Copyright 字符串中解析构建时间戳。
    /// </summary>
    private static string? ExtractBuildDate(string? copyright)
    {
        if (string.IsNullOrWhiteSpace(copyright))
            return null;
        // 形如 2026.06.28_14:30:00_UTC+08:00
        var match = Regex.Match(copyright,
            @"(\d{4}\.\d{2}\.\d{2}_\d{2}:\d{2}:\d{2}_UTC[+\-]\d{2}:\d{2})");
        if (!match.Success)
            return copyright;
        // 将 2026.06.28_14:30:00_UTC+08:00 美化为 2026.06.28 14:30:00 (UTC+08:00)
        var raw = match.Groups[1].Value;
        var parts = raw.Split('_');
        if (parts.Length == 3)
            return $"{parts[0]} {parts[1]} ({parts[2]})";
        return raw;
    }

    private void GithubLink_MouseDown(object sender, MouseButtonEventArgs e)
    {
        MainWindow.OpenGitHub();
    }

    private static ShortcutItem Make(string locKey, string key, bool localizeKey = false)
    {
        var loc = new LocExtension(locKey);
        loc.ResolveLocalizedValue(out string func);
        var displayKey = key;
        if (localizeKey)
        {
            var keyLoc = new LocExtension(key);
            keyLoc.ResolveLocalizedValue(out displayKey);
        }
        return new ShortcutItem(func ?? locKey, displayKey ?? key);
    }
}

public record ShortcutItem(string Function, string Key);
