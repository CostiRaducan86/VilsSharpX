using System;
using System.IO;

namespace VideoStreamPlayer
{
    /// <summary>
    /// Simple file-based logging utility for UDP/AVTP diagnostic messages.
    /// </summary>
    public static class DiagnosticLogger
    {
        private static readonly object _logLock = new();
        private static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "udp_rx.log");

        public static string LogPath => _logPath;

        /// <summary>
        /// Appends a timestamped message to the diagnostic log.
        /// </summary>
        public static void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
                }
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}
