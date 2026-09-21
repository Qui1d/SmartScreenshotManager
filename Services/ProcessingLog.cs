using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SmartScreenshotManager.Services
{
    public sealed class ProcessingLog
    {
        private readonly string _path;
        private readonly object _gate = new();
        public ProcessingLog(string path) => _path = path;

        public string ReadRecent()
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return "No log entries yet.";
                return string.Join(Environment.NewLine, File.ReadLines(_path).TakeLast(100));
            }
        }

        public void Write(string message)
        {
            try
            {
                lock (_gate)
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
                        File.Move(_path, _path + ".previous", true);
                    File.AppendAllText(_path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
                }
            }
            catch (Exception exception) { Debug.WriteLine(exception); }
        }
    }
}
