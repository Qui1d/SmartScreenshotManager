using SmartScreenshotManager.Data;
using SmartScreenshotManager.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SmartScreenshotManager.Services
{
    public sealed class AiQueueService
    {
        private readonly ScreenshotRepository _repository;
        private readonly ProcessingLog _log;
        private readonly OpenAiAnalysisService _api = new();
        private readonly Channel<(int Id, bool Automatic, int Version)> _jobs =
            Channel.CreateUnbounded<(int, bool, int)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<int, byte> _pending = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private CancellationTokenSource _session = new();
        private AiConfiguration _config = new();
        private int _version;
        private readonly Task _worker;
        private int _activeId;
        public int ActiveId => Volatile.Read(ref _activeId);
        public int[] PendingIds => _pending.Keys.ToArray();
        public event Action<int>? StateChanged;
        public bool CanAnalyze { get { lock (_gate) return _config.CanAnalyze; } }
        public bool AutomaticEnabled { get { lock (_gate) return _config.CanAnalyze && _config.Automatic; } }
        public bool IsPending(int id) => _pending.ContainsKey(id);

        public AiQueueService(ScreenshotRepository repository, ProcessingLog log)
        {
            _repository = repository;
            _log = log;
            _worker = Task.Run(RunAsync);
        }

        public void Configure(AiConfiguration configuration)
        {
            lock (_gate)
            {
                _session.Cancel();
                // Worker requests keep their captured token; avoid disposing its source mid-request.
                _session = new CancellationTokenSource();
                _config = configuration;
                _version++;
            }
        }

        public bool Enqueue(int id, bool automatic = false)
        {
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || !_config.CanAnalyze || (automatic && !_config.Automatic)
                    || !_pending.TryAdd(id, 0)) return false;
                if (_jobs.Writer.TryWrite((id, automatic, _version))) return true;
                _pending.TryRemove(id, out _);
                return false;
            }
        }

        private void Publish(int id)
        {
            try { StateChanged?.Invoke(id); }
            catch { _log.Write($"AI {id}: UI notification failed"); }
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var job in _jobs.Reader.ReadAllAsync(_stop.Token))
                {
                    AiJobState? state = null;
                    var timer = Stopwatch.StartNew();
                    bool attempted = false;
                    try
                    {
                        AiConfiguration config;
                        CancellationToken sessionToken;
                        lock (_gate)
                        {
                            if (job.Version != _version || !_config.CanAnalyze
                                || (job.Automatic && !_config.Automatic)) continue;
                            config = _config;
                            sessionToken = _session.Token;
                        }
                        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, sessionToken);
                        var token = cancel.Token;
                        token.ThrowIfCancellationRequested();
                        state = _repository.GetAiState(job.Id);
                        if (state == null || (job.Automatic && state.Status == "Processed")) continue;
                        if (!_repository.SaveAiStatus(state.Id, state.FilePath, "Processing", null)) continue;
                        attempted = true;
                        Volatile.Write(ref _activeId, job.Id);
                        Publish(state.Id);
                        long requestId = 0;
                        var result = await _api.AnalyzeAsync(state, config, token,
                            () => requestId = _repository.ReserveAiRequest(config.DailyLimit, config.Model),
                            (input, output) => _repository.RecordAiUsage(requestId, input, output));
                        token.ThrowIfCancellationRequested();
                        if (!_repository.SaveAiResult(state.Id, state.FilePath, result))
                        {
                            // Do not automatically repeat a paid request after rename/delete.
                            var current = _repository.GetAiState(state.Id);
                            if (current != null)
                                _repository.SaveAiStatus(current.Id, current.FilePath, "Cancelled",
                                    "File was renamed during analysis. Run AI analysis again if needed.");
                        }
                        _log.Write($"AI {job.Id}: response handled in {timer.ElapsedMilliseconds} ms");
                    }
                    catch (OperationCanceledException)
                    {
                        bool interrupted;
                        lock (_gate) interrupted = _stop.IsCancellationRequested || job.Version != _version;
                        SaveFailure(job.Id, interrupted ? "Cancelled" : "Failed",
                            interrupted ? "Analysis cancelled." : "AI request timed out. Try again later.");
                    }
                    catch (Exception exception)
                    {
                        string message = exception is InvalidOperationException ? exception.Message
                            : exception is HttpRequestException ? "Cannot reach OpenAI. Check your internet connection."
                            : "Could not analyze this file. Check that it is readable and try again.";
                        SaveFailure(job.Id, "Failed", message);
                    }
                    finally
                    {
                        if (attempted)
                        {
                            try { _repository.SaveAttemptTiming(job.Id, true, timer.ElapsedMilliseconds); }
                            catch { _log.Write("Could not save task duration."); }
                        }
                        Volatile.Write(ref _activeId, 0);
                        _pending.TryRemove(job.Id, out _);
                        Publish(job.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch { _log.Write("AI queue stopped unexpectedly. Restart the app to resume manual analysis."); }
        }

        private void SaveFailure(int id, string status, string message)
        {
            try
            {
                var latest = _repository.GetAiState(id);
                if (latest != null) _repository.SaveAiStatus(id, latest.FilePath, status, message);
            }
            catch { _log.Write($"AI {id}: could not save job status"); }
            _log.Write($"AI {id}: {status}"); // Never log credentials, request bodies, OCR, or API responses.
        }

        public void Stop()
        {
            _jobs.Writer.TryComplete();
            _stop.Cancel();
            lock (_gate) _session.Cancel();
        }
    }
}
