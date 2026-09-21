using SmartScreenshotManager.Data;
using SmartScreenshotManager.Models;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartScreenshotManager.Services
{
    public sealed class EmbeddingService
    {
        private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly ScreenshotRepository _repository;
        public EmbeddingService(ScreenshotRepository repository) => _repository = repository;

        public async Task<float[]> EmbedAsync(string text, AiConfiguration config, CancellationToken token)
        {
            if (!config.CanAnalyze) throw new InvalidOperationException("Enable AI and save an API key in Settings first.");
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Enter a search phrase.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = SemanticVectorStore.Model, input = SemanticDocument.LimitText(text, 6000),
                dimensions = SemanticVectorStore.Dimensions, encoding_format = "float"
            }), Encoding.UTF8, "application/json");
            token.ThrowIfCancellationRequested();
            long requestId = _repository.ReserveAiRequest(config.DailyLimit, SemanticVectorStore.Model);
            using var response = await Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException((int)response.StatusCode switch
                {
                    401 => "API key rejected. Check Settings.",
                    429 => "API quota or rate limit reached. Check API billing and retry later.",
                    403 => "Access to the embedding model was denied.",
                    _ => $"Embedding request failed (HTTP {(int)response.StatusCode})."
                });
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (json.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("prompt_tokens", out var count) && count.TryGetInt64(out long tokens) && tokens >= 0)
                _repository.RecordAiUsage(requestId, tokens, 0);
            var vector = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
                .EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (vector.Length != SemanticVectorStore.Dimensions || vector.Any(x => !float.IsFinite(x))
                || vector.All(x => x == 0))
                throw new InvalidOperationException("API returned an invalid embedding.");
            return vector;
        }
    }
}
