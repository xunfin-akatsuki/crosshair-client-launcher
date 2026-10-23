using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CCL.App.Interop;

public enum BackdropKind
{
    None,
    Mica,
    Acrylic
}

/// <summary>
/// 为窗口启用系统级背景材质。Win11 22H2 起用 DWM 的 SYSTEMBACKDROP_TYPE（云母/亚克力），
/// Win10 1809 起退回到 SetWindowCompositionAttribute 的亚克力模糊；都不可用时返回 None，
/// 由调用方保留不透明渐变底，避免出现透明窗口却没有任何模糊的破图。
/// </summary>
internal static class Backdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwcpRound = 2;

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;

    public static BackdropKind Enable(Window window)
    {
        // 诊断用：CCL_NO_BACKDROP=1 强制走不透明渐变底，便于截图或排查材质异常
        if (Environment.GetEnvironmentVariable("CCL_NO_BACKDROP") == "1") return BackdropKind.None;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return BackdropKind.None;

        TrySetDarkMode(hwnd);

        var corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var build = Environment.OSVersion.Version.Build;

        // 3 = 亚克力（玻璃感更强），2 = 云母（更素）。优先亚克力。
        if (build >= 22000)
        {
            var acrylic = DwmsbtTransientWindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref acrylic, sizeof(int)) == 0)
            {
                ApplyTransparent(window);
                return BackdropKind.Acrylic;
            }

            var mica = DwmsbtMainWindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref mica, sizeof(int)) == 0)
            {
                ApplyTransparent(window);
                return BackdropKind.Mica;
            }
        }

        if (build >= 17763 && TryEnableLegacyAcrylic(hwnd))
        {
            ApplyTransparent(window);
            return BackdropKind.Acrylic;
        }

        return BackdropKind.None;
    }

    /// <remarks>
    /// 必须走 WindowChrome 而不是 AllowsTransparency，后者会关掉 DWM 材质并失去硬件加速。
    /// </remarks>
    private static void ApplyTransparent(Window window)
    {
        window.Background = System.Windows.Media.Brushes.Transparent;
    }

    private static void TrySetDarkMode(IntPtr hwnd)
    {
        var enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) == 0) return;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
    }

    private static bool TryEnableLegacyAcrylic(IntPtr hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            GradientColor = unchecked((int)0x99140E0B), // AABBGGRR，偏暖的深色薄雾
            AccentFlags = 2
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
