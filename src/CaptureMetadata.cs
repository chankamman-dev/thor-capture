using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ThorCapture
{
    internal sealed class ForegroundSnapshot
    {
        public DateTime ObservedUtc = DateTime.UtcNow;
        public bool QueryFailed;
        public readonly Dictionary<int, string> Packages = new Dictionary<int, string>();

        public static ForegroundSnapshot Read(string serial)
        {
            DateTime started = DateTime.UtcNow;
            AdbResult result = Adb.Run("-s " + serial + " shell dumpsys activity activities", 8000);
            ForegroundSnapshot snapshot = result.Code == 0 ? Parse(result.Out) : new ForegroundSnapshot();
            snapshot.ObservedUtc = started;
            snapshot.QueryFailed = result.Code != 0;
            return snapshot;
        }

        public static ForegroundSnapshot Parse(string output)
        {
            ForegroundSnapshot snapshot = new ForegroundSnapshot();
            Dictionary<int, int> priorities = new Dictionary<int, int>();
            int display = -1;
            foreach (string line in (output ?? "").Split('\n'))
            {
                string text = line.Trim();
                Match header = Regex.Match(text, @"^Display #(\d+)\b");
                if (header.Success)
                {
                    if (!int.TryParse(header.Groups[1].Value, out display)) display = -1;
                    continue;
                }
                // Never attribute the global focused activity to the last display.
                if (Regex.IsMatch(text, @"^(mFocusedRootTask|mTopResumedActivity|mFocusedActivity|RootWindowContainer|ActivityTaskSupervisor|Activities waiting|ACTIVITY MANAGER)\b")) display = -1;
                if (display < 0) continue;
                Match activity = Regex.Match(text, @"^(topResumedActivity|mResumedActivity|ResumedActivity|Resumed)\s*[:=]\s*(.*)$");
                if (!activity.Success) continue;
                string key = activity.Groups[1].Value;
                int priority = key == "Resumed" || key == "ResumedActivity" ? 3 : key == "topResumedActivity" ? 2 : 1;
                Match component = Regex.Match(activity.Groups[2].Value, @"\b([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)/[A-Za-z0-9_.$]+");
                string package = component.Success ? component.Groups[1].Value : "";
                int previous;
                if (!priorities.TryGetValue(display, out previous) || priority > previous)
                {
                    priorities[display] = priority;
                    snapshot.Packages[display] = package;
                }
                else if (priority == previous && snapshot.Packages[display] != package)
                    snapshot.Packages[display] = ""; // Multiple resumed apps: unknown, not a guess.
            }
            return snapshot;
        }
    }

    internal sealed class CaptureMetadata
    {
        public string Package = "";
        public string Status = "unknown";
        public string Serial = "";
        public int DisplayId;
        public string PhysicalId = "";
        public DateTime CapturedUtc;
        public DateTime ObservedUtc;

        internal static string Sidecar(string image) { return image + ".thor-meta.txt"; }

        public static CaptureMetadata Create(ForegroundSnapshot snapshot, string serial, DeviceDisplay display, DateTime captured)
        {
            string package;
            snapshot.Packages.TryGetValue(display.LogicalId, out package);
            return new CaptureMetadata {
                Package = package ?? "", Status = snapshot.QueryFailed ? "query-failed" : string.IsNullOrEmpty(package) ? "unknown" : "identified",
                Serial = serial, DisplayId = display.LogicalId, PhysicalId = display.PhysicalId,
                CapturedUtc = captured, ObservedUtc = snapshot.ObservedUtc
            };
        }

        // Versioned UTF-8 sidecars require no additional framework assemblies.
        public void Save(string image)
        {
            string path = Sidecar(image);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new string[] {
                    "version=1", "package=" + Package, "status=" + Status,
                    "serial=" + Uri.EscapeDataString(Serial), "displayId=" + DisplayId.ToString(CultureInfo.InvariantCulture),
                    "physicalId=" + PhysicalId, "capturedUtc=" + CapturedUtc.ToString("o", CultureInfo.InvariantCulture),
                    "foregroundObservedUtc=" + ObservedUtc.ToString("o", CultureInfo.InvariantCulture)
                }, new UTF8Encoding(false));
                File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static CaptureMetadata Load(string image)
        {
            try
            {
                string path = Sidecar(image);
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
                Dictionary<string, string> fields = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int equal = line.IndexOf('=');
                    if (equal > 0) fields[line.Substring(0, equal)] = line.Substring(equal + 1);
                }
                if (fields["version"] != "1") return null;
                string package = fields["package"];
                if (package.Length > 255 || (package.Length > 0 && !Regex.IsMatch(package, @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*$"))) return null;
                return new CaptureMetadata {
                    Package = package, Status = fields["status"], Serial = Uri.UnescapeDataString(fields["serial"]),
                    DisplayId = int.Parse(fields["displayId"], CultureInfo.InvariantCulture), PhysicalId = fields["physicalId"],
                    CapturedUtc = DateTime.Parse(fields["capturedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    ObservedUtc = DateTime.Parse(fields["foregroundObservedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                };
            }
            catch { return null; } // Old captures and damaged sidecars still browse normally.
        }

        public static string AppName(string package)
        {
            return string.IsNullOrEmpty(package) ? "" : Settings.Get("appAlias." + package, package);
        }

        public bool Matches(string query)
        {
            return Package.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || AppName(Package).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
