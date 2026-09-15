using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SmartScreenshotManager.Services;
using System;
using System.IO;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SmartScreenshotManager.Views
{
    public sealed partial class SettingsPage : UserControl
    {
        private readonly SettingsService _settingsService;

        private bool _isRecordingHotkey;

        private bool _ctrlPressed;
        private bool _altPressed;
        private bool _shiftPressed;
        private bool _winPressed;

        public Window? ParentWindow { get; set; }

        public event Action<string>? ScreenshotFolderChanged;

        public event Action? HotkeyChanged;

        public SettingsPage()
        {
            InitializeComponent();

            _settingsService =
                new SettingsService();

            LoadSettings();
        }

        private void LoadSettings()
        {
            ScreenshotFolderText.Text =
                string.IsNullOrWhiteSpace(
                    _settingsService.ScreenshotFolder)
                    ? "No folder selected"
                    : _settingsService.ScreenshotFolder;

            UpdateHotkeyText();
        }

        // =========================
        // Screenshot folder
        // =========================

        private async void ChangeFolderButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (ParentWindow == null)
                return;

            var folderPicker =
                new FolderPicker();

            var hwnd =
                WindowNative.GetWindowHandle(
                    ParentWindow);

            InitializeWithWindow.Initialize(
                folderPicker,
                hwnd);

            folderPicker.FileTypeFilter.Add("*");

            var folder =
                await folderPicker.PickSingleFolderAsync();

            if (folder == null)
                return;

            if (!Directory.Exists(folder.Path))
                return;

            _settingsService.ScreenshotFolder =
                folder.Path;

            ScreenshotFolderText.Text =
                folder.Path;

            ScreenshotFolderChanged?.Invoke(
                folder.Path);
        }

        // =========================
        // Hotkey recorder
        // =========================

        private void ChangeHotkeyButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StartHotkeyRecording();
        }

        private void StartHotkeyRecording()
        {
            _isRecordingHotkey = true;

            _ctrlPressed = false;
            _altPressed = false;
            _shiftPressed = false;
            _winPressed = false;

            ChangeHotkeyButton.IsEnabled =
                false;

            ResetHotkeyButton.IsEnabled =
                false;

            CurrentHotkeyText.Text =
                "Waiting for input...";

            HotkeyStatusText.Text =
                "Press any key or key combination. Press Esc to cancel.";

            RootControl.Focus(
                FocusState.Programmatic);
        }

        private void StopHotkeyRecording()
        {
            _isRecordingHotkey = false;

            _ctrlPressed = false;
            _altPressed = false;
            _shiftPressed = false;
            _winPressed = false;

            ChangeHotkeyButton.IsEnabled =
                true;

            ResetHotkeyButton.IsEnabled =
                true;
        }

        private void CancelHotkeyRecording()
        {
            StopHotkeyRecording();

            UpdateHotkeyText();

            HotkeyStatusText.Text =
                "Hotkey change cancelled.";
        }

        private void RootControl_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (!_isRecordingHotkey)
                return;

            VirtualKey key =
                e.Key;

            // Ignore Fn / unknown key code
            if ((int)key == 255)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;

            if (key == VirtualKey.Escape)
            {
                CancelHotkeyRecording();
                return;
            }

            if (IsModifierKey(key))
            {
                SetModifierState(
                    key,
                    true);

                UpdateRecordingPreview();

                return;
            }

            SaveRecordedHotkey(
                key);
        }

        private void RootControl_KeyUp(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (!_isRecordingHotkey)
                return;

            VirtualKey key =
                e.Key;

            if ((int)key == 255)
            {
                e.Handled = true;
                return;
            }

            if (!IsModifierKey(key))
                return;

            e.Handled = true;

            SetModifierState(
                key,
                false);

            UpdateRecordingPreview();
        }

        private void SaveRecordedHotkey(
            VirtualKey key)
        {
            _settingsService.SetHotkey(
                key,
                _ctrlPressed,
                _altPressed,
                _shiftPressed,
                _winPressed);

            StopHotkeyRecording();

            UpdateHotkeyText();

            HotkeyStatusText.Text =
                "Shortcut saved.";

            HotkeyChanged?.Invoke();
        }

        private void UpdateRecordingPreview()
        {
            if (!_isRecordingHotkey)
                return;

            string preview =
                BuildHotkeyText(
                    _ctrlPressed,
                    _altPressed,
                    _shiftPressed,
                    _winPressed);

            if (string.IsNullOrWhiteSpace(
                    preview))
            {
                CurrentHotkeyText.Text =
                    "Waiting for input...";
            }
            else
            {
                CurrentHotkeyText.Text =
                    preview + " + ...";
            }
        }

        private void ResetHotkeyButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            _settingsService.ResetHotkey();

            UpdateHotkeyText();

            HotkeyStatusText.Text =
                "Reset to Alt + S.";

            HotkeyChanged?.Invoke();
        }

        private void UpdateHotkeyText()
        {
            CurrentHotkeyText.Text =
                _settingsService
                    .GetHotkeyDisplayText();

            HotkeyStatusText.Text =
                "Press Change hotkey to record a new shortcut.";
        }

        // =========================
        // Modifier handling
        // =========================

        private static bool IsModifierKey(
            VirtualKey key)
        {
            return
                key == VirtualKey.Control ||
                key == VirtualKey.Menu ||
                key == VirtualKey.Shift ||
                key == VirtualKey.LeftWindows ||
                key == VirtualKey.RightWindows;
        }

        private void SetModifierState(
            VirtualKey key,
            bool isPressed)
        {
            switch (key)
            {
                case VirtualKey.Control:

                    _ctrlPressed =
                        isPressed;

                    break;

                case VirtualKey.Menu:

                    _altPressed =
                        isPressed;

                    break;

                case VirtualKey.Shift:

                    _shiftPressed =
                        isPressed;

                    break;

                case VirtualKey.LeftWindows:
                case VirtualKey.RightWindows:

                    _winPressed =
                        isPressed;

                    break;
            }
        }

        private static string BuildHotkeyText(
            bool ctrl,
            bool alt,
            bool shift,
            bool win)
        {
            var parts =
                new System.Collections.Generic.List<string>();

            if (ctrl)
            {
                parts.Add("Ctrl");
            }

            if (alt)
            {
                parts.Add("Alt");
            }

            if (shift)
            {
                parts.Add("Shift");
            }

            if (win)
            {
                parts.Add("Win");
            }

            return string.Join(
                " + ",
                parts);
        }
    }
}