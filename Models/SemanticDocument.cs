using System;
using System.Security.Cryptography;
using System.Text;

namespace SmartScreenshotManager.Models
{
    public sealed record SemanticDocument(int Id, string FilePath, string Text, string Fingerprint)
    {
        public static SemanticDocument? Create(int id, string path, string name, string? category,
            string? description, string? tags, string? ocr)
        {
            if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(tags)
                && string.IsNullOrWhiteSpace(ocr)) return null;
            string text = LimitText($"Description: {description}\nTags: {tags}\nCategory: {category}\nFile: {name}\nText: {ocr}", 6000);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            return new(id, path, text, hash);
        }

        // A byte cap also bounds token count, without splitting Unicode characters.
        public static string LimitText(string text, int maxBytes)
        {
            var result = new StringBuilder();
            int bytes = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                bytes += rune.Utf8SequenceLength;
                if (bytes > maxBytes) break;
                result.Append(rune.ToString());
            }
            return result.ToString();
        }
    }
}
