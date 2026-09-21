using System;
using System.Collections.Generic;
using System.Globalization;

namespace SmartScreenshotManager.Models
{
    public sealed record ActivityItem(int Id, string FileName, string FilePath,
        string OcrStatus, string AiStatus, string? OcrError, string? AiError,
        long? OcrDurationMs, long? AiDurationMs, string? OcrFinishedAt, string? AiFinishedAt,
        bool CanRetryOcr = false, bool CanRetryAi = false)
    {
        public string OcrLabel => $"OCR: {OcrStatus} · {Duration(OcrDurationMs)}{Finished(OcrFinishedAt)}";
        public string AiLabel => $"AI: {AiStatus} · {Duration(AiDurationMs)}{Finished(AiFinishedAt)}";
        public string ErrorText => string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(OcrError) ? "" : "OCR: " + OcrError,
            string.IsNullOrWhiteSpace(AiError) ? "" : "AI: " + AiError
        }).Trim();
        private static string Duration(long? ms) => ms.HasValue
            ? $"last attempt {ms.Value / 1000.0:0.00} s" : "duration not recorded";
        private static string Finished(string? value) => DateTimeOffset.TryParse(value,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? " · " + date.ToLocalTime().ToString("g") : "";
    }
    public sealed record ActivitySnapshot(List<ActivityItem> Items, string Summary, long Total);
}
