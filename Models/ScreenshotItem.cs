using System;
using System.ComponentModel;

namespace SmartScreenshotManager.Models
{
    public class ScreenshotItem : INotifyPropertyChanged
    {
        public int Id { get; set; }

        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime AddedAt { get; set; }

        public string? OcrText { get; set; }
        public string OcrStatus { get; set; } = "Pending";
        public string? OcrError { get; set; }
        public string? Description { get; set; }
        private string? _category;
        private bool _isCategoryUpdating;

        public string? Category
        {
            get => _category;
            set
            {
                if (_category == value) return;
                _category = value;
                Notify(nameof(Category));
            }
        }

        public bool IsCategoryUpdating
        {
            get => _isCategoryUpdating;
            set
            {
                if (_isCategoryUpdating == value) return;
                _isCategoryUpdating = value;
                Notify(nameof(CanChangeCategory));
            }
        }

        public bool CanChangeCategory => !IsCategoryUpdating;
        public string? Tags { get; set; }

        private bool _isFavorite;
        private bool _isCardHovered;
        private bool _isFavoriteFocused;
        private bool _isFavoriteUpdating;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                Notify(nameof(IsFavorite));
                Notify(nameof(FavoriteGlyph));
                Notify(nameof(FavoriteActionText));
                Notify(nameof(FavoriteButtonOpacity));
            }
        }

        // Transient card state; only IsFavorite is stored in SQLite.
        public bool IsCardHovered
        {
            get => _isCardHovered;
            set
            {
                if (_isCardHovered == value) return;
                _isCardHovered = value;
                Notify(nameof(FavoriteButtonOpacity));
            }
        }

        public bool IsFavoriteFocused
        {
            get => _isFavoriteFocused;
            set
            {
                if (_isFavoriteFocused == value) return;
                _isFavoriteFocused = value;
                Notify(nameof(FavoriteButtonOpacity));
            }
        }

        public bool IsFavoriteUpdating
        {
            get => _isFavoriteUpdating;
            set
            {
                if (_isFavoriteUpdating == value) return;
                _isFavoriteUpdating = value;
                Notify(nameof(CanChangeFavorite));
                Notify(nameof(FavoriteButtonOpacity));
            }
        }

        public bool CanChangeFavorite => !IsFavoriteUpdating;
        public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";
        public string FavoriteActionText => IsFavorite ? "Remove from Favorites" : "Add to Favorites";
        public double FavoriteButtonOpacity =>
            IsFavorite || IsCardHovered || IsFavoriteFocused || IsFavoriteUpdating ? 1.0 : 0.0;

        private void Notify(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        public bool IsProcessed { get; set; }
    }
}