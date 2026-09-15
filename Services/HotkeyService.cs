using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace SmartScreenshotManager.Services
{
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 1001;

        private const uint WmHotkey = 0x0312;

        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;

        private static readonly UIntPtr SubclassId =
            new UIntPtr(1001);

        private readonly IntPtr _windowHandle;

        private readonly SubclassProc _subclassProc;

        private bool _isSubclassInstalled;
        private bool _isHotkeyRegistered;

        public event Action? HotkeyPressed;

        public HotkeyService(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;

            _subclassProc =
                WindowSubclassProc;

            InstallWindowSubclass();
        }

        public bool RegisterHotkey(
            VirtualKey key,
            bool ctrl,
            bool alt,
            bool shift,
            bool win)
        {
            UnregisterHotkey();

            uint modifiers =
                ModNoRepeat;

            if (ctrl)
            {
                modifiers |= ModControl;
            }

            if (alt)
            {
                modifiers |= ModAlt;
            }

            if (shift)
            {
                modifiers |= ModShift;
            }

            if (win)
            {
                modifiers |= ModWin;
            }

            bool result =
                RegisterHotKey(
                    _windowHandle,
                    HotkeyId,
                    modifiers,
                    (uint)key);

            _isHotkeyRegistered =
                result;

            return result;
        }

        public void UnregisterHotkey()
        {
            if (!_isHotkeyRegistered)
                return;

            UnregisterHotKey(
                _windowHandle,
                HotkeyId);

            _isHotkeyRegistered =
                false;
        }

        private void InstallWindowSubclass()
        {
            if (_isSubclassInstalled)
                return;

            bool result =
                SetWindowSubclass(
                    _windowHandle,
                    _subclassProc,
                    SubclassId,
                    UIntPtr.Zero);

            _isSubclassInstalled =
                result;
        }

        private IntPtr WindowSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData)
        {
            if (uMsg == WmHotkey &&
                wParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();

                return IntPtr.Zero;
            }

            return DefSubclassProc(
                hWnd,
                uMsg,
                wParam,
                lParam);
        }

        public void Dispose()
        {
            UnregisterHotkey();

            if (_isSubclassInstalled)
            {
                RemoveWindowSubclass(
                    _windowHandle,
                    _subclassProc,
                    SubclassId);

                _isSubclassInstalled =
                    false;
            }
        }

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(
            IntPtr hWnd,
            int id);

        [DllImport(
            "comctl32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData);

        [DllImport(
            "comctl32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam);
    }
}