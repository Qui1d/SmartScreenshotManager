using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace SmartScreenshotManager.Services
{
    public sealed class ScreenshotCaptureService
    {
        private const int SmCxScreen = 0;
        private const int SmCyScreen = 1;

        private const int Srccopy = 0x00CC0020;
        private const int CaptureBlt = 0x40000000;

        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;

        public ScreenCaptureData CapturePrimaryScreen()
        {
            int width =
                GetSystemMetrics(SmCxScreen);

            int height =
                GetSystemMetrics(SmCyScreen);

            return CaptureRegion(
                0,
                0,
                width,
                height);
        }

        public ScreenCaptureData CaptureRegion(
            int x,
            int y,
            int width,
            int height)
        {
            IntPtr screenDc =
                GetDC(IntPtr.Zero);

            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Could not access the screen.");
            }

            IntPtr memoryDc =
                CreateCompatibleDC(screenDc);

            if (memoryDc == IntPtr.Zero)
            {
                ReleaseDC(
                    IntPtr.Zero,
                    screenDc);

                throw new InvalidOperationException(
                    "Could not create a screen capture context.");
            }

            IntPtr bitmap =
                CreateCompatibleBitmap(
                    screenDc,
                    width,
                    height);

            if (bitmap == IntPtr.Zero)
            {
                DeleteDC(memoryDc);

                ReleaseDC(
                    IntPtr.Zero,
                    screenDc);

                throw new InvalidOperationException(
                    "Could not create the capture bitmap.");
            }

            IntPtr previousBitmap =
                SelectObject(
                    memoryDc,
                    bitmap);

            try
            {
                bool copied =
                    BitBlt(
                        memoryDc,
                        0,
                        0,
                        width,
                        height,
                        screenDc,
                        x,
                        y,
                        Srccopy | CaptureBlt);

                if (!copied)
                {
                    throw new InvalidOperationException(
                        "Screen capture failed.");
                }

                var bitmapInfo =
                    new BitmapInfo();

                bitmapInfo.Header.Size =
                    (uint)Marshal.SizeOf<BitmapInfoHeader>();

                bitmapInfo.Header.Width =
                    width;

                /*
                 * Negative height creates a top-down bitmap,
                 * which makes cropping much easier later.
                 */
                bitmapInfo.Header.Height =
                    -height;

                bitmapInfo.Header.Planes =
                    1;

                bitmapInfo.Header.BitCount =
                    32;

                bitmapInfo.Header.Compression =
                    BiRgb;

                byte[] pixels =
                    new byte[width * height * 4];

                int result =
                    GetDIBits(
                        memoryDc,
                        bitmap,
                        0,
                        (uint)height,
                        pixels,
                        ref bitmapInfo,
                        DibRgbColors);

                if (result == 0)
                {
                    throw new InvalidOperationException(
                        "Could not read screen pixels.");
                }

                return new ScreenCaptureData(
                    width,
                    height,
                    pixels);
            }
            finally
            {
                SelectObject(
                    memoryDc,
                    previousBitmap);

                DeleteObject(
                    bitmap);

                DeleteDC(
                    memoryDc);

                ReleaseDC(
                    IntPtr.Zero,
                    screenDc);
            }
        }

        public ScreenCaptureData Crop(
            ScreenCaptureData source,
            int x,
            int y,
            int width,
            int height)
        {
            x = Math.Clamp(
                x,
                0,
                source.Width - 1);

            y = Math.Clamp(
                y,
                0,
                source.Height - 1);

            width =
                Math.Clamp(
                    width,
                    1,
                    source.Width - x);

            height =
                Math.Clamp(
                    height,
                    1,
                    source.Height - y);

            byte[] croppedPixels =
                new byte[width * height * 4];

            int sourceStride =
                source.Width * 4;

            int destinationStride =
                width * 4;

            for (int row = 0; row < height; row++)
            {
                int sourceIndex =
                    ((y + row) * sourceStride)
                    + (x * 4);

                int destinationIndex =
                    row * destinationStride;

                Buffer.BlockCopy(
                    source.Pixels,
                    sourceIndex,
                    croppedPixels,
                    destinationIndex,
                    destinationStride);
            }

            return new ScreenCaptureData(
                width,
                height,
                croppedPixels);
        }

        public async Task<StorageFile> SavePngAsync(
            ScreenCaptureData capture,
            string folderPath,
            string fileName)
        {
            StorageFolder folder =
                await StorageFolder
                    .GetFolderFromPathAsync(
                        folderPath);

            StorageFile file =
                await folder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.GenerateUniqueName);

            using var stream =
                await file.OpenAsync(
                    FileAccessMode.ReadWrite);

            BitmapEncoder encoder =
                await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId,
                    stream);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)capture.Width,
                (uint)capture.Height,
                96,
                96,
                capture.Pixels);

            await encoder.FlushAsync();

            return file;
        }

        public sealed class ScreenCaptureData
        {
            public int Width { get; }

            public int Height { get; }

            public byte[] Pixels { get; }

            public ScreenCaptureData(
                int width,
                int height,
                byte[] pixels)
            {
                Width = width;
                Height = height;
                Pixels = pixels;
            }
        }

        [StructLayout(
            LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;

            public int Width;
            public int Height;

            public ushort Planes;
            public ushort BitCount;

            public uint Compression;
            public uint SizeImage;

            public int XPelsPerMeter;
            public int YPelsPerMeter;

            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(
            LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;

            public uint Colors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(
            IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(
            IntPtr hWnd,
            IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(
            IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(
            IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(
            IntPtr hDc,
            int width,
            int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(
            IntPtr hDc,
            IntPtr objectHandle);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(
            IntPtr objectHandle);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr destinationDc,
            int x,
            int y,
            int width,
            int height,
            IntPtr sourceDc,
            int sourceX,
            int sourceY,
            int rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            IntPtr hDc,
            IntPtr bitmap,
            uint start,
            uint lines,
            [Out] byte[] bits,
            ref BitmapInfo bitmapInfo,
            uint usage);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(
            int index);
    }
}