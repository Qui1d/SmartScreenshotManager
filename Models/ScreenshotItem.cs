using System;

namespace SmartScreenshotManager.Models
{
    public class ScreenshotItem
    {
        public int Id { get; set; }

        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public string? OcrText { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Tags { get; set; }

        public bool IsFavorite { get; set; }
        public bool IsProcessed { get; set; }
    }
}