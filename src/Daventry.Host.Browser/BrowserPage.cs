using System.Runtime.Versioning;

namespace Daventry.Host.Browser;

/// <summary>Small things a page around the player needs from the browser.</summary>
[SupportedOSPlatform("browser")]
public static class BrowserPage
{
    /// <summary>Offers bytes to the user as a downloaded file, such as a saved game to keep.</summary>
    public static void Download(string name, byte[] bytes) => Interop.Download(name, bytes);

    /// <summary>The browser's confirmation dialog: true when the user chose OK.</summary>
    public static bool Confirm(string text) => Interop.Confirm(text);
}
