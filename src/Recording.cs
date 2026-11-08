using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ThorCapture
{
    // Owns one remote recording. Never stops another app's screenrecord process.
    internal sealed class Recording
    {
        internal readonly string Serial;
        internal readonly string Remote;
        internal readonly string Destination;
        private readonly DeviceDisplay display;
        internal Recording(string serial, DeviceDisplay target, string directory)
            : this(serial, target, directory, NewId()) { }

        internal static string NewId()
        { return DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        internal Recording(string serial, DeviceDisplay target, string directory, string id)
        {
            Serial = serial;
            display = target;
            Remote = "/data/local/tmp/thor-record-" + display.Slug + "-" + id + ".mp4";
            Destination = Path.Combine(directory, "thor-" + display.Slug + "-" + id + ".mp4");
        }

        private AdbResult Run(string command, int timeout)
        {
            return Adb.Run("-s " + Serial + " " + command, timeout);
        }

        internal void CheckSupport()
        {
            AdbResult help = Run("shell screenrecord --help", 10000);
            if (!(help.Out + help.Err).Contains("--display-id"))
                throw new IOException("此设备未提供按物理屏幕录制的 screenrecord；无法可靠选择上下屏。" + help.Err);
        }

        internal void Stop()
        {
            AdbResult pid = Run("shell cat " + Remote + ".pid", 5000);
            string value = pid.Out.Trim();
            if (pid.Code != 0 || !Regex.IsMatch(value, @"^[1-9][0-9]*$"))
                throw new IOException("录屏进程尚未就绪或设备已断开，可稍后再停止；最长 3 分钟自动结束。");
            AdbResult cmd = Run("shell cat /proc/" + value + "/cmdline", 5000);
            if (cmd.Code != 0) return; // Already finished naturally.
            if (!cmd.Out.Contains(Remote) || !cmd.Out.Contains("screenrecord"))
                throw new IOException("录屏进程标识已变化，未发送停止信号。");
            AdbResult stop = Run("shell kill -2 " + value, 5000);
            if (stop.Code != 0) throw new IOException("停止录屏失败：" + stop.Err);
        }

        internal string Execute()
        {
            // exec keeps the PID stable; SIGINT lets MediaMuxer finalize the MP4.
            string command = "shell \"echo $$ > " + Remote + ".pid; exec screenrecord --display-id "
                + display.PhysicalId + " --bit-rate 8000000 --time-limit 180 " + Remote + "\"";
            AdbResult result = Run(command, 200000);
            if (result.Code != 0) throw new IOException("录屏未正常结束：" + result.Err + "。设备文件保留于 " + Remote);
            return Download();
        }

        internal string Download()
        {
            string partial = Destination + ".partial";
            AdbResult pull = Run("pull " + Remote + " \"" + partial + "\"", 60000);
            try
            {
                if (pull.Code != 0) throw new IOException("录屏下载失败：" + pull.Err);
                ValidateMp4(partial);
                File.Move(partial, Destination);
                Run("shell rm -f " + Remote + " " + Remote + ".pid", 5000);
                return Destination;
            }
            catch (Exception failure)
            {
                throw new IOException(failure.Message + "。原始录屏保留在设备：" + Remote);
            }
        }

        internal static void ValidateMp4(string path)
        {
            bool ftyp = false, moov = false, mdat = false;
            using (FileStream file = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(file))
            {
                while (file.Position < file.Length)
                {
                    long start = file.Position;
                    if (file.Length - start < 8) throw new IOException("MP4 尾部不完整");
                    byte[] sizeBytes = reader.ReadBytes(4);
                    long size = ((long)sizeBytes[0] << 24) | ((long)sizeBytes[1] << 16) | ((long)sizeBytes[2] << 8) | sizeBytes[3];
                    string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    long header = 8;
                    if (size == 1)
                    {
                        if (file.Length - file.Position < 8) throw new IOException("MP4 长度信息不完整");
                        byte[] large = reader.ReadBytes(8);
                        if ((large[0] & 128) != 0) throw new IOException("MP4 长度无效");
                        size = 0;
                        foreach (byte b in large) size = (size << 8) | b;
                        header = 16;
                    }
                    if (size == 0) size = file.Length - start;
                    if (size < header || size > file.Length - start) throw new IOException("MP4 文件不完整");
                    if (type == "ftyp" && size > header) ftyp = true;
                    if (type == "moov" && size > header) moov = true;
                    if (type == "mdat" && size > header) mdat = true;
                    file.Position = start + size;
                }
            }
            if (!ftyp || !moov || !mdat) throw new IOException("录屏缺少视频数据或结束索引，未作为成品保存");
        }
    }
}
