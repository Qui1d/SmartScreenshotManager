using System.Collections.Generic;
using Windows.Storage;
using Windows.System;

namespace SmartScreenshotManager.Services
{
    public class SettingsService
    {
        private const string ScreenshotFolderKey = "ScreenshotFolder";

        private const string HotkeyCtrlKey = "HotkeyCtrl";
        private const string HotkeyAltKey = "HotkeyAlt";
        private const string HotkeyShiftKey = "HotkeyShift";
        private const string HotkeyWinKey = "HotkeyWin";
        private const string HotkeyVirtualKeyKey = "HotkeyVirtualKey";

        private readonly ApplicationDataContainer _settings;

        public SettingsService()
        {
            _settings = ApplicationData.Current.LocalSettings;
        }

        public string? ScreenshotFolder
        {
            get
            {
                return _settings.Values[ScreenshotFolderKey] as string;
            }

            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _settings.Values.Remove(ScreenshotFolderKey);
                    return;
                }

                _settings.Values[ScreenshotFolderKey] = value;
            }
        }

        public bool HotkeyCtrl
        {
            get
            {
                return GetBool(HotkeyCtrlKey, false);
            }

            set
            {
                _settings.Values[HotkeyCtrlKey] = value;
            }
        }

        public bool HotkeyAlt
        {
            get
            {
                return GetBool(HotkeyAltKey, true);
            }

            set
            {
                _settings.Values[HotkeyAltKey] = value;
            }
        }

        public bool HotkeyShift
        {
            get
            {
                return GetBool(HotkeyShiftKey, false);
            }

            set
            {
                _settings.Values[HotkeyShiftKey] = value;
            }
        }

        public bool HotkeyWin
        {
            get
            {
                return GetBool(HotkeyWinKey, false);
            }

            set
            {
                _settings.Values[HotkeyWinKey] = value;
            }
        }

        public VirtualKey HotkeyVirtualKey
        {
            get
            {
                if (_settings.Values[HotkeyVirtualKeyKey] is int value)
                {
                    return (VirtualKey)value;
                }

                return VirtualKey.S;
            }

            set
            {
                _settings.Values[HotkeyVirtualKeyKey] = (int)value;
            }
        }

        public void SetHotkey(
            VirtualKey key,
            bool ctrl,
            bool alt,
            bool shift,
            bool win)
        {
            HotkeyVirtualKey = key;
            HotkeyCtrl = ctrl;
            HotkeyAlt = alt;
            HotkeyShift = shift;
            HotkeyWin = win;
        }

        public void ResetHotkey()
        {
            SetHotkey(
                VirtualKey.S,
                ctrl: false,
                alt: true,
                shift: false,
                win: false);
        }

        public string GetHotkeyDisplayText()
        {
            var parts = new List<string>();

            if (HotkeyCtrl)
            {
                parts.Add("Ctrl");
            }

            if (HotkeyAlt)
            {
                parts.Add("Alt");
            }

            if (HotkeyShift)
            {
                parts.Add("Shift");
            }

            if (HotkeyWin)
            {
                parts.Add("Win");
            }

            parts.Add(
                GetKeyDisplayName(
                    HotkeyVirtualKey));

            return string.Join(" + ", parts);
        }

        public static string GetKeyDisplayName(
            VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Snapshot => "Print Screen",
                VirtualKey.Delete => "Delete",
                VirtualKey.Insert => "Insert",

                VirtualKey.Home => "Home",
                VirtualKey.End => "End",

                VirtualKey.PageUp => "Page Up",
                VirtualKey.PageDown => "Page Down",

                VirtualKey.Left => "Left Arrow",
                VirtualKey.Right => "Right Arrow",
                VirtualKey.Up => "Up Arrow",
                VirtualKey.Down => "Down Arrow",

                VirtualKey.Space => "Space",
                VirtualKey.Tab => "Tab",
                VirtualKey.Enter => "Enter",
                VirtualKey.Back => "Backspace",

                VirtualKey.NumberPad0 => "Num 0",
                VirtualKey.NumberPad1 => "Num 1",
                VirtualKey.NumberPad2 => "Num 2",
                VirtualKey.NumberPad3 => "Num 3",
                VirtualKey.NumberPad4 => "Num 4",
                VirtualKey.NumberPad5 => "Num 5",
                VirtualKey.NumberPad6 => "Num 6",
                VirtualKey.NumberPad7 => "Num 7",
                VirtualKey.NumberPad8 => "Num 8",
                VirtualKey.NumberPad9 => "Num 9",

                VirtualKey.Multiply => "Num *",
                VirtualKey.Add => "Num +",
                VirtualKey.Subtract => "Num -",
                VirtualKey.Decimal => "Num .",
                VirtualKey.Divide => "Num /",

                _ => key.ToString()
            };
        }

        private bool GetBool(
            string key,
            bool defaultValue)
        {
            if (_settings.Values[key] is bool value)
            {
                return value;
            }

            return defaultValue;
        }
    }
}