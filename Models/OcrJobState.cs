namespace SmartScreenshotManager.Models
{
    public sealed record OcrJobState(int Id, string FilePath, string Status,
        string? Text, string? Error);
}
