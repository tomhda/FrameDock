using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FrameDock.Localization;

// UI wording backed by Strings/<lang>/Resources.resw (PRI). The loader is
// created once so normal launch and playback pay no per-string cost.
internal static class Strings
{
    private static readonly ResourceLoader Loader = new();

    internal static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    internal static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
