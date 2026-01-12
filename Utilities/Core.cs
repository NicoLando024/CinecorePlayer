#nullable enable
using CinecorePlayer2025;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    // ======= Program =======
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var f = new PlayerForm();
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.Sizable;
            f.WindowState = FormWindowState.Maximized;
            f.MaximizeBox = true;

            Application.Run(f);
        }
    }

    // ======= DEBUG CORE (file + ring buffer + batch writer) =======
    internal static class Dbg
    {
        public enum LogLevel { Error = 0, Warn = 1, Info = 2, Verbose = 3 }
        public static LogLevel Level = LogLevel.Info;

        static readonly object _lock = new();
        static readonly Queue<string> _ring = new();
        static readonly int _maxLines = 1500;

        static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "cinecore_debug.log");
        static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
        static readonly Thread _writer;

        static Dbg()
        {
            try { File.AppendAllText(_logPath, $"\r\n==== RUN {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n"); } catch { }
            _writer = new Thread(WriterProc) { IsBackground = true, Name = "DbgWriter" };
            _writer.Start();
        }

        static void WriterProc()
        {
            var batch = new List<string>(128);
            while (true)
            {
                try
                {
                    batch.Clear();
                    if (_queue.TryTake(out var first, 200))
                    {
                        batch.Add(first);
                        while (_queue.TryTake(out var line))
                        {
                            batch.Add(line);
                            if (batch.Count >= 256) break;
                        }
                    }
                    if (batch.Count > 0) File.AppendAllLines(_logPath, batch);
                }
                catch { Thread.Sleep(300); }
            }
        }

        static void Enqueue(string line)
        {
            lock (_lock)
            {
                _ring.Enqueue(line);
                while (_ring.Count > _maxLines) _ring.Dequeue();
            }
            try { Debug.WriteLine(line); } catch { }
            try { _queue.Add(line); } catch { }
        }

        static string Stamp(string msg) => $"{DateTime.Now:HH:mm:ss.fff} | {msg}";
        public static void Log(string msg, LogLevel lvl = LogLevel.Info) { if (lvl > Level) return; Enqueue(Stamp(msg)); }
        public static void Warn(string msg) => Log("WARN: " + msg, LogLevel.Warn);
        public static void Error(string msg) => Log("ERROR: " + msg, LogLevel.Error);
        public static string[] Snapshot() { lock (_lock) return _ring.ToArray(); }

        public static string Hex(Guid g) => g.ToString("B").ToUpperInvariant();
    }

    // ======= ENUM & MODALITÀ =======
    public enum HdrMode { Auto, Off }         // Off = forza SDR (tone-map con madVR/MPCVR)
    public enum Stereo3DMode { None, SBS, TAB }
    public enum VideoRendererChoice { MADVR, MPCVR, EVR }
}
