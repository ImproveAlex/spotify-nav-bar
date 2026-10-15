using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SpotifyBar;

public sealed record DisplayMonitor(string Id, string Label, int Left, int Top, int Right, int Bottom, bool Primary);

public sealed class DesktopDock : IDisposable
{
    public const int PixelHeight = 120;
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly uint _message = RegisterWindowMessage("SpotifyBar.AppBar");
    private bool _registered, _positioning, _disposed;
    private bool _reserve;
    private string _monitor = "";
    public DisplayMonitor CurrentMonitor { get; private set; }
    public event Action? PositionChanged;
    public double DpiScale => Math.Max(1, GetDpiForWindow(_handle)) / 96.0;

    public DesktopDock(Window window)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)!;
        _source.AddHook(WindowProcedure);
        CurrentMonitor = GetMonitors().First();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    public static List<DisplayMonitor> GetMonitors()
    {
        var result = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rectangle, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new(info.Device, $"{(info.Flags == 1 ? "Main display" : "Display " + (result.Count + 1))} · {info.Monitor.Right - info.Monitor.Left} × {info.Monitor.Bottom - info.Monitor.Top}",
                    info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom, info.Flags == 1));
            return true;
        }, IntPtr.Zero);
        return result.OrderByDescending(m => m.Primary).ToList();
    }

    public void Configure(string monitor, bool reserve)
    {
        _monitor = monitor;
        _reserve = reserve;
        Reposition();
    }

    private void Reposition()
    {
        if (_positioning || _disposed) return;
        _positioning = true;
        try
        {
            var monitors = GetMonitors();
            CurrentMonitor = monitors.FirstOrDefault(m => m.Id == _monitor) ?? monitors.First();
            var m = CurrentMonitor;
            var data = Data();
            if (_reserve && !_registered) _registered = SHAppBarMessage(0, ref data) != UIntPtr.Zero;
            if (!_reserve && _registered) { SHAppBarMessage(1, ref data); _registered = false; }
            data.Rectangle = new Rect { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Top + PixelHeight };
            if (_registered)
            {
                SHAppBarMessage(2, ref data);
                data.Rectangle.Bottom = data.Rectangle.Top + PixelHeight;
                SHAppBarMessage(3, ref data);
            }
            SetWindowPos(_handle, new IntPtr(-1), data.Rectangle.Left, data.Rectangle.Top,
                data.Rectangle.Right - data.Rectangle.Left, PixelHeight, 0x0010 | 0x0200);
            PositionChanged?.Invoke();
        }
        finally { _positioning = false; }
    }

    private AppBarData Data() => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Handle = _handle, Callback = _message, Edge = 1 };
    private void DisplayChanged(object? sender, EventArgs e) => _window.Dispatcher.BeginInvoke(() => Reposition());
    private IntPtr WindowProcedure(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _message)
        {
            if (wParam.ToInt32() == 1 && !_positioning) _window.Dispatcher.BeginInvoke(() => Reposition());
            if (wParam.ToInt32() == 2)
                SetWindowPos(_handle, lParam == IntPtr.Zero ? new IntPtr(-1) : new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        if (msg == 0x02E0) // WM_DPICHANGED: preserve physical height instead of WPF's logical size.
        {
            handled = true;
            _window.Dispatcher.BeginInvoke(() => Reposition());
        }
        return IntPtr.Zero;
    }

    public (int Width, int Height) GetPhysicalSize()
    {
        GetWindowRect(_handle, out var rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    public bool IsTopmost => (GetWindowLong(_handle, -20) & 8) != 0;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        _source.RemoveHook(WindowProcedure);
        if (_registered) { var data = Data(); SHAppBarMessage(1, ref data); _registered = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData { public uint Size; public IntPtr Handle; public uint Callback, Edge; public Rect Rectangle; public IntPtr Param; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect rectangle, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string text);
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
}
