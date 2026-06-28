using System.Collections.Generic;
using System.Windows;
using WPFLocalizeExtension.Extensions;

namespace MajdataEdit;

public partial class ShortcutHelp : Window
{
    public ShortcutHelp()
    {
        InitializeComponent();
        ShortcutList.ItemsSource = new List<ShortcutItem>
        {
            Make("SK_PlayPause", "Ctrl + Shift + C"),
            Make("SK_StopPlaying", "Ctrl + Shift + X"),
            Make("SK_SaveFile", "Ctrl + S"),
            Make("SK_SendToView", "Ctrl + Shift + V"),
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
