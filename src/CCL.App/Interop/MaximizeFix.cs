using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CCL.App.Interop;

/// <summary>
/// 修正 WindowStyle=None 窗口的最大化行为。默认实现会按“显示器物理尺寸 + 边框补偿”最大化，
/// 结果尺寸超出工作区并盖住任务栏，位置还会出现 -7 这类负偏移。
/// 这里拦截 WM_GETMINMAXINFO，把最大化矩形钉到当前显示器的工作区。
/// </summary>
internal static class MaximizeFix
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        source.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return IntPtr.Zero;

        var work = info.Work;
        var screen = info.Monitor;
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        minMax.MaxPosition.X = work.Left - screen.Left;
        minMax.MaxPosition.Y = work.Top - screen.Top;
        minMax.MaxSize.X = work.Right - work.Left;
        minMax.MaxSize.Y = work.Bottom - work.Top;
        minMax.MaxTrackSize = minMax.MaxSize;

        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
