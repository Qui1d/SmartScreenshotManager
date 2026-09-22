using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SmartScreenshotManager.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace SmartScreenshotManager.Views
{
    public sealed class CaptureGrid : Grid
    {
        public CaptureGrid()
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Cross);
        }
    }

    public sealed partial class SnippingWindow : Window
    {
        private const int GwlStyle = -16;

        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsMinimizeBox = 0x00020000L;
        private const long WsMaximizeBox = 0x00010000L;
        private const long WsSysMenu = 0x00080000L;

        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpShowWindow = 0x0040;

        private static readonly IntPtr HwndTopmost =
            new IntPtr(-1);

        private readonly ScreenshotCaptureService _captureService;
        private readonly string _destinationFolder;

        private ScreenshotCaptureService.ScreenCaptureData? _screenCapture;

        private Point _selectionStart;

        private bool _isSelecting;
        private bool _finished;

        private string? _previewFilePath;

        public bool CopyRequested { get; private set; }
        public bool ClipboardCopied { get; private set; }
        public event Action<string>? SnipCompleted;
        public event Action? SnipCancelled;
        public event Action? SnipFailed;

        public SnippingWindow(
            string destinationFolder)
        {
            InitializeComponent();

            _destinationFolder =
                destinationFolder;

            _captureService =
                new ScreenshotCaptureService();

            Closed +=
                SnippingWindow_Closed;
        }

        public async Task PrepareAsync()
        {
            _screenCapture =
                _captureService.CapturePrimaryScreen();

            string temporaryFolder =
                ApplicationData
                    .Current
                    .TemporaryFolder
                    .Path;

            string previewFileName =
                $"SnippingPreview_{Guid.NewGuid():N}.png";

            StorageFile previewFile =
                await _captureService.SavePngAsync(
                    _screenCapture,
                    temporaryFolder,
                    previewFileName);

            _previewFilePath =
                previewFile.Path;

            var bitmap =
                new BitmapImage
                {
                    CreateOptions =
                        BitmapCreateOptions.IgnoreImageCache,

                    UriSource =
                        new Uri(
                            previewFile.Path)
                };

            ScreenImage.Source =
                bitmap;

            ConfigureWindow();
        }

        private void ConfigureWindow()
        {
            IntPtr hwnd =
                WindowNative.GetWindowHandle(
                    this);

            long style =
                GetWindowLongPtr(
                    hwnd,
                    GwlStyle)
                    .ToInt64();

            style &= ~WsCaption;
            style &= ~WsThickFrame;
            style &= ~WsMinimizeBox;
            style &= ~WsMaximizeBox;
            style &= ~WsSysMenu;

            SetWindowLongPtr(
                hwnd,
                GwlStyle,
                new IntPtr(style));

            if (_screenCapture == null)
                return;

            SetWindowPos(
                hwnd,
                HwndTopmost,
                0,
                0,
                _screenCapture.Width,
                _screenCapture.Height,
                SwpFrameChanged |
                SwpShowWindow);
        }

        // =========================
        // Selection
        // =========================

        private void RootGrid_PointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (_finished || _isSelecting ||
                !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
                return;

            _selectionStart =
                e.GetCurrentPoint(
                    RootGrid)
                    .Position;

            _isSelecting =
                true;

            RootGrid.CapturePointer(
                e.Pointer);

            FullDim.Visibility =
                Visibility.Collapsed;

            SelectionRectangle.Visibility =
                Visibility.Visible;

            UpdateSelection(
                _selectionStart);
        }

        private void RootGrid_PointerMoved(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (!_isSelecting)
                return;

            Point current =
                e.GetCurrentPoint(
                    RootGrid)
                    .Position;

            UpdateSelection(
                current);
        }

        private async void RootGrid_PointerReleased(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (!_isSelecting)
                return;

            _isSelecting =
                false;

            RootGrid.ReleasePointerCapture(
                e.Pointer);

            Point end =
                e.GetCurrentPoint(
                    RootGrid)
                    .Position;

            Rect selection =
                GetSelectionRectangle(
                    _selectionStart,
                    end);

            if (selection.Width < 5 ||
                selection.Height < 5)
            {
                ResetSelection();
                return;
            }

            try { await CompleteSnipAsync(selection); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                _finished = true;
                SnipFailed?.Invoke();
                Close();
            }
        }

        private void UpdateSelection(
            Point current)
        {
            Rect selection =
                GetSelectionRectangle(
                    _selectionStart,
                    current);

            Canvas.SetLeft(
                SelectionRectangle,
                selection.X);

            Canvas.SetTop(
                SelectionRectangle,
                selection.Y);

            SelectionRectangle.Width =
                selection.Width;

            SelectionRectangle.Height =
                selection.Height;

            UpdateDimming(selection);
            UpdateSelectionSize(selection);
        }

        private void UpdateSelectionSize(Rect selection)
        {
            if (_screenCapture == null || RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
                return;

            var pixels = GetPixelSelection(selection);
            SelectionSizeText.Text = $"{pixels.Width} × {pixels.Height} px";
            SelectionSizeBadge.Visibility = Visibility.Visible;
            SelectionSizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = SelectionSizeBadge.DesiredSize;
            double top = selection.Y - size.Height - 8;
            if (top < 0) top = selection.Bottom + 8;
            Canvas.SetLeft(SelectionSizeBadge,
                Math.Clamp(selection.X, 0, Math.Max(0, RootGrid.ActualWidth - size.Width)));
            Canvas.SetTop(SelectionSizeBadge,
                Math.Clamp(top, 0, Math.Max(0, RootGrid.ActualHeight - size.Height)));
        }

        private (int X, int Y, int Width, int Height) GetPixelSelection(Rect selection)
        {
            var capture = _screenCapture!;
            double scaleX = capture.Width / RootGrid.ActualWidth;
            double scaleY = capture.Height / RootGrid.ActualHeight;
            int x = Math.Clamp((int)Math.Round(selection.X * scaleX), 0, capture.Width - 1);
            int y = Math.Clamp((int)Math.Round(selection.Y * scaleY), 0, capture.Height - 1);
            int width = Math.Clamp((int)Math.Round(selection.Width * scaleX), 1, capture.Width - x);
            int height = Math.Clamp((int)Math.Round(selection.Height * scaleY), 1, capture.Height - y);
            return (x, y, width, height);
        }

        private void UpdateDimming(
            Rect selection)
        {
            double totalWidth =
                RootGrid.ActualWidth;

            double totalHeight =
                RootGrid.ActualHeight;

            Canvas.SetLeft(
                TopDim,
                0);

            Canvas.SetTop(
                TopDim,
                0);

            TopDim.Width =
                totalWidth;

            TopDim.Height =
                selection.Y;

            Canvas.SetLeft(
                BottomDim,
                0);

            Canvas.SetTop(
                BottomDim,
                selection.Y +
                selection.Height);

            BottomDim.Width =
                totalWidth;

            BottomDim.Height =
                Math.Max(
                    0,
                    totalHeight -
                    selection.Y -
                    selection.Height);

            Canvas.SetLeft(
                LeftDim,
                0);

            Canvas.SetTop(
                LeftDim,
                selection.Y);

            LeftDim.Width =
                selection.X;

            LeftDim.Height =
                selection.Height;

            Canvas.SetLeft(
                RightDim,
                selection.X +
                selection.Width);

            Canvas.SetTop(
                RightDim,
                selection.Y);

            RightDim.Width =
                Math.Max(
                    0,
                    totalWidth -
                    selection.X -
                    selection.Width);

            RightDim.Height =
                selection.Height;
        }

        private Rect GetSelectionRectangle(
            Point start,
            Point end)
        {
            start = new Point(Math.Clamp(start.X, 0, RootGrid.ActualWidth),
                Math.Clamp(start.Y, 0, RootGrid.ActualHeight));
            end = new Point(Math.Clamp(end.X, 0, RootGrid.ActualWidth),
                Math.Clamp(end.Y, 0, RootGrid.ActualHeight));

            double x =
                Math.Min(
                    start.X,
                    end.X);

            double y =
                Math.Min(
                    start.Y,
                    end.Y);

            double width =
                Math.Abs(
                    start.X -
                    end.X);

            double height =
                Math.Abs(
                    start.Y -
                    end.Y);

            return new Rect(
                x,
                y,
                width,
                height);
        }

        private void ResetSelection()
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            SelectionRectangle.Visibility =
                Visibility.Collapsed;

            FullDim.Visibility =
                Visibility.Visible;

            TopDim.Width = 0;
            TopDim.Height = 0;

            BottomDim.Width = 0;
            BottomDim.Height = 0;

            LeftDim.Width = 0;
            LeftDim.Height = 0;

            RightDim.Width = 0;
            RightDim.Height = 0;
        }

        // =========================
        // Save screenshot
        // =========================

        private async Task CompleteSnipAsync(
            Rect selection)
        {
            if (_screenCapture == null)
                return;

            if (RootGrid.ActualWidth <= 0 ||
                RootGrid.ActualHeight <= 0)
            {
                return;
            }

            _finished =
                true;

            var pixels = GetPixelSelection(selection);
            var cropped = _captureService.Crop(_screenCapture,
                pixels.X, pixels.Y, pixels.Width, pixels.Height);

            string fileName =
                $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

            StorageFile file =
                await _captureService.SavePngAsync(
                    cropped,
                    _destinationFolder,
                    fileName);

            /*
             * Automatically copy the newly created screenshot
             * to the Windows clipboard.
             */
            CopyRequested = new SettingsService().AutoCopyScreenshot;
            ClipboardCopied = CopyRequested && CopyScreenshotToClipboard(file);

            SnipCompleted?.Invoke(
                file.Path);

            Close();
        }

        private static bool CopyScreenshotToClipboard(
            StorageFile file)
        {
            try
            {
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
                return true;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    exception);
                return false;
            }
        }

        // =========================
        // Escape
        // =========================

        private void EscapeAccelerator_Invoked(
            KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled =
                true;

            CancelSnip();
        }

        private void CancelSnip()
        {
            if (_finished)
                return;

            _finished =
                true;

            SnipCancelled?.Invoke();

            Close();
        }

        // =========================
        // Cleanup
        // =========================

        private void SnippingWindow_Closed(
            object sender,
            WindowEventArgs args)
        {
            if (!_finished)
            {
                _finished =
                    true;

                SnipCancelled?.Invoke();
            }

            if (!string.IsNullOrWhiteSpace(
                    _previewFilePath))
            {
                try
                {
                    ScreenImage.Source =
                        null;

                    File.Delete(
                        _previewFilePath);
                }
                catch
                {
                }
            }
        }

        // =========================
        // Win32
        // =========================

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(
            IntPtr hWnd,
            int index);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongW")]
        private static extern IntPtr GetWindowLong32(
            IntPtr hWnd,
            int index);

        private static IntPtr GetWindowLongPtr(
            IntPtr hWnd,
            int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(
                    hWnd,
                    index)
                : GetWindowLong32(
                    hWnd,
                    index);
        }

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(
            IntPtr hWnd,
            int index,
            IntPtr newValue);

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongW")]
        private static extern IntPtr SetWindowLong32(
            IntPtr hWnd,
            int index,
            IntPtr newValue);

        private static IntPtr SetWindowLongPtr(
            IntPtr hWnd,
            int index,
            IntPtr newValue)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(
                    hWnd,
                    index,
                    newValue)
                : SetWindowLong32(
                    hWnd,
                    index,
                    newValue);
        }

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}