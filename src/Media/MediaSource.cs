using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ThorCapture.Media
{
    internal sealed class MediaSource
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        internal string Name, Sha256, Identity, Package, LegacyKey;
        internal long Length;
        internal DateTime WriteUtc;
        internal ScreenRole Role;
        internal MediaKind Kind;
        internal bool HasMetadata;
        static string IdentityOf(FileStream stream)
        {
            FileInformation info;
            return GetFileInformationByHandle(stream.SafeFileHandle, out info)
                ? info.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture) + ":" + info.IndexHigh.ToString("X8", CultureInfo.InvariantCulture) + info.IndexLow.ToString("X8", CultureInfo.InvariantCulture) : "";
        }
        internal static bool Supported(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".mp4";
        }
        internal static MediaSource Read(string path, CancellationToken token)
        {
            FileInfo before = new FileInfo(path);
            if (!before.Exists || !Supported(path) || (before.Attributes & FileAttributes.ReparsePoint) != 0 || before.Length > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("Unsupported or unavailable media source");
            long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            string identity, sha;
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 hash = SHA256.Create())
            {
                identity = IdentityOf(file); byte[] buffer = new byte[65536]; int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, null, 0); }
                hash.TransformFinalBlock(buffer, 0, 0); sha = BitConverter.ToString(hash.Hash).Replace("-", "");
            }
            token.ThrowIfCancellationRequested(); FileInfo after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("Media source changed during scan");
            using (FileStream verify = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) if (IdentityOf(verify) != identity) throw new IOException("Media path was replaced during scan");
            string name = Path.GetFileName(path); Match legacy = Regex.Match(name, "^thor-(upper|lower)-(.+)\\.(png|mp4)$", RegexOptions.IgnoreCase);
            CaptureMetadata metadata = CaptureMetadata.Load(path);
            return new MediaSource { Name = name, Sha256 = sha, Identity = identity, Length = length, WriteUtc = modified,
                Package = metadata == null ? "" : metadata.Package, HasMetadata = metadata != null, Kind = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? MediaKind.Video : MediaKind.Image,
                Role = !legacy.Success ? ScreenRole.Unknown : legacy.Groups[1].Value.Equals("upper", StringComparison.OrdinalIgnoreCase) ? ScreenRole.Upper : ScreenRole.Lower,
                LegacyKey = legacy.Success ? legacy.Groups[3].Value.ToLowerInvariant() + "|" + legacy.Groups[2].Value.ToLowerInvariant() : "" };
        }
        internal MediaTrack Track(string id, ScreenRole role, bool changed)
        { return new MediaTrack(id, Name, Sha256, Identity, Package, role, Kind, changed ? TrackState.Changed : TrackState.Present, Length, WriteUtc); }
    }
}
