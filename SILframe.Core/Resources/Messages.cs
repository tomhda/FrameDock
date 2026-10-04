using System.Globalization;
using System.Resources;

namespace SILframe.Core;

// User-facing Core messages. The neutral resx holds English; Messages.ja.resx
// holds the original Japanese. Selection follows CurrentUICulture.
internal static class Messages
{
    private static readonly ResourceManager Manager = new("SILframe.Core.Messages", typeof(Messages).Assembly);

    internal static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    internal static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
