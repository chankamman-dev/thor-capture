using System;
using System.Threading;
using System.Threading.Tasks;
using ThorCapture.Backend;

namespace ThorCapture.Features.RemoteControl
{
    [Flags]
    internal enum ControlSupport { Unknown = 0, Touch = 1, Navigation = 2, Keyboard = 4 }
    internal enum ControlAction { TouchDown, TouchMove, TouchUp, KeyDown, KeyUp, ReleaseAll }
    internal enum NavigationKey { Back = 4, Home = 3, Recent = 187 }
    internal enum ControlStartOutcome { Starting, Disabled, Unsupported, Busy, Disconnected, Closed }
    internal enum ControlEnqueueOutcome { Queued, Coalesced, Disabled, Invalid, Congested }
    internal sealed class ControlBinding
    {
        public const string Protocol = "thor.control/1";
        public readonly Guid SessionId;
        public readonly long Generation, MappingRevision;
        public readonly string Serial;
        private readonly DisplayTarget[] displays;
        public DisplayTarget[] Displays { get { return (DisplayTarget[])displays.Clone(); } }
        internal ControlBinding(DeviceSession session, long revision)
        { SessionId = session.Id; Generation = session.Generation; Serial = session.Serial; MappingRevision = revision; displays = session.Displays; }
        public DisplayTarget Display(string role) { foreach (DisplayTarget d in displays) if (d.Role == role) return d; return null; }
    }
    // Internal semantic contract, not a claim of scrcpy wire compatibility.
    internal sealed class ControlCommand
    {
        public readonly Guid SessionId;
        public readonly long Generation, MappingRevision, Sequence;
        public readonly ControlAction Action;
        public readonly DisplayTarget Display;
        public readonly int PointerId, X, Y, Key;
        internal ControlCommand(ControlBinding b, long sequence, ControlAction action, DisplayTarget display, int pointer, int x, int y, int key)
        { SessionId = b.SessionId; Generation = b.Generation; MappingRevision = b.MappingRevision; Sequence = sequence; Action = action; Display = display; PointerId = pointer; X = x; Y = y; Key = key; }
    }
    internal sealed class ControlAcknowledgment
    {
        public readonly Guid SessionId;
        public readonly long Generation, MappingRevision, Sequence;
        public readonly bool Accepted;
        public ControlAcknowledgment(Guid session, long generation, long revision, long sequence, bool accepted)
        { SessionId = session; Generation = generation; MappingRevision = revision; Sequence = sequence; Accepted = accepted; }
        public static ControlAcknowledgment For(ControlCommand command)
        { return new ControlAcknowledgment(command.SessionId, command.Generation, command.MappingRevision, command.Sequence, true); }
        internal bool Matches(ControlCommand c)
        { return Accepted && SessionId == c.SessionId && Generation == c.Generation && MappingRevision == c.MappingRevision && Sequence == c.Sequence; }
    }
    internal interface IControlTransport
    {
        // The implementation must own only its channel, bind exactly this serial/displays,
        // and validate its actual server/protocol before returning true. Unknown is zero.
        string Protocol { get; }
        ControlSupport Support { get; }
        Task<bool> OpenAsync(ControlBinding binding, CancellationToken token);
        Task<ControlAcknowledgment> SendAsync(ControlCommand command, CancellationToken token);
        // True confirms local resources and the owned control channel are closed;
        // it does not prove that a disconnected device applied releases.
        Task<bool> CloseAsync();
    }
}
