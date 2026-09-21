using System.Net.Http;
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
using System.Collections.Generic;
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
        private string _searchQuery = string.Empty;
        private bool _isGalleryInitialized;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly SettingsService _settingsService;
        private readonly ScreenshotRepository _repository;
        private readonly OcrQueueService _ocrQueue;
        private readonly AiQueueService _aiQueue;
        private readonly HashSet<int> _automaticAiCandidates = new();
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
            var processingLog = new ProcessingLog(Path.Combine(
                ApplicationData.Current.LocalFolder.Path, "processing.log"));
            InitializeSemanticSearch(processingLog);
            InitializeActivity(processingLog);
            _ocrQueue = new OcrQueueService(_repository, processingLog);
            _ocrQueue.StateChanged += OcrQueue_StateChanged;
            _aiQueue = new AiQueueService(_repository, processingLog);
            _aiQueue.StateChanged += AiQueue_StateChanged;
            SettingsView.UsageRepository = _repository;
            SettingsView.Loaded += (_, _) => SettingsView.RefreshAiUsage();
            SettingsView.AiSettingsChanged += SettingsView_AiSettingsChanged;
            try { _aiQueue.Configure(_settingsService.GetAiConfiguration()); }
            catch { ShowStorageError(new InvalidOperationException("Could not load the saved AI key. Check Settings.")); }


            SettingsView.ParentWindow =
                this;

            SettingsView.ScreenshotFolderChanged +=
                SettingsView_ScreenshotFolderChanged;

            SettingsView.HotkeyChanged +=
                SettingsView_HotkeyChanged;

            InitializeGlobalHotkey();

            _isGalleryInitialized = true;
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
                && (_selectedCategory == null || x.Category == _selectedCategory)
                && MatchesSearch(x));
            var desired = SemanticMode && _searchQuery.Length > 0
                ? filtered.OrderByDescending(MatchesText)
                    .ThenBy(x => MatchesSemantic(x) && _semanticScores.TryGetValue(x.Id, out var distance)
                        ? distance : double.MaxValue)
                    .ThenBy(x => x.Id).ToList()
                : (_sortNewestFirst
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
            bool hasSearch = _searchQuery.Length > 0;
            EmptyGalleryText.Text = hasSearch
                ? "No matching screenshots in this section."
                : _showFavoritesOnly
                    ? "No favorite screenshots yet."
                    : $"No screenshots in {_selectedCategory} yet.";
            EmptyGalleryText.Visibility = (hasSearch || _showFavoritesOnly || _selectedCategory != null)
                && VisibleScreenshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool MatchesSearch(ScreenshotItem item) =>
            MatchesText(item) || (SemanticMode && MatchesSemantic(item));

        // Text matches stay available immediately, even without a current vector or API access.
        private bool MatchesText(ScreenshotItem item) =>
            _searchQuery.Length == 0
            || item.FileName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
            || (item.Category?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.OcrText?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Description?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Tags?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false);

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox) return;
            _searchQuery = textBox.Text.Trim();
            // XAML can raise TextChanged before the rest of the named controls exist.
            if (!_isGalleryInitialized) return;
            InvalidateSemanticResults();
            ApplyCurrentSort();
        }

        private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && SemanticMode)
            {
                e.Handled = true;
                RunSemanticSearch();
                return;
            }
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            SearchTextBox.Text = string.Empty;
            e.Handled = true;
        }

        private void UpdateGallerySection()
        {
            InvalidateSemanticResults();
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
            if (item == null || item.IsCategoryUpdating || (item.Category == category && item.CategoryIsManual)) return;
            int version = _folderVersion;
            item.IsCategoryUpdating = true;
            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed || version != _folderVersion || !Screenshots.Contains(item)) return;
                await Task.Run(() => _repository.SetCategory(id, category));
                item.Category = category;
                item.CategoryIsManual = true;
                if (!_isClosed && version == _folderVersion)
                {
                    if (string.Equals(_detailsFilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                        UpdateAiDetails(item);
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

            UpdateOcrDetails(screenshot);
            UpdateAiDetails(screenshot);
        }

        private void SettingsView_AiSettingsChanged(AiConfiguration configuration)
        {
            _automaticAiCandidates.Clear();
            InvalidateSemanticResults(cancelIndex: true);
            _cachedSemanticQuery = null;
            _cachedSemanticVector = null;
            _aiQueue.Configure(configuration);
            ApplyCurrentSort();
            var item = Screenshots.FirstOrDefault(x => x.FilePath == _detailsFilePath);
            if (item != null) UpdateAiDetails(item);
        }

        private void UpdateAiDetails(ScreenshotItem item)
        {
            DetailsCategory.Text = item.Category ?? "Not assigned";
            DetailsCategorySource.Text = item.CategoryIsManual
                ? "Manual category: AI will not change it."
                : "AI category updates allowed.";
            DetailsDescription.Text = item.Description ?? "No description yet";
            DetailsTags.Text = string.IsNullOrWhiteSpace(item.Tags) ? "No tags yet" : item.Tags;
            bool busy = _aiQueue.IsPending(item.Id);
            DetailsAiStatus.Text = "Status: " + (busy && item.AiStatus != "Processing" ? "Pending" : item.AiStatus);
            DetailsAiError.Text = item.AiError ?? string.Empty;
            DetailsAiError.Visibility = string.IsNullOrWhiteSpace(item.AiError) ? Visibility.Collapsed : Visibility.Visible;
            AnalyzeAiButton.IsEnabled = _aiQueue.CanAnalyze && !busy;
            DetailsAiHint.Text = _aiQueue.CanAnalyze
                ? "Sends this image and recognized text to OpenAI. Each run uses your API balance."
                : "Enable AI analysis and save an API key in Settings.";
        }

        private void AnalyzeAiButton_Click(object sender, RoutedEventArgs e)
        {
            var item = Screenshots.FirstOrDefault(x => x.FilePath == _detailsFilePath);
            if (item == null) return;
            _automaticAiCandidates.Remove(item.Id);
            if (_aiQueue.Enqueue(item.Id)) UpdateAiDetails(item);
        }

        private void AiQueue_StateChanged(int id)
        {
            _dispatcherQueue.TryEnqueue(async () =>
            {
                await _storageGate.WaitAsync();
                try
                {
                    if (_isClosed) return;
                    // Reload current database values, including any manual edit made in flight.
                    SettingsView.RefreshAiUsage();
                    var state = await Task.Run(() => _repository.GetAiState(id));
                    if (state == null || _isClosed) return;
                    var item = Screenshots.FirstOrDefault(x => x.Id == id);
                    if (item == null) return;
                    item.Description = state.Description;
                    item.Tags = state.Tags;
                    item.Category = state.Category;
                    item.CategoryIsManual = state.CategoryIsManual;
                    item.AiStatus = state.Status;
                    item.AiError = state.Error;
                    if (item.FilePath == _detailsFilePath) UpdateAiDetails(item);
                    ApplyCurrentSort();
                }
                catch (Exception exception) { ShowStorageError(exception); }
                finally { _storageGate.Release(); }
            });
        }

        private async void AllowAiCategoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem { Tag: int id }) return;
            await _storageGate.WaitAsync();
            try
            {
                if (_isClosed) return;
                await Task.Run(() => _repository.AllowAiCategory(id));
                var item = Screenshots.FirstOrDefault(x => x.Id == id);
                if (item == null || _isClosed) return;
                item.CategoryIsManual = false;
                if (item.FilePath == _detailsFilePath) UpdateAiDetails(item);
            }
            catch (Exception exception) { ShowStorageError(exception); }
            finally { _storageGate.Release(); }
        }

        private void UpdateOcrDetails(ScreenshotItem screenshot)
        {
            DetailsOcrStatus.Text = $"Status: {screenshot.OcrStatus}";
            DetailsOcrError.Text = screenshot.OcrError ?? string.Empty;
            DetailsOcrError.Visibility = string.IsNullOrWhiteSpace(screenshot.OcrError)
                ? Visibility.Collapsed : Visibility.Visible;
            DetailsOcrText.Text = !string.IsNullOrWhiteSpace(screenshot.OcrText)
                ? screenshot.OcrText
                : screenshot.OcrStatus == "Processed" ? "No text found in this image."
                : screenshot.OcrStatus == "Failed" ? "Text recognition failed. You can retry below."
                : "Waiting for text recognition...";
            RetryOcrButton.IsEnabled = screenshot.OcrStatus is not ("Pending" or "Processing");
            CopyOcrButton.IsEnabled = !string.IsNullOrWhiteSpace(screenshot.OcrText);
        }

        private void OcrQueue_StateChanged(OcrJobState state)
        {
            _dispatcherQueue.TryEnqueue(async () =>
            {
                // Wait for folder loading/renaming to finish before applying a notification.
                await _storageGate.WaitAsync();
                try
                {
                    if (_isClosed) return;
                    var item = Screenshots.FirstOrDefault(x => x.Id == state.Id
                        && string.Equals(x.FilePath, state.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (item == null) return;
                    item.OcrStatus = state.Status;
                    item.OcrText = state.Text;
                    item.OcrError = state.Error;
                    item.IsProcessed = state.Status == "Processed";
                    if ((state.Status is "Processed" or "Failed") && _automaticAiCandidates.Remove(item.Id))
                        _aiQueue.Enqueue(item.Id, automatic: true);
                    if (string.Equals(_detailsFilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                        UpdateOcrDetails(item);
                    if (_searchQuery.Length > 0) ApplyCurrentSort();
                }
                catch (Exception exception) { ShowStorageError(exception); }
                finally { _storageGate.Release(); }
            });
        }

        private void RetryOcrButton_Click(object sender, RoutedEventArgs e)
        {
            var item = Screenshots.FirstOrDefault(x => string.Equals(x.FilePath,
                _detailsFilePath, StringComparison.OrdinalIgnoreCase));
            if (item == null || !_ocrQueue.Enqueue(item.Id, force: true)) return;
            item.OcrStatus = "Pending";
            item.OcrError = null;
            UpdateOcrDetails(item);
        }

        private void CopyOcrButton_Click(object sender, RoutedEventArgs e)
        {
            var item = Screenshots.FirstOrDefault(x => string.Equals(x.FilePath,
                _detailsFilePath, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(item?.OcrText)) return;
            try
            {
                var data = new DataPackage();
                data.SetText(item.OcrText);
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }
            catch (Exception exception) { ShowStorageError(exception); }
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
            InvalidateSemanticResults(cancelIndex: true);
            int version = ++_folderVersion;
            _automaticAiCandidates.Clear();
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
                foreach (var item in items)
                    if (item.OcrStatus is "Pending" or "Processing") _ocrQueue.Enqueue(item.Id);
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
                if (_aiQueue.AutomaticEnabled && screenshot.WasAddedToLibrary && screenshot.AiStatus == "NotProcessed")
                {
                    if (screenshot.OcrStatus is "Processed" or "Failed")
                        _aiQueue.Enqueue(screenshot.Id, automatic: true);
                    else _automaticAiCandidates.Add(screenshot.Id);
                }
                if (screenshot.OcrStatus is "Pending" or "Processing") _ocrQueue.Enqueue(screenshot.Id);
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
                _automaticAiCandidates.Remove(item.Id);
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
            HideActivity();
            CloseDetailsPanel();

            GalleryPage.Visibility =
                Visibility.Collapsed;

            SettingsPageContainer.Visibility =
                Visibility.Visible;
        }

        private void ShowGalleryPage()
        {
            HideActivity();
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
            _activityTimer?.Stop();
            _semanticCancellation?.Cancel();
            _ocrQueue.StateChanged -= OcrQueue_StateChanged;
            _ocrQueue.Stop();
            SettingsView.AiSettingsChanged -= SettingsView_AiSettingsChanged;
            _aiQueue.StateChanged -= AiQueue_StateChanged;
            _aiQueue.Stop();
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

        private SemanticVectorStore? _vectors;
        private ProcessingLog? _semanticLog;
        private EmbeddingService? _embeddings;
        private CancellationTokenSource? _semanticCancellation;
        private bool _semanticBusy;
        private bool _indexing;
        private int _semanticGeneration;
        private Dictionary<int, double> _semanticScores = new();
        private Dictionary<int, string> _semanticHashes = new();
        private string? _cachedSemanticQuery;
        private float[]? _cachedSemanticVector;
        private bool SemanticMode => SemanticModeCheckBox?.IsChecked == true;

        private void InitializeSemanticSearch(ProcessingLog log)
        {
            _semanticLog = log;
            _vectors = new SemanticVectorStore(Path.Combine(ApplicationData.Current.LocalFolder.Path,
                "semantic-small-512-v1.db"));
            _embeddings = new EmbeddingService(_repository);
        }

        private static SemanticDocument? ToSemanticDocument(ScreenshotItem item) =>
            SemanticDocument.Create(item.Id, item.FilePath, item.FileName, item.Category,
                item.Description, item.Tags, item.OcrText);

        private List<SemanticDocument> SemanticDocuments(bool activeSection) => Screenshots
            .Where(x => !activeSection || ((!_showFavoritesOnly || x.IsFavorite)
                && (_selectedCategory == null || x.Category == _selectedCategory)))
            .Select(ToSemanticDocument).OfType<SemanticDocument>().ToList();

        private bool MatchesSemantic(ScreenshotItem item)
        {
            if (_searchQuery.Length == 0) return true;
            return _semanticScores.ContainsKey(item.Id)
                && _semanticHashes.TryGetValue(item.Id, out var hash)
                && ToSemanticDocument(item)?.Fingerprint == hash;
        }

        private void InvalidateSemanticResults(bool cancelIndex = false)
        {
            _semanticGeneration++;
            _semanticScores.Clear();
            _semanticHashes.Clear();
            if (!_indexing || cancelIndex) _semanticCancellation?.Cancel();
            if (SemanticMode && !_semanticBusy)
                SemanticStatusText.Text = "Text matches appear immediately. Press Enter to add semantic matches. Update the index after changing screenshot text.";
        }

        private void SemanticMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isGalleryInitialized) return;
            InvalidateSemanticResults();
            SortButton.IsEnabled = !SemanticMode;
            SemanticSearchButton.IsEnabled = SemanticMode && !_semanticBusy;
            ApplyCurrentSort();
        }

        private void CancelSemanticButton_Click(object sender, RoutedEventArgs e) => _semanticCancellation?.Cancel();
        private void SemanticSearchButton_Click(object sender, RoutedEventArgs e) => RunSemanticSearch();
        private void IndexSemanticButton_Click(object sender, RoutedEventArgs e) => RunSemanticIndex();

        private CancellationToken BeginSemanticWork(bool indexing)
        {
            _semanticCancellation?.Dispose();
            _semanticCancellation = new CancellationTokenSource();
            _semanticBusy = true;
            _indexing = indexing;
            IndexSemanticButton.IsEnabled = false;
            SemanticSearchButton.IsEnabled = false;
            CancelSemanticButton.IsEnabled = true;
            return _semanticCancellation.Token;
        }

        private void EndSemanticWork()
        {
            _semanticBusy = false;
            _indexing = false;
            if (_isClosed) return;
            IndexSemanticButton.IsEnabled = true;
            SemanticSearchButton.IsEnabled = SemanticMode;
            CancelSemanticButton.IsEnabled = false;
            SettingsView.RefreshAiUsage();
        }

        private async void RunSemanticIndex()
        {
            if (_semanticBusy || _vectors == null || _embeddings == null) return;
            var documents = SemanticDocuments(false);
            if (documents.Count == 0)
            {
                SemanticStatusText.Text = "No text to index. Run OCR or AI analysis on screenshots first.";
                return;
            }
            var token = BeginSemanticWork(true);
            int saved = 0, skipped = 0;
            try
            {
                var config = _settingsService.GetAiConfiguration();
                if (!config.CanAnalyze) throw new InvalidOperationException("Enable AI and save an API key in Settings first.");
                SemanticStatusText.Text = $"Checking {documents.Count} screenshots…";
                await Task.Run(async () =>
                {
                    // Checks the extension before any paid work.
                    var current = _vectors.GetCurrentIds(documents);
                    foreach (var original in documents)
                    {
                        token.ThrowIfCancellationRequested();
                        var document = _repository.GetSemanticDocument(original.Id);
                        if (document == null || !File.Exists(document.FilePath)) { skipped++; continue; }
                        if (current.Contains(document.Id) && original.Fingerprint == document.Fingerprint)
                        { skipped++; continue; }
                        var vector = await _embeddings.EmbedAsync(document.Text, config, token);
                        token.ThrowIfCancellationRequested();
                        var latest = _repository.GetSemanticDocument(document.Id);
                        if (latest?.Fingerprint == document.Fingerprint && File.Exists(latest.FilePath))
                        {
                            _vectors.Save(document, vector);
                            saved++;
                        }
                        else skipped++;
                        int done = saved + skipped;
                        _dispatcherQueue.TryEnqueue(() =>
                        {
                            if (!_isClosed && _semanticBusy && _indexing && !token.IsCancellationRequested)
                                SemanticStatusText.Text = $"Indexing: {done}/{documents.Count}. You can cancel; completed items stay saved.";
                        });
                    }
                }, token);
                _semanticLog?.Write($"Semantic index: {saved} saved, {skipped} skipped");
                if (!_isClosed) SemanticStatusText.Text = $"Index ready: {saved} updated, {skipped} unchanged or skipped. Enable Search by meaning and enter a phrase.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _semanticLog?.Write($"Semantic index cancelled: {saved} saved");
                if (!_isClosed) SemanticStatusText.Text = $"Indexing stopped; {saved} items saved. Run Update index to continue.";
            }
            catch (OperationCanceledException)
            {
                if (!_isClosed) SemanticStatusText.Text = $"Request timed out; {saved} items saved. Run Update index to continue.";
            }
            catch (Exception exception)
            {
                _semanticLog?.Write($"Semantic index failed: {exception.GetType().Name}");
                if (!_isClosed) SemanticStatusText.Text = $"{saved} items saved. " + SemanticError(exception);
            }
            finally { EndSemanticWork(); }
        }

        private async void RunSemanticSearch()
        {
            if (_semanticBusy || !SemanticMode || _vectors == null || _embeddings == null) return;
            string query = _searchQuery;
            if (string.IsNullOrWhiteSpace(query)) return;
            if (System.Text.Encoding.UTF8.GetByteCount(query) > 6000)
            { SemanticStatusText.Text = "Please use a shorter search phrase."; return; }
            var documents = SemanticDocuments(true);
            int generation = ++_semanticGeneration;
            var token = BeginSemanticWork(false);
            _semanticScores.Clear();
            _semanticHashes.Clear();
            ApplyCurrentSort();
            try
            {
                SemanticStatusText.Text = "Text matches are shown. Searching for additional semantic matches…";
                var config = _settingsService.GetAiConfiguration();
                if (!config.CanAnalyze) throw new InvalidOperationException("Enable AI and save an API key in Settings first.");
                var currentIds = await Task.Run(() => _vectors.GetCurrentIds(documents), token);
                var eligible = documents.Where(x => currentIds.Contains(x.Id)).ToList();
                if (eligible.Count == 0)
                    throw new InvalidOperationException("Showing text matches only: this section has no current indexed screenshots. Run Update folder index to enable semantic matches.");
                float[] vector;
                if (_cachedSemanticQuery == query && _cachedSemanticVector != null) vector = _cachedSemanticVector;
                else
                {
                    vector = await Task.Run(() => _embeddings.EmbedAsync(query, config, token), token);
                    token.ThrowIfCancellationRequested();
                    _cachedSemanticQuery = query;
                    _cachedSemanticVector = vector;
                }
                var scores = await Task.Run(() => _vectors.Search(vector, eligible), token);
                token.ThrowIfCancellationRequested();
                if (_isClosed || generation != _semanticGeneration) return;
                _semanticLog?.Write($"Semantic search: {scores.Count} results from {eligible.Count} indexed items");
                _semanticScores = scores;
                _semanticHashes = eligible.ToDictionary(x => x.Id, x => x.Fingerprint);
                ApplyCurrentSort();
                SemanticStatusText.Text = $"{VisibleScreenshots.Count} results: text matches first, followed by semantic matches. {documents.Count - eligible.Count} screenshots need indexing. Semantic results may include weak matches.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!_isClosed) SemanticStatusText.Text = "Search cancelled. Press Enter to search again.";
            }
            catch (OperationCanceledException)
            {
                if (!_isClosed) SemanticStatusText.Text = "Search request timed out. Try again later.";
            }
            catch (Exception exception)
            {
                _semanticLog?.Write($"Semantic search failed: {exception.GetType().Name}");
                if (!_isClosed && generation == _semanticGeneration) SemanticStatusText.Text = SemanticError(exception);
            }
            finally { EndSemanticWork(); }
        }

        private static string SemanticError(Exception exception) => exception switch
        {
            InvalidOperationException => exception.Message,
            HttpRequestException => "Cannot reach OpenAI. Check your internet connection.",
            _ => "Semantic search failed. Restore packages and rebuild for x64, then try again."
        };

        private DispatcherTimer? _activityTimer;
        private ProcessingLog? _activityLog;
        private bool _activityRefreshing;
        private readonly ObservableCollection<ActivityItem> _activityItems = new();

        private void InitializeActivity(ProcessingLog log)
        {
            _activityLog = log;
            ActivityList.ItemsSource = _activityItems;
            _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _activityTimer.Tick += (_, _) => RefreshActivity();
        }

        private void HideActivity()
        {
            _activityTimer?.Stop();
            ActivityPage.Visibility = Visibility.Collapsed;
            ActivityButton.Style = null;
        }

        private void ActivityButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDetailsPanel();
            GalleryPage.Visibility = Visibility.Collapsed;
            SettingsPageContainer.Visibility = Visibility.Collapsed;
            ActivityPage.Visibility = Visibility.Visible;
            ActivityButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            foreach (var button in new[] { AllScreenshotsButton, FavoritesButton, GamingButton,
                ProgrammingButton, DocumentsButton, OtherButton }) button.Style = null;
            ActivityMessageText.Text = string.Empty;
            _activityTimer?.Start();
            RefreshActivity();
        }

        private void ActivityRefresh_Click(object sender, RoutedEventArgs e) => RefreshActivity();
        private void ActivityFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_activityTimer != null) RefreshActivity();
        }

        private async void RefreshActivity()
        {
            if (_isClosed || _activityRefreshing || ActivityPage.Visibility != Visibility.Visible) return;
            _activityRefreshing = true;
            string folder = _currentFolderPath ?? string.Empty;
            bool failures = ActivityFailuresOnly.IsChecked == true;
            try
            {
                var result = await Task.Run(() =>
                {
                    var activity = _repository.GetActivity(folder, failures);
                    string usage = _repository.GetAiUsageSummary();
                    string log;
                    try { log = _activityLog?.ReadRecent() ?? "No log entries yet."; }
                    catch { log = "Could not read the processing log."; }
                    return (activity, usage, log);
                });
                if (_isClosed || ActivityPage.Visibility != Visibility.Visible
                    || folder != (_currentFolderPath ?? string.Empty)
                    || failures != (ActivityFailuresOnly.IsChecked == true)) return;
                var ocrIds = _ocrQueue.PendingIds.ToHashSet();
                var aiIds = _aiQueue.PendingIds.ToHashSet();
                int ocrActive = _ocrQueue.ActiveId;
                int aiActive = _aiQueue.ActiveId;
                int ocrRunning = ocrIds.Contains(ocrActive) ? 1 : 0;
                int aiRunning = aiIds.Contains(aiActive) ? 1 : 0;
                ActivityFolderText.Text = folder.Length == 0 ? "Select a screenshot folder in Settings." : folder;
                ActivityQueueText.Text = $"Live queues (whole app): OCR {ocrIds.Count - ocrRunning} waiting / {ocrRunning} running · AI {aiIds.Count - aiRunning} waiting / {aiRunning} running"
                    + (_semanticBusy ? (_indexing ? " · Semantic indexing is running" : " · Semantic search is running") : "");
                ActivityProgress.Visibility = ocrIds.Count + aiIds.Count > 0 || _semanticBusy
                    ? Visibility.Visible : Visibility.Collapsed;
                ActivitySummaryText.Text = result.activity.Summary;
                ActivitySemanticText.Text = "Semantic search / index: " + (string.IsNullOrWhiteSpace(SemanticStatusText.Text)
                    ? "No activity in this session." : SemanticStatusText.Text);
                ActivityUsageText.Text = "API usage across the app — daily limit: " + _settingsService.AiDailyLimit
                    + "\n" + result.usage;
                ActivityLogText.Text = result.log;
                var rows = result.activity.Items.Select(item => item with
                {
                    OcrStatus = ocrIds.Contains(item.Id) ? (item.Id == ocrActive ? "Processing" : "Queued") : item.OcrStatus,
                    AiStatus = aiIds.Contains(item.Id) ? (item.Id == aiActive ? "Processing" : "Queued") : item.AiStatus,
                    OcrError = ocrIds.Contains(item.Id) ? null : item.OcrError,
                    AiError = aiIds.Contains(item.Id) ? null : item.AiError,
                    CanRetryOcr = item.OcrStatus == "Failed" && !ocrIds.Contains(item.Id),
                    CanRetryAi = (item.AiStatus is "Failed" or "Cancelled") && !aiIds.Contains(item.Id) && _aiQueue.CanAnalyze
                }).ToList();
                // Update changed rows without recreating the entire list on every timer tick.
                var ids = rows.Select(x => x.Id).ToHashSet();
                for (int i = _activityItems.Count - 1; i >= 0; i--)
                    if (!ids.Contains(_activityItems[i].Id)) _activityItems.RemoveAt(i);
                for (int i = 0; i < rows.Count; i++)
                {
                    int existing = -1;
                    for (int j = i; j < _activityItems.Count; j++)
                        if (_activityItems[j].Id == rows[i].Id) { existing = j; break; }
                    if (existing < 0) _activityItems.Insert(i, rows[i]);
                    else
                    {
                        if (existing != i) _activityItems.Move(existing, i);
                        if (_activityItems[i] != rows[i]) _activityItems[i] = rows[i];
                    }
                }
                ActivityCountText.Text = rows.Count == 0
                    ? (failures ? "No failed or cancelled tasks in this folder." : "No screenshots in this folder.")
                    : $"Showing {rows.Count} screenshots (up to 200; active and failed first). Folder total: {result.activity.Total}. Auto-refresh: 2 s.";
            }
            catch (Exception exception)
            {
                _activityLog?.Write($"Activity refresh failed: {exception.GetType().Name}");
                if (!_isClosed) ActivityMessageText.Text = "Could not refresh processing activity. Try Refresh again.";
            }
            finally { _activityRefreshing = false; }
        }

        private void ActivityRetryOcr_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int id }) RetryActivity(id, false);
        }
        private void ActivityRetryAi_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int id }) RetryActivity(id, true);
        }
        private async void RetryActivity(int id, bool ai)
        {
            try
            {
                bool allowed = await Task.Run(() =>
                {
                    if (ai)
                    {
                        var state = _repository.GetAiState(id);
                        return state != null && (state.Status is "Failed" or "Cancelled") && File.Exists(state.FilePath);
                    }
                    var ocr = _repository.GetOcrState(id);
                    return ocr?.Status == "Failed" && File.Exists(ocr.FilePath);
                });
                if (_isClosed) return;
                if (!allowed)
                {
                    ActivityMessageText.Text = "Task state changed or the file is no longer available. Refresh the list.";
                    RefreshActivity();
                    return;
                }
                bool queued = ai ? _aiQueue.Enqueue(id) : _ocrQueue.Enqueue(id, force: true);
                ActivityMessageText.Text = queued ? "Retry queued."
                    : ai && !_aiQueue.CanAnalyze ? "Enable AI and save an API key in Settings first." : "This task is already queued.";
                RefreshActivity();
            }
            catch (Exception exception)
            {
                _activityLog?.Write($"Activity retry failed: {exception.GetType().Name}");
                if (!_isClosed) ActivityMessageText.Text = "Could not queue the retry.";
            }
        }

    }
}