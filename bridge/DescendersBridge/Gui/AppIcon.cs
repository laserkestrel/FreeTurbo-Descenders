using System.Drawing;

namespace DescendersBridge.Gui;

/// <summary>
/// The bridge's own icon (Assets\bridge.ico, every size from 16 to 256 px) for the title bar and the taskbar. Without it WinForms shows
/// its stock icon. Read from the copy embedded in the assembly, not from the exe's icon resource: that way Windows gets the real 16 px
/// drawing for the title bar instead of a 32 px one scaled down.
/// </summary>
internal static class AppIcon
{
    public const string ResourceName = "bridge.ico";

    /// <summary>A new icon each call (the form that takes it disposes it); null when the resource is missing, the window then keeps the stock icon.</summary>
    public static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
        return stream == null ? null : new Icon(stream);
    }
}
