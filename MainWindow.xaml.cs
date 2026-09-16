using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SmartScreenshotManager.Data;
using SmartScreenshotManager.Models;
using SmartScreenshotManager.Services;
using SmartScreenshotManager.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

        public ObservableCollection<ScreenshotItem> VisibleScreenshots { get; } = new();
        private bool _showFavoritesOnly;
        private string? _selectedCategory;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly SettingsService _settingsService;
        private readonly ScreenshotRepository _repository;
        private readonly SemaphoreSlim _storageGate = new(1, 1);
        private int _folderVersion;
        private bool _isClosed;

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

            _repository = new ScreenshotRepository(Path.Combine(
                ApplicationData.Current.LocalFolder.Path, "screenshots.db"));

            SettingsView.ParentWindow =
                this;

            SettingsView.ScreenshotFolderChanged +=
                SettingsView_ScreenshotFolderChanged;

            SettingsView.HotkeyChanged +=
                SettingsView_HotkeyChanged;

            InitializeGlobalHotkey();

            UpdateGallerySection();
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
            var filtered = Screenshots.Where(x => (!_showFavoritesOnly || x.IsFavorite)
                && (_selectedCategory == null || x.Category == _selectedCategory));
            var desired = (_sortNewestFirst
                ? filtered.OrderByDescending(x => x.AddedAt).ThenBy(x => x.Id)
                : filtered.OrderBy(x => x.AddedAt).ThenBy(x => x.Id)).ToList();

            // Keep existing card instances and hover/focus when only one item changes.
            var desiredItems = desired.ToHashSet();
            for (int i = VisibleScreenshots.Count - 1; i >= 0; i--)
                if (!desiredItems.Contains(VisibleScreenshots[i])) VisibleScreenshots.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < VisibleScreenshots.Count && ReferenceEquals(VisibleScreenshots[i], desired[i]))
                    continue;
                int existingIndex = VisibleScreenshots.IndexOf(desired[i]);
                if (existingIndex >= 0) VisibleScreenshots.Move(existingIndex, i);
                else VisibleScreenshots.Insert(i, desired[i]);
            }

            if (_detailsFilePath != null && !desired.Any(x => string.Equals(
                x.FilePath, _detailsFilePath, StringComparison.OrdinalIgnoreCase)))
                CloseDetailsPanel();
            EmptyGalleryText.Text = _showFavoritesOnly
                ? "No favorite screenshots yet."
                : $"No screenshots in {_selectedCategory} yet.";
            EmptyGalleryText.Visibility = (_showFavoritesOnly || _selectedCategory != null)
                && VisibleScreenshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateGallerySection()
        {
            GalleryTitle.Text = _showFavoritesOnly ? "Favorites" : _selectedCategory ?? "All Screenshots";
            var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            AllScreenshotsButton.Style = !_showFavoritesOnly && _selectedCategory == null ? accentStyle : null;
            FavoritesButton.Style = _showFavoritesOnly ? accentStyle : null;
            GamingButton.Style = _selectedCategory == "Gaming" ? accentStyle : null;
            ProgrammingButton.Style = _selectedCategory == "Programming" ? accentStyle : null;
            DocumentsButton.Style = _selectedCategory == "Documents" ? accentStyle : null;
            OtherButton.Style = _selectedCategory == "Other" ? accentStyle : null;
            ApplyCurrentSort();
        }

        private async void CategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: int id } menuItem)
                await ChangeCategoryAsync(id, menuItem.Text);
        }

        private async void ClearCategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: int id })
                await ChangeCategoryAsync(id, null);
        }

        private async Task ChangeCategoryAsync(int id, string? category)
        {
            var item = Screenshots.FirstOrDefault(x => x.Id == id);
            if (item == null || item.IsCategoryUpdating || item.Category == category) return;
            int version = _folderVersion;
            item.IsCategoryUpdating = true;
            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed || version != _folderVersion || !Screenshots.Contains(item)) return;
                await Task.Run(() => _repository.SetCategory(id, category));
                item.Category = category;
                if (!_isClosed && version == _folderVersion)
                {
                    if (string.Equals(_detailsFilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                        DetailsCategory.Text = category ?? "Not assigned";
                    ApplyCurrentSort();
                }
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                item.IsCategoryUpdating = false;
                _storageGate.Release();
            }
        }

        private void ScreenshotCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ScreenshotItem item })
                item.IsCardHovered = true;
        }

        private void ScreenshotCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ScreenshotItem item })
                item.IsCardHovered = false;
        }

        private void ScreenshotCard_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ScreenshotItem item })
            {
                item.IsCardHovered = false;
                item.IsFavoriteFocused = false;
            }
        }

        private void FavoriteButton_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ScreenshotItem item } button)
                item.IsFavoriteFocused = button.FocusState == FocusState.Keyboard;
        }

        private void FavoriteButton_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ScreenshotItem item })
                item.IsFavoriteFocused = false;
        }

        private void FavoriteButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int id }) await ToggleFavoriteAsync(id);
        }

        private async void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: int id }) await ToggleFavoriteAsync(id);
        }

        private async Task ToggleFavoriteAsync(int id)
        {
            var item = Screenshots.FirstOrDefault(x => x.Id == id);
            if (item == null || item.IsFavoriteUpdating) return;
            int version = _folderVersion;
            item.IsFavoriteUpdating = true;
            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed || version != _folderVersion || !Screenshots.Contains(item)) return;
                bool nextValue = !item.IsFavorite;
                await Task.Run(() => _repository.SetFavorite(id, nextValue));
                // Change the star only after a successful write; errors leave it unchanged.
                item.IsFavorite = nextValue;
                if (!_isClosed && version == _folderVersion) ApplyCurrentSort();
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                item.IsFavoriteUpdating = false;
                _storageGate.Release();
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

            for (DependencyObject? source = e.OriginalSource as DependencyObject;
                 source != null && !ReferenceEquals(source, sender);
                 source = VisualTreeHelper.GetParent(source))
            {
                if (source is Button) return;
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

                await RemoveScreenshotAsync(filePath);
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
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

        private async void SetScreenshotFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;
            StopWatchingFolder();
            int version = ++_folderVersion;
            _currentFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            SelectedFolderText.Text = _currentFolderPath;
            Screenshots.Clear();
            ApplyCurrentSort();
            CloseDetailsPanel();

            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed || version != _folderVersion) return;
                // Subscribe before scanning so new files cannot fall into a gap.
                StartWatchingFolder(_currentFolderPath);
                string folder = _currentFolderPath;
                var items = await Task.Run(() => _repository.SynchronizeFolder(folder));
                if (_isClosed || version != _folderVersion) return;
                foreach (var item in items) Screenshots.Add(item);
                ApplyCurrentSort();
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                _storageGate.Release();
            }
        }

        private bool IsCurrentFolder(string filePath) =>
            !_isClosed && string.Equals(Path.GetDirectoryName(Path.GetFullPath(filePath)),
                _currentFolderPath, StringComparison.OrdinalIgnoreCase);

        private async void AddScreenshot(string filePath)
        {
            await _storageGate.WaitAsync();
            try
            {
                if (!IsCurrentFolder(filePath) || !File.Exists(filePath)
                    || !IsSupportedImage(filePath)) return;
                int version = _folderVersion;
                if (Screenshots.Any(x => string.Equals(x.FilePath, filePath,
                    StringComparison.OrdinalIgnoreCase))) return;
                var screenshot = await Task.Run(() => _repository.GetOrAdd(filePath));
                if (version != _folderVersion || !IsCurrentFolder(filePath)
                    || !File.Exists(filePath)) return;
                Screenshots.Add(screenshot);
                ApplyCurrentSort();
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                _storageGate.Release();
            }
        }

        private static bool IsSupportedImage(string filePath) =>
            ScreenshotRepository.IsSupportedImage(filePath);

        private async Task RemoveScreenshotAsync(string filePath)
        {
            await _storageGate.WaitAsync();
            try
            {
                // A delete followed by recreation at the same path must not remove the new file.
                if (File.Exists(filePath)) return;
                await Task.Run(() => _repository.Delete(filePath));
                if (!IsCurrentFolder(filePath)) return;
                RemoveScreenshotCard(filePath);
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                _storageGate.Release();
            }
        }

        private void RemoveScreenshotCard(string filePath)
        {
            if (string.Equals(_detailsFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                CloseDetailsPanel();
            var item = Screenshots.FirstOrDefault(x => string.Equals(x.FilePath,
                filePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                Screenshots.Remove(item);
                ApplyCurrentSort();
            }
        }

        private async Task RenameScreenshotAsync(string oldPath, string newPath)
        {
            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed) return;
                if (IsSupportedImage(newPath))
                    await Task.Run(() => _repository.Rename(oldPath, newPath));
                else
                    await Task.Run(() => _repository.Delete(oldPath));
                if (IsCurrentFolder(oldPath)) RemoveScreenshotCard(oldPath);
                if (IsCurrentFolder(newPath)) RemoveScreenshotCard(newPath);
            }
            catch (Exception exception)
            {
                ShowStorageError(exception);
            }
            finally
            {
                _storageGate.Release();
            }
            if (IsSupportedImage(newPath)) await WaitAndAddScreenshotAsync(newPath);
        }

        private void ShowStorageError(Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            if (_isClosed) return;
            StorageInfoBar.Message = "Could not update the screenshot library. " + exception.Message;
            StorageInfoBar.IsOpen = true;
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

        private void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(async () =>
                await RenameScreenshotAsync(e.OldFullPath, e.FullPath));
        }

        private void Watcher_Deleted(object sender, FileSystemEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(async () =>
                await RemoveScreenshotAsync(e.FullPath));
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

        private void AllScreenshotsButton_Click(object sender, RoutedEventArgs e)
        {
            _showFavoritesOnly = false;
            _selectedCategory = null;
            UpdateGallerySection();
            ShowGalleryPage();
        }

        private void FavoritesButton_Click(object sender, RoutedEventArgs e)
        {
            _showFavoritesOnly = true;
            _selectedCategory = null;
            UpdateGallerySection();
            ShowGalleryPage();
        }

        private void CategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string category }) return;
            _showFavoritesOnly = false;
            _selectedCategory = category;
            UpdateGallerySection();
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
            _isClosed = true;
            ++_folderVersion;
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