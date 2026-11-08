using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture
{
    internal static class BackendCoreTests
    {
        private static readonly DeviceCapabilities All = (DeviceCapabilities)127;
        private static DisplayTarget Upper() { return new DisplayTarget(0, "4630946441858561667", "upper", 1920, 1080); }
        private static DeviceSession Open(DeviceSessionRegistry registry, string serial = "test") { return registry.Open(serial, All, Upper(), new DisplayTarget(4, "4630946482288158084", "lower", 1240, 1080)); }
        private static OperationLease Acquire(OperationGate gate, DeviceSession session, OperationKind kind)
        {
            OperationLease lease;
            if (gate.TryAcquire(session, kind, out lease) != LeaseOutcome.Acquired) throw new Exception("Expected lease " + kind);
            return lease;
        }
        internal static void Run()
        {
            SessionsAndPolicy(); Task.Run(() => Runner()).GetAwaiter().GetResult(); Task.Run(() => AdbBinding()).GetAwaiter().GetResult();
        }
        private static void SessionsAndPolicy()
        {
            using (DeviceSessionRegistry registry = new DeviceSessionRegistry())
            using (OperationGate gate = new OperationGate(registry))
            {
                DisplayTarget[] incoming = { Upper(), new DisplayTarget(4, "4630946482288158084", "lower", 1240, 1080) };
                DeviceSession session = registry.Open("test", All, incoming);
                incoming[0] = null; DisplayTarget[] exposed = session.Displays; exposed[1] = null;
                Tests.Check(session.Display("upper").PhysicalId == "4630946441858561667" && session.Display("lower").LogicalId == 4,
                    "backend session snapshots both physical and logical display IDs and resists array mutation");
                Tests.Reject(() => registry.Open("test;touch unsafe", All, Upper()), "backend rejects serial command injection");
                Tests.Reject(() => new DisplayTarget(0, "18446744073709551616", "upper", 1920, 1080), "display identities cannot exceed unsigned 64-bit range");
                Tests.Check(new DisplayTarget(0, "000001", "upper", 1, 1).PhysicalId == "1", "display physical identities are canonical decimal values");
                Tests.Reject(() => registry.Open("test", All, Upper(), Upper()), "backend rejects duplicate display identities before changing session");
                Tests.Check(registry.IsCurrent(session), "invalid connection update preserves the active session");
                DeviceSession callbacks = Open(registry, "callbacks");
                OperationLease callbackLease = Acquire(gate, callbacks, OperationKind.Inspect);
                using (CancellationTokenRegistration registration = callbackLease.Token.Register(() => { throw new InvalidOperationException("fixture"); }))
                {
                    registry.Disconnect("callbacks"); callbackLease.Dispose();
                    Tests.Check(gate.ActiveCount == 0 && !registry.IsCurrent(callbacks), "failing cancellation callbacks cannot strand session or lease cleanup");
                }
                OperationLease preview = Acquire(gate, session, OperationKind.Preview), rejected;
                Tests.Check(gate.TryAcquire(session, OperationKind.Recording, out rejected) == LeaseOutcome.Busy,
                    "recording conservatively conflicts with an independent preview encoder");
                using (OperationLease capture = Acquire(gate, session, OperationKind.Capture))
                {
                    Tests.Check(gate.TryAcquire(session, OperationKind.Capture, out rejected) == LeaseOutcome.Busy,
                        "simultaneous capture transactions for the same device are serialized");
                }
                preview.Dispose(); preview.Dispose();
                Tests.Check(gate.ActiveCount == 0, "lease release is idempotent");
                using (OperationLease recording = Acquire(gate, session, OperationKind.Recording))
                using (OperationLease capture = Acquire(gate, session, OperationKind.Capture))
                using (OperationLease observe = Acquire(gate, session, OperationKind.InputObservation))
                    Tests.Check(recording.IsCurrent && capture.IsCurrent && observe.IsCurrent,
                        "recording permits screenshot viewfinding and independent input observation");
                using (OperationLease control = Acquire(gate, session, OperationKind.RemoteControl))
                {
                    Tests.Check(gate.TryAcquire(session, OperationKind.CocoonWrite, out rejected) == LeaseOutcome.Busy,
                        "Cocoon writes cannot overlap real device control");
                    using (OperationLease read = Acquire(gate, session, OperationKind.CocoonRead))
                        Tests.Check(read.IsCurrent, "read-only Cocoon inspection can coexist with control");
                }
                using (OperationLease write = Acquire(gate, session, OperationKind.CocoonWrite))
                {
                    bool blocked = true;
                    foreach (OperationKind kind in Enum.GetValues(typeof(OperationKind)))
                        blocked &= gate.TryAcquire(session, kind, out rejected) == LeaseOutcome.Busy;
                    Tests.Check(blocked, "a Cocoon transaction excludes all device operations until cleanup");
                    DeviceSession other = Open(registry, "other");
                    using (OperationLease onOther = Acquire(gate, other, OperationKind.Recording))
                        Tests.Check(onOther.IsCurrent, "device resources remain independent across serials");
                }
                DeviceSession unknown = registry.Open("unknown", DeviceCapabilities.None);
                Tests.Check(gate.TryAcquire(unknown, OperationKind.RemoteControl, out rejected) == LeaseOutcome.Unsupported,
                    "unknown capabilities cannot silently enable real control");
                using (OperationLease inspect = Acquire(gate, unknown, OperationKind.Inspect))
                    Tests.Check(inspect.IsCurrent, "unknown device still allows explicit read-only inspection");
                OperationLease oldRecording = Acquire(gate, session, OperationKind.Recording);
                DeviceSession current = Open(registry);
                Tests.Check(session.Token.IsCancellationRequested && !oldRecording.IsCurrent && current.Generation > session.Generation,
                    "reconnection cancels old work and invalidates late result binding");
                Tests.Check(gate.TryAcquire(current, OperationKind.Preview, out rejected) == LeaseOutcome.Busy,
                    "old encoder resources stay held after reconnect until their owner completes cleanup");
                oldRecording.Dispose();
                using (OperationLease fresh = Acquire(gate, current, OperationKind.Preview))
                {
                    registry.Disconnect(current.Serial);
                    Tests.Check(fresh.Token.IsCancellationRequested && !fresh.IsCurrent, "disconnect cancels active leases");
                }
                Tests.Check(gate.TryAcquire(current, OperationKind.Capture, out rejected) == LeaseOutcome.Disconnected,
                    "disconnected snapshots cannot start new work");
                DeviceSession foreign;
                using (DeviceSessionRegistry otherRegistry = new DeviceSessionRegistry())
                {
                    foreign = Open(otherRegistry);
                    Tests.Check(gate.TryAcquire(foreign, OperationKind.Capture, out rejected) == LeaseOutcome.Disconnected,
                        "a snapshot from a different registry cannot authorize operations");
                }
                current = Open(registry);
                Task<OperationLease>[] races = Enumerable.Range(0, 24).Select(i => Task.Run(() => {
                    OperationLease winner; return gate.TryAcquire(current, OperationKind.Recording, out winner) == LeaseOutcome.Acquired ? winner : null;
                })).ToArray();
                Task.WaitAll(races);
                Tests.Check(races.Count(t => t.Result != null) == 1, "concurrent recording starts atomically acquire exactly one lease");
                foreach (Task<OperationLease> race in races) if (race.Result != null) race.Result.Dispose();
                OperationLease held = Acquire(gate, current, OperationKind.RemoteControl);
                gate.Dispose();
                Tests.Check(held.Token.IsCancellationRequested && gate.ActiveCount == 1 && !held.IsCurrent,
                    "gate shutdown requests cancellation without pretending held resources have stopped");
                held.Dispose();
                Tests.Check(gate.TryAcquire(current, OperationKind.Inspect, out rejected) == LeaseOutcome.Closed && gate.ActiveCount == 0,
                    "closed gate cannot restart and cleanup eventually releases all leases");
            }
        }
        private static ProcessRequest Request(string command, params string[] args)
        {
            return new ProcessRequest(Assembly.GetExecutingAssembly().Location, "test.backend", new[] { command }.Concat(args).ToArray());
        }
        private static async Task Runner()
        {
            using (ProcessRunner runner = new ProcessRunner(1024))
            {
                ProcessResult flood = await runner.RunAsync(Request("backend-flood"), TimeSpan.FromSeconds(4), CancellationToken.None);
                Tests.Check(flood.Outcome == ProcessOutcome.Success && flood.Output.Length == 1024 && flood.Error.Length == 1024
                    && flood.Truncated && flood.CleanupComplete, "both output pipes are drained past storage limits without deadlock");
                ProcessResult failed = await runner.RunAsync(Request("backend-fail"), TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(failed.Outcome == ProcessOutcome.Failed && failed.ExitCode == 17 && failed.Error == "fixture failure",
                    "process failure retains typed outcome and exit code");
                ProcessResult missing = await runner.RunAsync(new ProcessRequest(Path.Combine(Tests.Root, "no-backend-executable.exe"), "missing", new string[0]),
                    TimeSpan.FromSeconds(1), CancellationToken.None);
                Tests.Check(missing.Outcome == ProcessOutcome.NotFound && missing.CleanupComplete && runner.ActiveCount == 0,
                    "missing executable returns NotFound without leaking job ownership");
                ProcessResult denied = await runner.RunAsync(new ProcessRequest(Tests.Root, "denied", new string[0]), TimeSpan.FromSeconds(1), CancellationToken.None);
                Tests.Check(denied.Outcome == ProcessOutcome.StartDenied && denied.CleanupComplete && runner.ActiveCount == 0,
                    "unlaunchable executable returns StartDenied without leaking job ownership");
                string marker = Path.Combine(Tests.Root, "backend-pre-cancel.marker");
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    ProcessResult beforeStart = await runner.RunAsync(Request("backend-marker", marker), TimeSpan.FromSeconds(1), cancelled.Token);
                    Tests.Check(beforeStart.Outcome == ProcessOutcome.Cancelled && !File.Exists(marker), "pre-cancelled job never launches a child");
                }
                Stopwatch clock = Stopwatch.StartNew();
                ProcessResult timeout = await runner.RunAsync(Request("backend-hang"), TimeSpan.FromMilliseconds(150), CancellationToken.None);
                Tests.Check(timeout.Outcome == ProcessOutcome.TimedOut && timeout.CleanupComplete && runner.ActiveCount == 0 && clock.ElapsedMilliseconds < 3500,
                    "timeout terminates only the owned hung child within a bounded cleanup wait");
                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    Task<ProcessResult> work = runner.RunAsync(Request("backend-hang"), TimeSpan.FromSeconds(10), cancelled.Token);
                    cancelled.CancelAfter(150);
                    ProcessResult aborted = await work;
                    Tests.Check(aborted.Outcome == ProcessOutcome.Cancelled && aborted.CleanupComplete, "in-flight cancellation stops an owned child");
                }
                string[] values = { "中文 空格", "a\"b", "trailing\\", "", "x\\\"z", "__BACKEND_SECRET__" };
                string[] args = new[] { "backend-echo" }.Concat(values).ToArray();
                Dictionary<string, string> env = new Dictionary<string, string> { { "BACKEND_FIXTURE_ENV", "original" } };
                ProcessRequest immutable = new ProcessRequest(Assembly.GetExecutingAssembly().Location, "echo", args, env);
                args[1] = "mutated"; env["BACKEND_FIXTURE_ENV"] = "mutated";
                ProcessResult echo = await runner.RunAsync(immutable, TimeSpan.FromSeconds(3), CancellationToken.None);
                string expected = string.Join("|", values.Select(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)))) + "\noriginal";
                Tests.Check(echo.Output.TrimEnd('\r', '\n') == expected, "argument quoting and immutable environment survive spaces Unicode quotes and trailing backslashes");
                ProcessResult inherited = await runner.RunAsync(Request("backend-env", "THOR_CAPTURE_TEST_MODE"), TimeSpan.FromSeconds(3), CancellationToken.None);
                ProcessResult removed = await runner.RunAsync(new ProcessRequest(Assembly.GetExecutingAssembly().Location, "env.remove", new[] { "backend-env", "THOR_CAPTURE_TEST_MODE" },
                    new Dictionary<string, string> { { "THOR_CAPTURE_TEST_MODE", null } }), TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(inherited.Output.Length > 0 && removed.Outcome == ProcessOutcome.Success && removed.Output == "", "environment override can remove an inherited variable");
                Tests.Reject(() => new ProcessRequest("fixture.exe", "env", new string[0], new Dictionary<string, string> { { "BAD=KEY", "value" } }),
                    "invalid environment keys are rejected before process start");
                string spaced = Path.Combine(Tests.Root, "后端 space.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, spaced, true);
                ProcessResult spacedResult = await runner.RunAsync(new ProcessRequest(spaced, "path", new[] { "backend-echo", "test" }), TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(spacedResult.Outcome == ProcessOutcome.Success, "owned child can launch from a Unicode path containing spaces");
                string log = File.ReadAllText(Log.CurrentFile);
                Tests.Check(!log.Contains("__BACKEND_SECRET__") && !log.Contains(spaced), "process diagnostics exclude argv output environment and executable paths");
                await runner.StopAllAsync();
                Tests.Reject(() => runner.RunAsync(Request("backend-echo"), TimeSpan.FromSeconds(1), CancellationToken.None), "closed runner refuses new jobs");
            }
            using (ProcessRunner runner = new ProcessRunner())
            using (Process foreign = new Process { StartInfo = Request("backend-hang").StartInfo() })
            {
                foreign.Start();
                try
                {
                    Task<ProcessResult> owned = runner.RunAsync(Request("backend-hang"), TimeSpan.FromSeconds(20), CancellationToken.None);
                    await runner.StopAllAsync();
                    Tests.Check((await owned).Outcome == ProcessOutcome.Cancelled && runner.ActiveCount == 0 && !foreign.HasExited,
                        "runner shutdown cancels its jobs and leaves an unrelated process alive");
                }
                finally { if (!foreign.HasExited) foreign.Kill(); foreign.WaitForExit(2000); }
            }
        }
        private static async Task AdbBinding()
        {
            using (DeviceSessionRegistry registry = new DeviceSessionRegistry())
            using (OperationGate gate = new OperationGate(registry))
            using (ProcessRunner runner = new ProcessRunner())
            {
                DeviceSession session = Open(registry);
                ReadOnlyAdbClient client = new ReadOnlyAdbClient(Assembly.GetExecutingAssembly().Location, runner, gate);
                string[] argv = ReadOnlyAdbClient.Arguments(session, AdbQuery.Displays);
                Tests.Check(argv[0] == "-s" && argv[1] == "test" && argv[2] == "shell" && argv[3] == "dumpsys" && argv[4] == "display",
                    "ADB adapter binds a fixed serial and exposes a read-only command allowlist");
                Tests.Reject(() => ReadOnlyAdbClient.Arguments(session, (AdbQuery)999), "unknown ADB operation cannot become arbitrary shell");
                using (OperationLease write = Acquire(gate, session, OperationKind.CocoonWrite))
                {
                    DeviceCommandResult busy = await client.RunAsync(session, AdbQuery.Properties, TimeSpan.FromSeconds(3), CancellationToken.None);
                    Tests.Check(busy.Outcome == CommandOutcome.Busy && runner.ActiveCount == 0, "blocked device inspection launches no process");
                }
                Tests.FakeMode("backend-adb-normal");
                DeviceCommandResult success = await client.RunAsync(session, AdbQuery.Properties, TimeSpan.FromSeconds(3), CancellationToken.None);
                Tests.Check(success.Outcome == CommandOutcome.Success && success.SessionId == session.Id && success.Output == "backend properties",
                    "typed ADB result retains its source session identity");
                Tests.FakeMode("backend-adb-slow");
                Task<DeviceCommandResult> oldWork = client.RunAsync(session, AdbQuery.Properties, TimeSpan.FromSeconds(3), CancellationToken.None);
                registry.Disconnect(session.Serial); Open(registry);
                DeviceCommandResult stale = await oldWork;
                Tests.Check(stale.Outcome == CommandOutcome.Disconnected && stale.Output == "" && stale.CleanupComplete && gate.ActiveCount == 0,
                    "reconnect discards late command output and cleans up the old job before returning its lease");
                Tests.FakeMode("normal");
                Tests.Check(await client.StopAsync(), "ADB shutdown drains the runner and its retained resource leases");
                Tests.Reject(() => client.RunAsync(session, AdbQuery.Properties, TimeSpan.FromSeconds(1), CancellationToken.None), "closed ADB adapter cannot start another process");
            }
        }
        internal static int FakeAdb(string command, string mode, string dir)
        {
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Length == 2 && args[0] == "backend-env") { Console.Write(Environment.GetEnvironmentVariable(args[1]) ?? ""); return 0; }
            if (args.Length > 0 && args[0] == "backend-echo")
            {
                Console.Write(string.Join("|", args.Skip(1).Select(v => Convert.ToBase64String(Encoding.UTF8.GetBytes(v)))) + "\n" + (Environment.GetEnvironmentVariable("BACKEND_FIXTURE_ENV") ?? "")); return 0;
            }
            if (command == "backend-flood") { Console.Error.Write(new string('e', 300000)); Console.Out.Write(new string('o', 300000)); return 0; }
            if (command == "backend-fail") { Console.Error.Write("fixture failure"); return 17; }
            if (command == "backend-hang") { Thread.Sleep(20000); return 0; }
            if (args.Length == 2 && args[0] == "backend-marker") { File.WriteAllText(args[1], "started"); return 0; }
            if (command.Contains("getprop") && mode != null && mode.StartsWith("backend-adb-"))
            {
                if (mode == "backend-adb-slow") Thread.Sleep(500);
                Console.Write("backend properties"); return 0;
            }
            return Tests.NotHandled;
        }
    }
}
