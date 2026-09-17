using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace SmartScreenshotManager.Services
{
    public sealed class OcrService
    {
        public async Task<string> RecognizeAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                if (language != null) engine = OcrEngine.TryCreateFromLanguage(language);
            }
            if (engine == null)
                throw new InvalidOperationException(
                    "No Windows OCR language is installed. Install OCR support for your language " +
                    "in Windows language settings, then retry OCR.");

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // Decode at a supported size instead of allocating a full-size enormous bitmap.
            double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension /
                Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale))
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap);
            cancellationToken.ThrowIfCancellationRequested();
            return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        }
    }
}
