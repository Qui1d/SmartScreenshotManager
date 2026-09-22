using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SmartScreenshotManager.Services;
using SmartScreenshotManager.Models;
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
        private bool _settingsLoaded;

        private bool _ctrlPressed;
        private bool _altPressed;
        private bool _shiftPressed;
        private bool _winPressed;

        public Window? ParentWindow { get; set; }

        public event Action<string>? ScreenshotFolderChanged;

        public event Action? HotkeyChanged;
        public event Action<AiConfiguration>? AiSettingsChanged;

        public SettingsPage()
        {
            InitializeComponent();

            _settingsService =
                new SettingsService();

            LoadSettings();
            _settingsLoaded = true;
        }

        private void AutoCopyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingsLoaded) return;
            try
            {
                _settingsService.AutoCopyScreenshot = AutoCopyToggle.IsOn;
                CaptureSettingsStatusText.Text = "Saved.";
            }
            catch { CaptureSettingsStatusText.Text = "Could not save the clipboard setting."; }
        }

        private void SaveUsageLimit_Click(object sender, RoutedEventArgs e)
        {
            double value = AiDailyLimitBox.Value;
            if (!double.IsFinite(value) || value < 0 || value > 10000 || value != Math.Truncate(value))
            { UsageSettingsStatusText.Text = "Enter a whole number from 0 to 10000."; return; }
            try
            {
                _settingsService.AiDailyLimit = (int)value;
                AiSettingsChanged?.Invoke(_settingsService.GetAiConfiguration());
                UsageSettingsStatusText.Text = "Daily limit saved.";
                RefreshAiUsage();
            }
            catch { UsageSettingsStatusText.Text = "Could not apply the limit. Check AI settings and try again."; }
        }

        private void LoadSettings()
        {
            ScreenshotFolderText.Text =
                string.IsNullOrWhiteSpace(
                    _settingsService.ScreenshotFolder)
                    ? "No folder selected"
                    : _settingsService.ScreenshotFolder;

            AutoCopyToggle.IsOn = _settingsService.AutoCopyScreenshot;
            UpdateHotkeyText();
            AiEnabledToggle.IsOn = _settingsService.AiEnabled;
            AiAutomaticToggle.IsOn = _settingsService.AiAutomatic;
            AiModelBox.Text = _settingsService.AiModel;
            AiDailyLimitBox.Value = _settingsService.AiDailyLimit;
            try
            {
                AiKeyStatusText.Text = string.IsNullOrEmpty(new ApiKeyStore().Read())
                    ? "No API key saved." : "API key saved in Windows Credential Locker.";
            }
            catch { AiSettingsStatusText.Text = "Could not read the saved API key. Save a new key to continue."; }
        }

        public SmartScreenshotManager.Data.ScreenshotRepository? UsageRepository { get; set; }
        public async void RefreshAiUsage()
        {
            var repository = UsageRepository;
            if (repository == null) return;
            try { AiUsageText.Text = await System.Threading.Tasks.Task.Run(repository.GetAiUsageSummary); }
            catch { AiUsageText.Text = "Could not read usage statistics."; }
        }
        private void RefreshAiUsageButton_Click(object sender, RoutedEventArgs e) => RefreshAiUsage();

        private void SaveAiSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string model = AiModelBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(model) || model.Length > 120)
                {
                    AiSettingsStatusText.Text = "Enter a valid model name.";
                    return;
                }
                var store = new ApiKeyStore();
                string enteredKey = AiApiKeyBox.Password.Trim();
                if (enteredKey.Length > 0) store.Save(enteredKey);
                AiApiKeyBox.Password = string.Empty;
                string key = store.Read();
                if (AiEnabledToggle.IsOn && string.IsNullOrWhiteSpace(key))
                {
                    AiSettingsStatusText.Text = "Save an API key before enabling AI analysis.";
                    return;
                }
                _settingsService.AiModel = model;
                _settingsService.AiEnabled = AiEnabledToggle.IsOn;
                _settingsService.AiAutomatic = AiAutomaticToggle.IsOn;
                AiSettingsChanged?.Invoke(new AiConfiguration
                {
                    Enabled = _settingsService.AiEnabled, Automatic = _settingsService.AiAutomatic,
                    Model = model, ApiKey = key, DailyLimit = _settingsService.AiDailyLimit
                });
                AiKeyStatusText.Text = key.Length == 0 ? "No API key saved." : "API key saved in Windows Credential Locker.";
                AiSettingsStatusText.Text = "Settings saved. The API key will be checked when you analyze a screenshot.";
            }
            catch { AiSettingsStatusText.Text = "Could not save AI settings or access Windows Credential Locker."; }
        }

        private void RemoveAiKeyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Stop outgoing work before removing the stored credential.
                _settingsService.AiEnabled = false;
                _settingsService.AiAutomatic = false;
                AiSettingsChanged?.Invoke(new AiConfiguration());
                AiEnabledToggle.IsOn = false;
                AiAutomaticToggle.IsOn = false;
                new ApiKeyStore().Remove();
                AiApiKeyBox.Password = string.Empty;
                AiKeyStatusText.Text = "No API key saved.";
                AiSettingsStatusText.Text = "API key removed. AI analysis is disabled.";
            }
            catch { AiSettingsStatusText.Text = "AI is disabled, but the saved key could not be removed. Try again."; }
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