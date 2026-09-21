namespace SmartScreenshotManager.Models
{
    public sealed record AiAnalysis(string Description, string Category, string[] Tags);
    // Do not log this object: it contains a user-provided credential.
    public sealed class AiConfiguration
    {
        public bool Enabled { get; init; }
        public bool Automatic { get; init; }
        public int DailyLimit { get; init; } = 100;
        public string ApiKey { get; init; } = string.Empty;
        public string Model { get; init; } = "gpt-4.1-mini-2025-04-14";
        public bool CanAnalyze => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
    }
    public sealed record AiJobState(int Id, string FilePath, string Status, string? Error,
        string? Description, string? Tags, string? Category, bool CategoryIsManual, string? OcrText);
}
