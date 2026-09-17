using System;
using System.Diagnostics;
using System.IO;

namespace SmartScreenshotManager.Services
{
    public sealed class ProcessingLog
    {
        private readonly string _path;
        private readonly object _gate = new();
        public ProcessingLog(string path) => _path = path;

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
