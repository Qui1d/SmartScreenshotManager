using SmartScreenshotManager.Data;
using SmartScreenshotManager.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SmartScreenshotManager.Services
{
    // One consumer keeps native OCR CPU/memory usage bounded. SQLite stores durable job state.
    public sealed class OcrQueueService
    {
        private readonly ScreenshotRepository _repository;
        private readonly OcrService _ocr = new();
        private readonly ProcessingLog _log;
        private readonly Channel<(int Id, bool Force)> _queue =
            Channel.CreateUnbounded<(int Id, bool Force)>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = false });
        private readonly ConcurrentDictionary<int, byte> _pending = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private int _activeId;
        public int ActiveId => Volatile.Read(ref _activeId);
        public int[] PendingIds => _pending.Keys.ToArray();
        public event Action<OcrJobState>? StateChanged;

        public OcrQueueService(ScreenshotRepository repository, ProcessingLog log)
        {
            _repository = repository;
            _log = log;
            _worker = Task.Run(RunAsync);
        }

        public bool Enqueue(int id, bool force = false)
        {
            if (_stop.IsCancellationRequested || !_pending.TryAdd(id, 0)) return false;
            if (_queue.Writer.TryWrite((id, force))) return true;
            _pending.TryRemove(id, out _);
            return false;
        }

        private void Publish(OcrJobState state)
        {
            try { StateChanged?.Invoke(state); }
            catch (Exception exception) { _log.Write($"OCR notification: {exception.Message}"); }
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(_stop.Token))
                {
                    OcrJobState? state = null;
                    bool retryRenamed = false;
                    var timer = Stopwatch.StartNew();
                    bool attempted = false;
                    try
                    {
                        state = _repository.GetOcrState(job.Id);
                        if (state == null) continue;
                        if (!job.Force && state.Status is "Processed" or "Failed")
                        {
                            Publish(state);
                            continue;
                        }
                        if (!_repository.SaveOcrState(state.Id, state.FilePath, "Processing", state.Text, null))
                        {
                            retryRenamed = true;
                            continue;
                        }
                        attempted = true;
                        Volatile.Write(ref _activeId, job.Id);
                        Publish(state with { Status = "Processing", Error = null });
                        string text = await _ocr.RecognizeAsync(state.FilePath, _stop.Token);
                        if (_repository.SaveOcrState(state.Id, state.FilePath, "Processed", text, null))
                        {
                            Publish(state with { Status = "Processed", Text = text, Error = null });
                            _log.Write($"OCR {state.Id}: Processed in {timer.ElapsedMilliseconds} ms, {text.Length} chars");
                        }
                        else retryRenamed = true;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        // A remaining Processing record is also recovered on the next folder load.
                        if (state != null)
                        {
                            try { _repository.SaveOcrState(state.Id, state.FilePath, "Pending", state.Text, null); }
                            catch (Exception exception) { _log.Write($"OCR shutdown: {exception.Message}"); }
                        }
                        break;
                    }
                    catch (Exception exception)
                    {
                        _log.Write($"OCR {job.Id}: Failed after {timer.ElapsedMilliseconds} ms: {exception.Message}");
                        if (state != null)
                        {
                            try
                            {
                                if (_repository.SaveOcrState(state.Id, state.FilePath, "Failed", state.Text, exception.Message))
                                    Publish(state with { Status = "Failed", Error = exception.Message });
                                else retryRenamed = true;
                            }
                            catch (Exception saveException)
                            {
                                _log.Write($"OCR state save: {saveException.Message}");
                                Publish(state with { Status = "Failed", Error = saveException.Message });
                            }
                        }
                    }
                    finally
                    {
                        if (attempted)
                        {
                            try { _repository.SaveAttemptTiming(job.Id, false, timer.ElapsedMilliseconds); }
                            catch { _log.Write("Could not save task duration."); }
                        }
                        Volatile.Write(ref _activeId, 0);
                        _pending.TryRemove(job.Id, out _);
                        // A rename invalidates an in-flight result. Resolve the latest path on retry.
                        if (retryRenamed) Enqueue(job.Id, job.Force);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { _log.Write($"OCR queue stopped: {exception}"); }
        }

        public void Stop()
        {
            _queue.Writer.TryComplete();
            _stop.Cancel();
        }
    }
}
