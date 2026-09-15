using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace SmartScreenshotManager.Views
{
    public sealed partial class ImageViewerWindow : Window
    {
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpShowWindow = 0x0040;

        private static readonly IntPtr HwndTopmost =
            new IntPtr(-1);

        private readonly string _filePath;

        private bool _fullscreenApplied;

        public ImageViewerWindow(
            string filePath,
            string fileName)
        {
            InitializeComponent();

            _filePath =
                filePath;

            FileNameText.Text =
                fileName;

            LoadImage();

            Activated +=
                ImageViewerWindow_Activated;
        }

        private void LoadImage()
        {
            var bitmap =
                new BitmapImage
                {
                    CreateOptions =
                        BitmapCreateOptions.IgnoreImageCache,

                    UriSource =
                        new Uri(_filePath)
                };

            ViewerImage.Source =
                bitmap;
        }

        private async void ImageViewerWindow_Activated(
            object sender,
            WindowActivatedEventArgs args)
        {
            if (!_fullscreenApplied)
            {
                _fullscreenApplied =
                    true;

                AppWindow.SetPresenter(
                    AppWindowPresenterKind.FullScreen);
            }

            IntPtr hwnd =
                WindowNative.GetWindowHandle(
                    this);

            SetWindowPos(
                hwnd,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove |
                SwpNoSize |
                SwpShowWindow);

            SetForegroundWindow(
                hwnd);

            RootGrid.Focus(
                FocusState.Programmatic);

            /*
             * A double-click is still finishing when the viewer
             * is first activated. The second click can otherwise
             * return focus to MainWindow.
             *
             * Re-assert foreground after that mouse sequence ends.
             */
            await Task.Delay(
                150);

            SetForegroundWindow(
                hwnd);

            RootGrid.Focus(
                FocusState.Programmatic);
        }

        private void EscapeAccelerator_Invoked(
            KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled =
                true;

            Close();
        }

        private void CloseButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(
            IntPtr hWnd);
    }
}