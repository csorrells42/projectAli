using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ali.App.Wpf;

internal static class NativeTitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    public static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkMode,
                    ref enabled,
                    Marshal.SizeOf<int>()) != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeLegacy,
                    ref enabled,
                    Marshal.SizeOf<int>());
            }
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
