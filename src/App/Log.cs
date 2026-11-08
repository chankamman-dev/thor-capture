using System;
using System.IO;
using System.Text;

namespace ThorCapture
{
    // Plain-text log beside the EXE: logs/thor-capture-yyyyMMdd.log. Logging never throws;
    // if the file cannot be written the line is dropped. Safe to call from any thread.
    internal static class Log
    {
        private static readonly object gate = new object();
        private static string directory;

        // Defaults to <exe folder>/logs; tests may point it elsewhere.
        public static string Directory
        {
            get { lock (gate) return directory ?? (directory = Path.Combine(Adb.AppDir(), "logs")); }
            set { lock (gate) directory = value; }
        }

        public static string CurrentFile
        {
            get { return Path.Combine(Directory, "thor-capture-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); }
        }

        public static void Info(string message) { Write("INFO", message, null); }
        public static void Warn(string message) { Write("WARN", message, null); }
        public static void Warn(string message, Exception error) { Write("WARN", message, error); }
        public static void Error(string message, Exception error) { Write("ERROR", message, error); }

        private static void Write(string level, string message, Exception error)
        {
            StringBuilder line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level)
                .Append(" [").Append(System.Threading.Thread.CurrentThread.ManagedThreadId).Append("] ").Append(message);
            if (error != null) line.Append(Environment.NewLine).Append(error);
            line.Append(Environment.NewLine);
            try
            {
                lock (gate)
                {
                    string path = CurrentFile;
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, line.ToString(), Encoding.UTF8);
                }
            }
            catch (IOException) { } // a log line must never take the app down
            catch (UnauthorizedAccessException) { }
        }
    }
}
