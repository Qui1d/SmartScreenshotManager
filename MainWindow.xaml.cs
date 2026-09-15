using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SmartScreenshotManager.Models;
using SmartScreenshotManager.Services;
using SmartScreenshotManager.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace SmartScreenshotManager
{
    public sealed partial class MainWindow : Window
    {
        private const int SwHide = 0;
        private const int SwShow = 5;

        public ObservableCollection<ScreenshotItem> Screenshots { get; set; } =
            new();

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly SettingsService _settingsService;

        private HotkeyService? _hotkeyService;
        private SnippingWindow? _snippingWindow;
        private ImageViewerWindow? _imageViewerWindow;

        private FileSystemWatcher? _watcher;
        private string? _currentFolderPath;
        private string? _detailsFilePath;

        private bool _mainWindowHiddenForSnip;

        private bool _sortNewestFirst = true;

        public MainWindow()
        {
            InitializeComponent();

            ConfigureTitleBar();

            _dispatcherQueue =
                DispatcherQueue.GetForCurrentThread();

            _settingsService =
                new SettingsService();

            SettingsView.ParentWindow =
                this;

            SettingsView.ScreenshotFolderChanged +=
                SettingsView_ScreenshotFolderChanged;

            SettingsView.HotkeyChanged +=
                SettingsView_HotkeyChanged;

            InitializeGlobalHotkey();

            LoadSavedFolder();

            Closed +=
                MainWindow_Closed;
        }

        // =========================
        // Title bar
        // =========================

        private void ConfigureTitleBar()
        {
            if (!Microsoft.UI.Windowing.AppWindowTitleBar
                .IsCustomizationSupported())
            {
                return;
            }

            var titleBar =
                AppWindow.TitleBar;

            var titleBarColor =
                Microsoft.UI.ColorHelper.FromArgb(
                    255,
                    43,
                    43,
                    43);

            var inactiveColor =
                Microsoft.UI.ColorHelper.FromArgb(
                    255,
                    38,
                    38,
                    38);

            var hoverColor =
                Microsoft.UI.ColorHelper.FromArgb(
                    255,
                    58,
                    58,
                    58);

            var pressedColor =
                Microsoft.UI.ColorHelper.FromArgb(
                    255,
                    70,
                    70,
                    70);

            titleBar.BackgroundColor =
                titleBarColor;

            titleBar.ForegroundColor =
                Microsoft.UI.Colors.White;

            titleBar.InactiveBackgroundColor =
                inactiveColor;

            titleBar.InactiveForegroundColor =
                Microsoft.UI.Colors.Gray;

            titleBar.ButtonBackgroundColor =
                titleBarColor;

            titleBar.ButtonForegroundColor =
                Microsoft.UI.Colors.White;

            titleBar.ButtonHoverBackgroundColor =
                hoverColor;

            titleBar.ButtonHoverForegroundColor =
                Microsoft.UI.Colors.White;

            titleBar.ButtonPressedBackgroundColor =
                pressedColor;

            titleBar.ButtonPressedForegroundColor =
                Microsoft.UI.Colors.White;

            titleBar.ButtonInactiveBackgroundColor =
                inactiveColor;

            titleBar.ButtonInactiveForegroundColor =
                Microsoft.UI.Colors.Gray;
        }

        // =========================
        // Global hotkey
        // =========================

        private void InitializeGlobalHotkey()
        {
            IntPtr hwnd =
                WindowNative.GetWindowHandle(
                    this);

            _hotkeyService =
                new HotkeyService(
                    hwnd);

            _hotkeyService.HotkeyPressed +=
                HotkeyService_HotkeyPressed;

            RegisterCurrentHotkey();
        }

        private void RegisterCurrentHotkey()
        {
            if (_hotkeyService == null)
                return;

            bool registered =
                _hotkeyService.RegisterHotkey(
                    _settingsService.HotkeyVirtualKey,
                    _settingsService.HotkeyCtrl,
                    _settingsService.HotkeyAlt,
                    _settingsService.HotkeyShift,
                    _settingsService.HotkeyWin);

            if (!registered)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Could not register hotkey: " +
                    $"{_settingsService.GetHotkeyDisplayText()}");
            }
        }

        private void SettingsView_HotkeyChanged()
        {
            RegisterCurrentHotkey();
        }

        private void HotkeyService_HotkeyPressed()
        {
            StartSnip(
                hideMainWindow: false);
        }

        // =========================
        // Snipping
        // =========================

        private void NewSnipButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StartSnip(
                hideMainWindow: true);
        }

        private async void StartSnip(
            bool hideMainWindow)
        {
            if (_snippingWindow != null)
                return;

            if (string.IsNullOrWhiteSpace(
                    _currentFolderPath) ||
                !Directory.Exists(
                    _currentFolderPath))
            {
                await ShowNoFolderDialogAsync();
                return;
            }

            _mainWindowHiddenForSnip =
                hideMainWindow;

            IntPtr mainWindowHandle =
                WindowNative.GetWindowHandle(
                    this);

            if (_mainWindowHiddenForSnip)
            {
                ShowWindow(
                    mainWindowHandle,
                    SwHide);

                await Task.Delay(
                    180);
            }
            else
            {
                await Task.Delay(
                    50);
            }

            try
            {
                _snippingWindow =
                    new SnippingWindow(
                        _currentFolderPath);

                _snippingWindow.SnipCompleted +=
                    SnippingWindow_SnipCompleted;

                _snippingWindow.SnipCancelled +=
                    SnippingWindow_SnipCancelled;

                await _snippingWindow
                    .PrepareAsync();

                _snippingWindow.Activate();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    exception);

                CleanupSnippingWindow();

                RestoreMainWindowIfNeeded();
            }
        }

        private void SnippingWindow_SnipCompleted(
            string filePath)
        {
            CleanupSnippingWindow();

            RestoreMainWindowIfNeeded();

            _ =
                EnsureScreenshotAppearsAsync(
                    filePath);
        }

        private void SnippingWindow_SnipCancelled()
        {
            CleanupSnippingWindow();

            RestoreMainWindowIfNeeded();
        }

        private void CleanupSnippingWindow()
        {
            if (_snippingWindow == null)
                return;

            _snippingWindow.SnipCompleted -=
                SnippingWindow_SnipCompleted;

            _snippingWindow.SnipCancelled -=
                SnippingWindow_SnipCancelled;

            _snippingWindow =
                null;
        }

        private void RestoreMainWindowIfNeeded()
        {
            if (!_mainWindowHiddenForSnip)
                return;

            _mainWindowHiddenForSnip =
                false;

            IntPtr hwnd =
                WindowNative.GetWindowHandle(
                    this);

            ShowWindow(
                hwnd,
                SwShow);

            Activate();
        }

        private async Task EnsureScreenshotAppearsAsync(
            string filePath)
        {
            await Task.Delay(
                400);

            _dispatcherQueue.TryEnqueue(() =>
            {
                AddScreenshot(
                    filePath);
            });
        }

        private async Task ShowNoFolderDialogAsync()
        {
            Activate();

            var dialog =
                new ContentDialog
                {
                    Title =
                        "Screenshot folder not selected",

                    Content =
                        "Open Settings and select a folder before taking screenshots.",

                    CloseButtonText =
                        "OK",

                    XamlRoot =
                        MainGrid.XamlRoot
                };

            try
            {
                await dialog.ShowAsync();
            }
            catch
            {
            }
        }

        // =========================
        // Sorting
        // =========================

        private void NewestFirstMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            _sortNewestFirst =
                true;

            SortButton.Content =
                "Newest first";

            ApplyCurrentSort();
        }

        private void OldestFirstMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            _sortNewestFirst =
                false;

            SortButton.Content =
                "Oldest first";

            ApplyCurrentSort();
        }

        private void ApplyCurrentSort()
        {
            if (Screenshots.Count <= 1)
                return;

            var sortedScreenshots =
                _sortNewestFirst
                    ? Screenshots
                        .OrderByDescending(
                            x => x.CreatedAt)
                        .ToList()
                    : Screenshots
                        .OrderBy(
                            x => x.CreatedAt)
                        .ToList();

            Screenshots.Clear();

            foreach (var screenshot
                     in sortedScreenshots)
            {
                Screenshots.Add(
                    screenshot);
            }
        }

        // =========================
        // Screenshot actions
        // =========================

        private void ScreenshotCard_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e)
        {
            e.Handled =
                true;

            if (sender is not FrameworkElement element)
                return;

            if (element.DataContext
                is not ScreenshotItem screenshot)
            {
                return;
            }

            string filePath =
                screenshot.FilePath;

            _dispatcherQueue.TryEnqueue(() =>
            {
                OpenScreenshot(
                    filePath);
            });
        }

        private void OpenScreenshotMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem)
                return;

            string? filePath =
                menuItem.Tag as string;

            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            OpenScreenshot(
                filePath);
        }

        private void OpenScreenshot(
            string filePath)
        {
            if (!File.Exists(
                    filePath))
            {
                return;
            }

            if (_imageViewerWindow != null)
            {
                try
                {
                    _imageViewerWindow.Close();
                }
                catch
                {
                }

                _imageViewerWindow =
                    null;
            }

            _imageViewerWindow =
                new ImageViewerWindow(
                    filePath,
                    Path.GetFileName(
                        filePath));

            _imageViewerWindow.Closed +=
                ImageViewerWindow_Closed;

            _imageViewerWindow.Activate();
        }

        private void ImageViewerWindow_Closed(
            object sender,
            WindowEventArgs args)
        {
            if (_imageViewerWindow == null)
                return;

            _imageViewerWindow.Closed -=
                ImageViewerWindow_Closed;

            _imageViewerWindow =
                null;
        }

        private void DetailsScreenshotMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem)
                return;

            string? filePath =
                menuItem.Tag as string;

            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            var screenshot =
                Screenshots.FirstOrDefault(x =>
                    string.Equals(
                        x.FilePath,
                        filePath,
                        StringComparison.OrdinalIgnoreCase));

            if (screenshot == null)
                return;

            ShowScreenshotDetails(
                screenshot);
        }

        private void ShowScreenshotDetails(
            ScreenshotItem screenshot)
        {
            _detailsFilePath =
                screenshot.FilePath;

            DetailsColumn.Width =
                new GridLength(
                    412);

            GalleryColumn.Width =
                new GridLength(
                    1,
                    GridUnitType.Star);

            DetailsPanel.Visibility =
                Visibility.Visible;

            var bitmap =
                new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    CreateOptions =
                        Microsoft.UI.Xaml.Media.Imaging
                            .BitmapCreateOptions
                            .IgnoreImageCache,

                    UriSource =
                        new Uri(
                            screenshot.FilePath)
                };

            DetailsImage.Source =
                bitmap;

            DetailsFileName.Text =
                screenshot.FileName;

            DetailsDate.Text =
                screenshot.CreatedAt
                    .ToString(
                        "dd.MM.yyyy HH:mm");

            DetailsCategory.Text =
                string.IsNullOrWhiteSpace(
                    screenshot.Category)
                    ? "Not assigned"
                    : screenshot.Category;

            DetailsDescription.Text =
                string.IsNullOrWhiteSpace(
                    screenshot.Description)
                    ? "No description yet"
                    : screenshot.Description;

            DetailsOcrText.Text =
                string.IsNullOrWhiteSpace(
                    screenshot.OcrText)
                    ? "OCR has not been processed yet"
                    : screenshot.OcrText;
        }

        private async void CopyScreenshotMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem)
                return;

            string? filePath =
                menuItem.Tag as string;

            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            if (!File.Exists(
                    filePath))
            {
                return;
            }

            try
            {
                StorageFile file =
                    await StorageFile
                        .GetFileFromPathAsync(
                            filePath);

                var dataPackage =
                    new DataPackage
                    {
                        RequestedOperation =
                            DataPackageOperation.Copy
                    };

                dataPackage.SetBitmap(
                    RandomAccessStreamReference
                        .CreateFromFile(
                            file));

                Clipboard.SetContent(
                    dataPackage);

                Clipboard.Flush();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    exception);
            }
        }

        private async void DeleteScreenshotMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem)
                return;

            string? filePath =
                menuItem.Tag as string;

            if (string.IsNullOrWhiteSpace(
                    filePath))
            {
                return;
            }

            if (!File.Exists(
                    filePath))
            {
                return;
            }

            var dialog =
                new ContentDialog
                {
                    Title =
                        "Delete screenshot?",

                    Content =
                        $"This will permanently delete:\n\n" +
                        $"{Path.GetFileName(filePath)}",

                    PrimaryButtonText =
                        "Delete",

                    CloseButtonText =
                        "Cancel",

                    DefaultButton =
                        ContentDialogButton.Close,

                    XamlRoot =
                        MainGrid.XamlRoot
                };

            ContentDialogResult result;

            try
            {
                result =
                    await dialog.ShowAsync();
            }
            catch
            {
                return;
            }

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                if (string.Equals(
                        _detailsFilePath,
                        filePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CloseDetailsPanel();
                }

                if (_imageViewerWindow != null)
                {
                    try
                    {
                        _imageViewerWindow.Close();
                    }
                    catch
                    {
                    }

                    _imageViewerWindow =
                        null;
                }

                File.Delete(
                    filePath);

                _dispatcherQueue.TryEnqueue(() =>
                {
                    var screenshot =
                        Screenshots.FirstOrDefault(x =>
                            string.Equals(
                                x.FilePath,
                                filePath,
                                StringComparison.OrdinalIgnoreCase));

                    if (screenshot != null)
                    {
                        Screenshots.Remove(
                            screenshot);
                    }
                });
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    exception);
            }
        }

        // =========================
        // Screenshot folder
        // =========================

        private void LoadSavedFolder()
        {
            string? savedFolder =
                _settingsService.ScreenshotFolder;

            if (string.IsNullOrWhiteSpace(
                    savedFolder))
            {
                SelectedFolderText.Text =
                    "No screenshot folder selected";

                return;
            }

            if (!Directory.Exists(
                    savedFolder))
            {
                SelectedFolderText.Text =
                    "Screenshot folder does not exist";

                return;
            }

            SetScreenshotFolder(
                savedFolder);
        }

        private void SettingsView_ScreenshotFolderChanged(
            string folderPath)
        {
            SetScreenshotFolder(
                folderPath);

            ShowGalleryPage();
        }

        private void SetScreenshotFolder(
            string folderPath)
        {
            if (!Directory.Exists(
                    folderPath))
            {
                return;
            }

            _currentFolderPath =
                folderPath;

            SelectedFolderText.Text =
                folderPath;

            Screenshots.Clear();

            LoadScreenshotsFromFolder(
                folderPath);

            StartWatchingFolder(
                folderPath);

            CloseDetailsPanel();
        }

        private void LoadScreenshotsFromFolder(
            string folderPath)
        {
            if (!Directory.Exists(
                    folderPath))
            {
                return;
            }

            var files =
                Directory
                    .EnumerateFiles(
                        folderPath)
                    .Where(
                        IsSupportedImage);

            foreach (var file in files)
            {
                AddScreenshot(
                    file);
            }

            ApplyCurrentSort();
        }

        private void AddScreenshot(
            string filePath)
        {
            if (!File.Exists(
                    filePath))
            {
                return;
            }

            if (!IsSupportedImage(
                    filePath))
            {
                return;
            }

            bool alreadyExists =
                Screenshots.Any(x =>
                    string.Equals(
                        x.FilePath,
                        filePath,
                        StringComparison.OrdinalIgnoreCase));

            if (alreadyExists)
                return;

            var screenshot =
                new ScreenshotItem
                {
                    FilePath =
                        filePath,

                    FileName =
                        Path.GetFileName(
                            filePath),

                    CreatedAt =
                        File.GetCreationTime(
                            filePath)
                };

            Screenshots.Add(
                screenshot);

            ApplyCurrentSort();
        }

        private static bool IsSupportedImage(
            string filePath)
        {
            string extension =
                Path.GetExtension(
                    filePath);

            return
                extension.Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase)
                ||
                extension.Equals(
                    ".jpg",
                    StringComparison.OrdinalIgnoreCase)
                ||
                extension.Equals(
                    ".jpeg",
                    StringComparison.OrdinalIgnoreCase);
        }

        // =========================
        // Folder watcher
        // =========================

        private void StartWatchingFolder(
            string folderPath)
        {
            StopWatchingFolder();

            _watcher =
                new FileSystemWatcher(
                    folderPath)
                {
                    IncludeSubdirectories =
                        false,

                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.CreationTime |
                        NotifyFilters.LastWrite
                };

            _watcher.Created +=
                Watcher_Created;

            _watcher.Renamed +=
                Watcher_Renamed;

            _watcher.Deleted +=
                Watcher_Deleted;

            _watcher.EnableRaisingEvents =
                true;
        }

        private void StopWatchingFolder()
        {
            if (_watcher == null)
                return;

            _watcher.EnableRaisingEvents =
                false;

            _watcher.Created -=
                Watcher_Created;

            _watcher.Renamed -=
                Watcher_Renamed;

            _watcher.Deleted -=
                Watcher_Deleted;

            _watcher.Dispose();

            _watcher =
                null;
        }

        private void Watcher_Created(
            object sender,
            FileSystemEventArgs e)
        {
            if (!IsSupportedImage(
                    e.FullPath))
            {
                return;
            }

            _ =
                WaitAndAddScreenshotAsync(
                    e.FullPath);
        }

        private void Watcher_Renamed(
            object sender,
            RenamedEventArgs e)
        {
            if (!IsSupportedImage(
                    e.FullPath))
            {
                return;
            }

            _ =
                WaitAndAddScreenshotAsync(
                    e.FullPath);
        }

        private void Watcher_Deleted(
            object sender,
            FileSystemEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var screenshot =
                    Screenshots.FirstOrDefault(x =>
                        string.Equals(
                            x.FilePath,
                            e.FullPath,
                            StringComparison.OrdinalIgnoreCase));

                if (screenshot == null)
                    return;

                if (string.Equals(
                        _detailsFilePath,
                        e.FullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CloseDetailsPanel();
                }

                Screenshots.Remove(
                    screenshot);
            });
        }

        private async Task WaitAndAddScreenshotAsync(
            string filePath)
        {
            bool ready =
                false;

            for (int i = 0;
                 i < 40;
                 i++)
            {
                try
                {
                    if (File.Exists(
                            filePath))
                    {
                        using var stream =
                            new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.None);

                        if (stream.Length > 0)
                        {
                            ready =
                                true;

                            break;
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                await Task.Delay(
                    100);
            }

            if (!ready)
                return;

            await Task.Delay(
                100);

            _dispatcherQueue.TryEnqueue(() =>
            {
                AddScreenshot(
                    filePath);
            });
        }

        // =========================
        // Navigation
        // =========================

        private void AllScreenshotsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ShowGalleryPage();
        }

        private void SettingsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseDetailsPanel();

            GalleryPage.Visibility =
                Visibility.Collapsed;

            SettingsPageContainer.Visibility =
                Visibility.Visible;
        }

        private void ShowGalleryPage()
        {
            SettingsPageContainer.Visibility =
                Visibility.Collapsed;

            GalleryPage.Visibility =
                Visibility.Visible;
        }

        // =========================
        // Drag & Drop
        // =========================

        private void MainGrid_DragEnter(
            object sender,
            DragEventArgs e)
        {
            if (GalleryPage.Visibility !=
                Visibility.Visible)
            {
                return;
            }

            if (!e.DataView.Contains(
                    StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation =
                DataPackageOperation.Copy;

            DropOverlay.Visibility =
                Visibility.Visible;
        }

        private void MainGrid_DragOver(
            object sender,
            DragEventArgs e)
        {
            if (GalleryPage.Visibility !=
                Visibility.Visible)
            {
                return;
            }

            if (!e.DataView.Contains(
                    StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation =
                DataPackageOperation.Copy;

            DropOverlay.Visibility =
                Visibility.Visible;
        }

        private void MainGrid_DragLeave(
            object sender,
            DragEventArgs e)
        {
            DropOverlay.Visibility =
                Visibility.Collapsed;
        }

        private async void MainGrid_Drop(
            object sender,
            DragEventArgs e)
        {
            DropOverlay.Visibility =
                Visibility.Collapsed;

            if (GalleryPage.Visibility !=
                Visibility.Visible)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(
                    _currentFolderPath))
            {
                return;
            }

            if (!e.DataView.Contains(
                    StandardDataFormats.StorageItems))
            {
                return;
            }

            var items =
                await e.DataView
                    .GetStorageItemsAsync();

            foreach (var item in items)
            {
                if (item is not StorageFile file)
                    continue;

                if (!IsSupportedImage(
                        file.Path))
                {
                    continue;
                }

                await CopyFileToScreenshotFolderAsync(
                    file);
            }
        }

        private async Task CopyFileToScreenshotFolderAsync(
            StorageFile file)
        {
            if (string.IsNullOrWhiteSpace(
                    _currentFolderPath))
            {
                return;
            }

            var destinationFolder =
                await StorageFolder
                    .GetFolderFromPathAsync(
                        _currentFolderPath);

            string destinationName =
                GetUniqueFileName(
                    _currentFolderPath,
                    file.Name);

            await file.CopyAsync(
                destinationFolder,
                destinationName,
                NameCollisionOption.FailIfExists);
        }

        private static string GetUniqueFileName(
            string folderPath,
            string fileName)
        {
            string nameWithoutExtension =
                Path.GetFileNameWithoutExtension(
                    fileName);

            string extension =
                Path.GetExtension(
                    fileName);

            string candidate =
                fileName;

            int index =
                1;

            while (File.Exists(
                Path.Combine(
                    folderPath,
                    candidate)))
            {
                candidate =
                    $"{nameWithoutExtension}_{index}{extension}";

                index++;
            }

            return candidate;
        }

        // =========================
        // Details panel
        // =========================

        private void CloseDetailsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            CloseDetailsPanel();
        }

        private void CloseDetailsPanel()
        {
            _detailsFilePath =
                null;

            DetailsImage.Source =
                null;

            DetailsPanel.Visibility =
                Visibility.Collapsed;

            DetailsColumn.Width =
                new GridLength(
                    0);

            GalleryColumn.Width =
                new GridLength(
                    1,
                    GridUnitType.Star);

            ScreenshotGridView.SelectedItem =
                null;
        }

        // =========================
        // Cleanup
        // =========================

        private void MainWindow_Closed(
            object sender,
            WindowEventArgs args)
        {
            StopWatchingFolder();

            if (_imageViewerWindow != null)
            {
                try
                {
                    _imageViewerWindow.Close();
                }
                catch
                {
                }

                _imageViewerWindow =
                    null;
            }

            if (_hotkeyService != null)
            {
                _hotkeyService.HotkeyPressed -=
                    HotkeyService_HotkeyPressed;

                _hotkeyService.Dispose();

                _hotkeyService =
                    null;
            }
        }

        // =========================
        // Win32
        // =========================

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(
            IntPtr hWnd,
            int command);
    }
}