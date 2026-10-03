using System;
using System.IO;
using System.Text;

namespace ZincXManagerClient.Class.LogC
{
    public class Log : IDisposable
    {
        private readonly object _lock = new object();
        private readonly FileStream _logS;
        private readonly StreamWriter _logW;

        public Log(string filePath = "log.log")
        {
            _logS = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _logW = new StreamWriter(_logS, Encoding.UTF8, 1024, leaveOpen: true);
        }

        public void WriteLog(char logLevel, string text)
        {
            string logLevelText;
            switch (logLevel)
            {
                case 'i': logLevelText = "[INFO]"; break;
                case 'w': logLevelText = "[WARN]"; break;
                case 'd': logLevelText = "[DEBUG]"; break;
                case 'e': logLevelText = "[ERROR]"; break;
                case 'f': logLevelText = "[FATAL]"; break;
                default: logLevelText = "[UNKNOWN]"; break;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            string line = $"{logLevelText}[{time}]{text}";

            lock (_lock)
            {
                _logW.WriteLine(line);
                _logW.Flush();
            }
        }

        public void Dispose()
        {
            _logW?.Dispose();
            _logS?.Dispose();
        }
    }
}