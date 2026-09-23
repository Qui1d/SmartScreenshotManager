using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SmartScreenshotManager.Services
{
    public sealed class TrayService : IDisposable
    {
        private const uint CallbackMessage = 0x8000 + 42;
        private readonly IntPtr _hwnd;
        private readonly SubclassProc _proc;
        private readonly uint _taskbarCreated;
        private IntPtr _ownedIcon;
        private bool _installed;
        private bool _disposed;
        private NotifyIconData _data;
        public event Action? OpenRequested;
        public event Action? ExitRequested;

        public TrayService(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _proc = WindowProc;
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _ownedIcon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
            _data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 42,
                Flags = 1 | 2 | 4, Callback = CallbackMessage,
                Icon = _ownedIcon != IntPtr.Zero ? _ownedIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512)),
                Tip = "Smart Screenshot Manager", Info = "", InfoTitle = ""
            };
            _installed = SetWindowSubclass(hwnd, _proc, new UIntPtr(1042), UIntPtr.Zero);
        }

        public bool EnsureVisible()
        {
            if (_disposed || !_installed) return false;
            // Modify succeeds for an existing icon; add recreates it after Explorer restarts.
            return Shell_NotifyIcon(1, ref _data) || Shell_NotifyIcon(0, ref _data);
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (message == _taskbarCreated) EnsureVisible();
            if (message == CallbackMessage)
            {
                uint mouseMessage = unchecked((uint)lParam.ToInt64());
                if (mouseMessage == 0x0202 || mouseMessage == 0x0203) OpenRequested?.Invoke();
                if (mouseMessage == 0x0205 || mouseMessage == 0x007B) ShowMenu();
                return IntPtr.Zero;
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private void ShowMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            uint selected;
            try
            {
                AppendMenu(menu, 0, new UIntPtr(1), "Open");
                AppendMenu(menu, 0x800, UIntPtr.Zero, "");
                AppendMenu(menu, 0, new UIntPtr(2), "Exit");
                GetCursorPos(out Point point);
                SetForegroundWindow(_hwnd);
                selected = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { DestroyMenu(menu); }
            if (selected == 1) OpenRequested?.Invoke();
            if (selected == 2) ExitRequested?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Shell_NotifyIcon(2, ref _data);
            if (_installed) RemoveWindowSubclass(_hwnd, _proc, new UIntPtr(1042));
            _installed = false;
            if (_ownedIcon != IntPtr.Zero) { DestroyIcon(_ownedIcon); _ownedIcon = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size; public IntPtr Window; public uint Id; public uint Flags;
            public uint Callback; public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State; public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
        private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rectangle);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
