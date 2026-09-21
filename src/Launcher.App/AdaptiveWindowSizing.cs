using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Launcher.App;

internal static class AdaptiveWindowSizing
{
    private const int WindowMessageGetMinMaxInfo = 0x0024;
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private const uint MonitorDefaultToNearest = 2;

    public static void DisableRoundedCorners(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var preference = DwmWindowCornerPreferenceDoNotRound;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowAttributeCornerPreference,
            ref preference,
            (uint)Marshal.SizeOf<int>());
    }

    public static void AttachMaximizeWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowProcedure);
    }

    public static void FitMainWindow(Window window)
    {
        var workArea = GetWorkAreaInDips(window);
        var width = Math.Min(1360, workArea.Width * 0.96);
        var height = Math.Min(820, workArea.Height * 0.94);

        window.MinWidth = Math.Min(840, width);
        window.MinHeight = Math.Min(520, height);
        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);
        CenterInWorkArea(window, workArea);
    }

    public static void FitDialog(
        Window window,
        double desiredWidth,
        double desiredHeight,
        double minimumWidth,
        double minimumHeight)
    {
        var workArea = GetWorkAreaInDips(window);
        var width = Math.Min(desiredWidth, workArea.Width * 0.94);
        var height = Math.Min(desiredHeight, workArea.Height * 0.9);

        window.MinWidth = Math.Min(minimumWidth, width);
        window.MinHeight = Math.Min(minimumHeight, height);
        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);
        window.MaxHeight = workArea.Height * 0.94;
        CenterInWorkArea(window, workArea);
    }

    public static void ClampToCurrentWorkArea(Window window)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var workArea = GetWorkAreaInDips(window);
        var maximumWidth = workArea.Width * 0.96;
        var maximumHeight = workArea.Height * 0.94;

        window.MinWidth = Math.Min(window.MinWidth, maximumWidth);
        window.MinHeight = Math.Min(window.MinHeight, maximumHeight);
        window.Width = Math.Min(window.ActualWidth > 0 ? window.ActualWidth : window.Width, maximumWidth);
        window.Height = Math.Min(window.ActualHeight > 0 ? window.ActualHeight : window.Height, maximumHeight);
        window.Left = Math.Clamp(
            window.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(
            window.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }

    private static Rect GetWorkAreaInDips(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(
            monitorInfo.Work.Left / dpi.DpiScaleX,
            monitorInfo.Work.Top / dpi.DpiScaleY,
            (monitorInfo.Work.Right - monitorInfo.Work.Left) / dpi.DpiScaleX,
            (monitorInfo.Work.Bottom - monitorInfo.Work.Top) / dpi.DpiScaleY);
    }

    private static void CenterInWorkArea(Window window, Rect workArea)
    {
        window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2);
        window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2);
    }

    private static nint WindowProcedure(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WindowMessageGetMinMaxInfo || lParam == nint.Zero)
        {
            return nint.Zero;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return nint.Zero;
        }

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        bounds.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        bounds.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
        bounds.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        bounds.MaxTrackSize = bounds.MaxSize;
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return nint.Zero;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        uint attributeSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
