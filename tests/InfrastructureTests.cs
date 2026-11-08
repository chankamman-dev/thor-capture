using System;
using System.IO;

namespace ThorCapture
{
    // Checks the shared test infrastructure itself, and doubles as the template for feature
    // suites: a static class named *Tests with Run(), and optionally RunLive() and FakeAdb().
    internal static class InfrastructureTests
    {
        internal static void Run()
        {
            AdbResult pong = Adb.Run("infra-ping", 5000);
            Tests.Check(pong.Code == 0 && pong.Out.Trim() == "pong", "suites can register fake adb answers");

            string previous = Log.Directory;
            Log.Directory = Path.Combine(Tests.Root, "logs-" + Guid.NewGuid().ToString("N"));
            try
            {
                Log.Info("infrastructure check");
                Log.Error("with exception", new InvalidOperationException("boom"));
                string text = File.ReadAllText(Log.CurrentFile);
                Tests.Check(text.Contains("INFO") && text.Contains("infrastructure check") && text.Contains("boom"), "log writes levels, messages and exceptions");
            }
            finally { Log.Directory = previous; }
        }

        internal static int FakeAdb(string command, string mode, string dir)
        {
            if (command != "infra-ping") return Tests.NotHandled;
            Console.WriteLine("pong");
            return 0;
        }
    }
}
