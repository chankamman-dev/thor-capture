using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace ThorCapture.Backend
{
    [Flags]
    internal enum DeviceCapabilities
    {
        None = 0, Capture = 1, VideoPreview = 2, Recording = 4,
        InputObservation = 8, RemoteControl = 16, CocoonRead = 32, CocoonWrite = 64
    }

    internal sealed class DisplayTarget
    {
        public readonly int LogicalId, Width, Height;
        public readonly string PhysicalId, Role;
        public DisplayTarget(int logicalId, string physicalId, string role, int width, int height)
        {
            ulong physical;
            if (logicalId < 0 || !Regex.IsMatch(physicalId ?? "", @"\A[0-9]{1,20}\z")
                || !ulong.TryParse(physicalId, NumberStyles.None, CultureInfo.InvariantCulture, out physical)
                || (role != "upper" && role != "lower") || width < 1 || height < 1 || width > 16384 || height > 16384)
                throw new ArgumentException("Invalid display target");
            LogicalId = logicalId; PhysicalId = physical.ToString(CultureInfo.InvariantCulture); Role = role; Width = width; Height = height;
        }
    }

    // A connection is immutable. Retiring it cancels work, but leases retain the cancellation source until cleanup.
    internal sealed class DeviceSession
    {
        private readonly object gate = new object();
        private readonly DisplayTarget[] displays;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool connected = true, retiring;
        private int references = 1;
        public readonly Guid Id;
        public readonly long Generation;
        public readonly string Serial;
        public readonly DeviceCapabilities Capabilities;
        public readonly CancellationToken Token;
        public DisplayTarget[] Displays { get { return (DisplayTarget[])displays.Clone(); } }
        public bool Connected { get { lock (gate) return connected; } }

        internal DeviceSession(string serial, long generation, DeviceCapabilities capabilities, DisplayTarget[] targets)
        {
            Serial = serial; Generation = generation; Capabilities = capabilities;
            displays = (DisplayTarget[])targets.Clone(); Id = Guid.NewGuid(); Token = cancellation.Token;
        }
        public DisplayTarget Display(string role)
        {
            foreach (DisplayTarget target in displays) if (target.Role == role) return target;
            return null;
        }
        internal bool Hold()
        {
            lock (gate) { if (!connected) return false; references++; return true; }
        }
        internal void Release()
        {
            bool dispose;
            lock (gate) { references--; dispose = references == 0 && !connected && !retiring; }
            if (dispose) cancellation.Dispose();
        }
        internal void Retire()
        {
            lock (gate) { if (!connected) return; connected = false; retiring = true; }
            try { cancellation.Cancel(); }
            catch (AggregateException) { Log.Warn("Device session cancellation callback failed"); }
            finally
            {
                bool dispose;
                lock (gate) { retiring = false; references--; dispose = references == 0; }
                if (dispose) cancellation.Dispose();
            }
        }
    }

    internal sealed class DeviceSessionRegistry : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, DeviceSession> sessions = new Dictionary<string, DeviceSession>(StringComparer.Ordinal);
        private long generation;
        private bool disposed;

        internal static void ValidateSerial(string serial)
        {
            if (!Regex.IsMatch(serial ?? "", @"\A[A-Za-z0-9_.:\-\[\]]{1,256}\z")) throw new ArgumentException("Invalid device identity", "serial");
        }
        public DeviceSession Open(string serial, DeviceCapabilities capabilities, params DisplayTarget[] displays)
        {
            ValidateSerial(serial);
            if ((capabilities & ~(DeviceCapabilities)127) != 0) throw new ArgumentOutOfRangeException("capabilities");
            if (displays == null || displays.Length > 2) throw new ArgumentException("Invalid displays", "displays");
            HashSet<string> roles = new HashSet<string>(), physical = new HashSet<string>();
            HashSet<int> logical = new HashSet<int>();
            foreach (DisplayTarget target in displays)
                if (target == null || !roles.Add(target.Role) || !physical.Add(target.PhysicalId) || !logical.Add(target.LogicalId))
                    throw new ArgumentException("Duplicate display identity", "displays");
            DeviceSession old, current;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("DeviceSessionRegistry");
                sessions.TryGetValue(serial, out old);
                current = new DeviceSession(serial, ++generation, capabilities, displays); sessions[serial] = current;
            }
            if (old != null) old.Retire();
            return current;
        }
        public bool IsCurrent(DeviceSession session)
        {
            if (session == null) return false;
            lock (gate)
            {
                DeviceSession current;
                return !disposed && sessions.TryGetValue(session.Serial, out current) && ReferenceEquals(current, session) && session.Connected;
            }
        }
        internal DeviceSession Find(string serial, Guid id)
        {
            lock (gate) { DeviceSession found; return serial != null && !disposed && sessions.TryGetValue(serial, out found) && found.Id == id && found.Connected ? found : null; }
        }
        public void Disconnect(string serial)
        {
            DeviceSession old;
            lock (gate) { if (!sessions.TryGetValue(serial, out old)) return; sessions.Remove(serial); }
            old.Retire();
        }
        public void Dispose()
        {
            DeviceSession[] retired;
            lock (gate)
            {
                if (disposed) return; disposed = true;
                retired = new List<DeviceSession>(sessions.Values).ToArray(); sessions.Clear();
            }
            foreach (DeviceSession session in retired) session.Retire();
        }
    }
}
