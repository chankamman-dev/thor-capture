using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.DeviceWatch
{
    internal enum WirelessSupport { Unknown, Unsupported, Supported }
    internal enum WirelessOperation { Pair, Connect, Disconnect }
    internal enum WirelessOutcome { Success, BadAddress, BadCode, UnknownCapability, Unsupported, TimedOut, Cancelled, Rejected, Busy, Closed, CleanupPending }
    internal sealed class WirelessResult
    {
        public readonly WirelessOutcome Outcome;
        public readonly bool CleanupComplete;
        public string Hint
        {
            get
            {
                switch (Outcome)
                {
                    case WirelessOutcome.Success: return "操作完成；请刷新设备列表确认连接状态。";
                    case WirelessOutcome.BadAddress: return "地址无效；请输入设备无线调试页面显示的 IP 和端口。";
                    case WirelessOutcome.BadCode: return "配对码无效；请使用当前配对窗口中的六位数字。";
                    case WirelessOutcome.UnknownCapability: return "尚未确认固件支持；先完成无线调试能力验收。";
                    case WirelessOutcome.Unsupported: return "此固件或工具不支持该无线操作。";
                    case WirelessOutcome.TimedOut: return "操作超时；检查设备地址、端口和网络后手动重试。";
                    case WirelessOutcome.Cancelled: return "操作已取消。";
                    case WirelessOutcome.CleanupPending: return "操作尚未完成清理；等待确认后再试。";
                    case WirelessOutcome.Busy: return "另一个无线操作正在执行。";
                    case WirelessOutcome.Closed: return "无线组件已关闭。";
                    default: return "设备拒绝或连接失败；核对当前地址与配对窗口后手动重试。";
                }
            }
        }
        public WirelessResult(WirelessOutcome outcome, bool cleanup = true) { Outcome = outcome; CleanupComplete = cleanup; }
    }
    internal interface IWirelessOperationGuard
    {
        // The caller's shared transport coordinator owns this lease until process cleanup.
        Task<IDisposable> AcquireAsync(string endpoint, CancellationToken token);
    }
    internal interface IWirelessTransport
    {
        Task<WirelessResult> ExecuteAsync(WirelessOperation operation, string endpoint, string pairingCode, CancellationToken token);
        Task<bool> StopAsync(TimeSpan deadline, CancellationToken token);
    }
    internal sealed class WirelessCapability
    {
        public readonly WirelessSupport Pairing, Connection;
        private readonly Func<bool> current;
        public WirelessCapability(WirelessSupport pairing, WirelessSupport connection, Func<bool> current)
        { Pairing = pairing; Connection = connection; this.current = current ?? delegate { return false; }; }
        public bool IsCurrent { get { try { return current(); } catch { return false; } } }
        public static WirelessCapability Unknown { get { return new WirelessCapability(WirelessSupport.Unknown, WirelessSupport.Unknown, null); } }
    }
    internal sealed class WirelessAdb
    {
        private readonly IWirelessTransport transport;
        private readonly WirelessCapability capability;
        private readonly object gate = new object();
        private readonly List<string> recent = new List<string>();
        private bool closed;
        public WirelessAdb(IWirelessTransport transport, WirelessCapability capability = null)
        { if (transport == null) throw new ArgumentNullException("transport"); this.transport = transport; this.capability = capability ?? WirelessCapability.Unknown; }
        public static bool TryParseEndpoint(string text, out string host, out int port)
        {
            host = null; port = 0;
            if (text == null || text.Length > 128 || text != text.Trim()) return false;
            Match match = Regex.Match(text, @"\A(?:\[([0-9a-fA-F:]+)\]|([0-9.]+)):([0-9]{1,5})\z");
            if (!match.Success || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) { port = 0; return false; }
            string candidate = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value; IPAddress address;
            if (!IPAddress.TryParse(candidate, out address)) { port = 0; return false; }
            if (match.Groups[1].Success)
            { if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) { port = 0; return false; } }
            else
            {
                string[] octets = candidate.Split('.');
                if (octets.Length != 4 || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { port = 0; return false; }
                foreach (string octet in octets) { int value; if (octet.Length == 0 || (octet.Length > 1 && octet[0] == '0') || !int.TryParse(octet, out value) || value > 255) { port = 0; return false; } }
            }
            byte[] bytes = address.GetAddressBytes(); bool zero = true; foreach (byte b in bytes) zero &= b == 0;
            if (zero || IPAddress.IsLoopback(address) || (bytes.Length == 4 && (bytes[0] >= 224 || bytes[0] == 0)) || (bytes.Length == 16 && (bytes[0] == 255 || address.IsIPv6LinkLocal))) { port = 0; return false; }
            host = address.ToString(); return true;
        }
        private static string Canonical(string endpoint)
        { string host; int port; if (!TryParseEndpoint(endpoint, out host, out port)) return null; return (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture); }
        private Task<WirelessResult> Invoke(WirelessOperation operation, string endpoint, string code, bool explicitRequest, CancellationToken token)
        {
            lock (gate) if (closed) return Task.FromResult(new WirelessResult(WirelessOutcome.Closed));
            string address = Canonical(endpoint); if (address == null) return Task.FromResult(new WirelessResult(WirelessOutcome.BadAddress));
            if (operation == WirelessOperation.Pair && !Regex.IsMatch(code ?? "", @"\A[0-9]{6}\z")) return Task.FromResult(new WirelessResult(WirelessOutcome.BadCode));
            WirelessSupport support = operation == WirelessOperation.Pair ? capability.Pairing : capability.Connection;
            if (!explicitRequest || !capability.IsCurrent || support == WirelessSupport.Unknown) return Task.FromResult(new WirelessResult(WirelessOutcome.UnknownCapability));
            if (support == WirelessSupport.Unsupported) return Task.FromResult(new WirelessResult(WirelessOutcome.Unsupported));
            return Execute(operation, address, code, token);
        }
        private async Task<WirelessResult> Execute(WirelessOperation operation, string address, string code, CancellationToken token)
        {
            if (token.IsCancellationRequested) return new WirelessResult(WirelessOutcome.Cancelled);
            try
            {
                WirelessResult result = await transport.ExecuteAsync(operation, address, code, token).ConfigureAwait(false);
                if (!capability.IsCurrent) return new WirelessResult(WirelessOutcome.UnknownCapability, result.CleanupComplete);
                if (result.Outcome == WirelessOutcome.Success && result.CleanupComplete && operation == WirelessOperation.Connect) Remember(address);
                return result;
            }
            catch (OperationCanceledException) { return new WirelessResult(WirelessOutcome.Cancelled, false); }
            catch { return new WirelessResult(WirelessOutcome.CleanupPending, false); }
        }
        public Task<WirelessResult> PairAsync(string pairEndpoint, string code, bool explicitRequest, CancellationToken token) { return Invoke(WirelessOperation.Pair, pairEndpoint, code, explicitRequest, token); }
        public Task<WirelessResult> ConnectAsync(string connectEndpoint, bool explicitRequest, CancellationToken token) { return Invoke(WirelessOperation.Connect, connectEndpoint, null, explicitRequest, token); }
        public Task<WirelessResult> DisconnectAsync(string connectEndpoint, bool explicitRequest, CancellationToken token) { return Invoke(WirelessOperation.Disconnect, connectEndpoint, null, explicitRequest, token); }
        public IList<string> Recent() { lock (gate) return new ReadOnlyCollection<string>(recent.ToArray()); }
        public void Remember(string endpoint) { string value = Canonical(endpoint); if (value == null) throw new ArgumentException("BadAddress"); lock (gate) { recent.Remove(value); recent.Insert(0, value); if (recent.Count > 5) recent.RemoveAt(5); } }
        public void Forget(string endpoint) { string value = Canonical(endpoint); lock (gate) recent.Remove(value); }
        public Task<bool> StopAsync(TimeSpan deadline, CancellationToken token) { lock (gate) closed = true; return transport.StopAsync(deadline, token); }
    }
    internal sealed class OwnedWirelessTransport : IWirelessTransport
    {
        private readonly string executable;
        private readonly IWirelessOperationGuard guard;
        private readonly Func<bool> authorized;
        private readonly ProcessRunner runner = new ProcessRunner(4096);
        private readonly SemaphoreSlim single = new SemaphoreSlim(1, 1);
        private readonly object gate = new object();
        private Task stop, cleanup;
        private bool closed;
        public OwnedWirelessTransport(string executable, IWirelessOperationGuard guard, Func<bool> authorized)
        { if (string.IsNullOrWhiteSpace(executable) || guard == null || authorized == null) throw new ArgumentException("wireless dependencies"); this.executable = executable; this.guard = guard; this.authorized = authorized; }
        public async Task<WirelessResult> ExecuteAsync(WirelessOperation operation, string endpoint, string code, CancellationToken token)
        {
            if (!Enum.IsDefined(typeof(WirelessOperation), operation)) return new WirelessResult(WirelessOutcome.Rejected);
            string host; int port; if (!WirelessAdb.TryParseEndpoint(endpoint, out host, out port)) return new WirelessResult(WirelessOutcome.BadAddress);
            if (operation == WirelessOperation.Pair && !Regex.IsMatch(code ?? "", @"\A[0-9]{6}\z")) return new WirelessResult(WirelessOutcome.BadCode);
            lock (gate) if (closed) return new WirelessResult(WirelessOutcome.Closed);
            try { if (!await single.WaitAsync(0, token).ConfigureAwait(false)) return new WirelessResult(WirelessOutcome.Busy); }
            catch (OperationCanceledException) { return new WirelessResult(WirelessOutcome.Cancelled); }
            IDisposable lease = null; bool deferred = false;
            try
            {
                lease = await guard.AcquireAsync(endpoint, token).ConfigureAwait(false);
                if (lease == null) return new WirelessResult(WirelessOutcome.Busy);
                lock (gate) if (closed) return new WirelessResult(WirelessOutcome.Closed);
                if (!authorized()) return new WirelessResult(WirelessOutcome.UnknownCapability);
                string command = operation == WirelessOperation.Pair ? "pair" : operation == WirelessOperation.Connect ? "connect" : "disconnect";
                string[] args = operation == WirelessOperation.Pair ? new[] { command, endpoint, code } : new[] { command, endpoint };
                ProcessResult result = await runner.RunAsync(new ProcessRequest(executable, "wireless", args), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (!result.CleanupComplete)
                {
                    IDisposable retained = lease; lease = null; deferred = true;
                    cleanup = Task.Run(async delegate { while (runner.ActiveCount != 0) await Task.Delay(25).ConfigureAwait(false); retained.Dispose(); single.Release(); });
                    return new WirelessResult(WirelessOutcome.CleanupPending, false);
                }
                return Parse(operation, result, endpoint);
            }
            catch (OperationCanceledException) { return new WirelessResult(WirelessOutcome.Cancelled); }
            catch { return new WirelessResult(WirelessOutcome.Rejected); }
            finally { if (lease != null) lease.Dispose(); if (!deferred) single.Release(); }
        }
        internal static WirelessResult Parse(WirelessOperation operation, ProcessResult result, string expectedEndpoint = null)
        {
            if (!result.CleanupComplete) return new WirelessResult(WirelessOutcome.CleanupPending, false);
            if (result.Outcome == ProcessOutcome.Cancelled) return new WirelessResult(WirelessOutcome.Cancelled);
            if (result.Outcome == ProcessOutcome.TimedOut) return new WirelessResult(WirelessOutcome.TimedOut);
            if (result.Truncated) return new WirelessResult(WirelessOutcome.Rejected);
            string output = (result.Output ?? "").Trim();
            if (output.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0) return new WirelessResult(WirelessOutcome.Unsupported);
            if (result.Outcome != ProcessOutcome.Success) return new WirelessResult(WirelessOutcome.Rejected);
            bool ok = operation == WirelessOperation.Pair ? output.StartsWith("Successfully paired to ", StringComparison.Ordinal)
                : operation == WirelessOperation.Connect ? output.StartsWith("connected to ", StringComparison.Ordinal) || output.StartsWith("already connected to ", StringComparison.Ordinal)
                : output.StartsWith("disconnected ", StringComparison.Ordinal);
            if (ok && expectedEndpoint != null)
            {
                if (operation == WirelessOperation.Pair) ok = Regex.IsMatch(output, "\\ASuccessfully paired to " + Regex.Escape(expectedEndpoint) + @"(?: \[guid=[A-Za-z0-9_.:\-]{1,256}\])?\z");
                else if (operation == WirelessOperation.Connect) ok = output == "connected to " + expectedEndpoint || output == "already connected to " + expectedEndpoint;
                else ok = output == "disconnected " + expectedEndpoint;
            }
            return new WirelessResult(ok ? WirelessOutcome.Success : WirelessOutcome.Rejected);
        }
        public async Task<bool> StopAsync(TimeSpan deadline, CancellationToken token)
        {
            if (deadline.TotalMilliseconds < 1 || deadline.TotalMinutes > 5) throw new ArgumentOutOfRangeException("deadline");
            Task owned; lock (gate) { closed = true; if (stop == null) stop = Task.Run(() => runner.StopAllAsync()); owned = stop; }
            await Task.WhenAny(owned, Task.Delay(deadline, token)).ConfigureAwait(false);
            return owned.Status == TaskStatus.RanToCompletion && runner.ActiveCount == 0 && single.CurrentCount == 1 && (cleanup == null || cleanup.Status == TaskStatus.RanToCompletion);
        }
    }
}
