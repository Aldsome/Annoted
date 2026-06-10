using Microsoft.Win32;

namespace Annoted.Wpf;

/// <summary>Reads the current Windows app theme so first-run defaults match the OS.</summary>
public static class SystemTheme
{
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 = light, 0 = dark. Missing → assume light.
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
