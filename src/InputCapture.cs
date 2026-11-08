using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ThorCapture
{
    [Flags]
    internal enum PadButton { A=1, B=2, X=4, Y=8, L1=16, R1=32, L2=64, R2=128, L3=256, R3=512, Up=1024, Down=2048, Left=4096, Right=8192, Select=16384, Start=32768, Home=65536, Back=131072 }

    internal sealed class PadState
    {
        public PadButton Buttons;
        public float LX, LY, RX, RY, LT, RT;
        public bool Animated;
        public float TiltX,TiltY;
        internal float[] Pressures;
        public float DirectionX { get { return (Down(PadButton.Right)?1:0)-(Down(PadButton.Left)?1:0); } }
        public float DirectionY { get { return (Down(PadButton.Down)?1:0)-(Down(PadButton.Up)?1:0); } }
        public bool Down(PadButton button) { return (Buttons & button) != 0; }
        public PadState Copy()
        {
            PadState copy = (PadState)MemberwiseClone();
            if (Pressures != null) copy.Pressures = (float[])Pressures.Clone();
            return copy;
        }
        public float Press(PadButton button)
        {
            if (!Animated || Pressures == null) return Down(button) ? 1 : 0;
            int bit = (int)button, index = 0;
            while (bit > 1) { bit >>= 1; index++; }
            return index < Pressures.Length ? Pressures[index] : 0;
        }
        public void Key(PadButton button, bool down) { if (down) Buttons |= button; else Buttons &= ~button; }
        public static PadState Blend(PadState a, PadState b, float amount)
        {
            PadState value = b.Copy();
            value.LX=a.LX+(b.LX-a.LX)*amount; value.LY=a.LY+(b.LY-a.LY)*amount;
            value.RX=a.RX+(b.RX-a.RX)*amount; value.RY=a.RY+(b.RY-a.RY)*amount;
            value.Animated=true;
            value.Pressures = new float[18];
            for (int i = 0; i < value.Pressures.Length; i++)
            {
                PadButton button = (PadButton)(1 << i);
                value.Pressures[i] = a.Press(button) + (b.Press(button) - a.Press(button)) * amount;
            }
            value.TiltX=a.TiltX+(b.DirectionX-a.TiltX)*amount;
            value.TiltY=a.TiltY+(b.DirectionY-a.TiltY)*amount;
            return value;
        }
    }

    internal sealed class AxisRange
    {
        public int Min, Max, Value, Flat;
        public float Normalize(int value, bool trigger)
        {
            if (Max <= Min) return 0;
            double unit = (value - (double)Min) / (Max - (double)Min);
            if (trigger) return (float)Math.Max(0, Math.Min(1, unit));
            double signed = Math.Max(-1, Math.Min(1, unit*2-1));
            double dead = Math.Min(0.3, Math.Max(0.06, Flat*2.0/(Max-(double)Min)));
            return Math.Abs(signed) <= dead ? 0 : (float)(Math.Sign(signed)*(Math.Abs(signed)-dead)/(1-dead));
        }
    }

    internal sealed class InputDevice
    {
        public string Path, Name;
        public readonly Dictionary<int, AxisRange> Axes = new Dictionary<int, AxisRange>();
        public bool HasButtons;
        public override string ToString() { return Name + " · " + Path; }
        private static readonly Dictionary<string,int> AxisCodes = new Dictionary<string,int> {
            {"ABS_X",0},{"ABS_Y",1},{"ABS_Z",2},{"ABS_RX",3},{"ABS_RY",4},{"ABS_RZ",5},
            {"ABS_BRAKE",10},{"ABS_GAS",9},{"ABS_HAT0X",16},{"ABS_HAT0Y",17}
        };
        public static List<InputDevice> Parse(string text)
        {
            List<InputDevice> result = new List<InputDevice>(); InputDevice current = null;
            foreach (string line in text.Split('\n'))
            {
                Match device = Regex.Match(line, @"add device \d+:\s*(/dev/input/event\d+)");
                if (device.Success) { current = new InputDevice { Path=device.Groups[1].Value, Name=device.Groups[1].Value }; result.Add(current); continue; }
                if (current == null) continue;
                Match name = Regex.Match(line, "name:\\s*\"([^\"]+)\"");
                if (name.Success) current.Name = name.Groups[1].Value;
                if (Regex.IsMatch(line, @"\bBTN_(A|B|X|Y|SOUTH|EAST|NORTH|WEST|GAMEPAD)\b")) current.HasButtons = true;
                Match axis = Regex.Match(line, @"(ABS_[A-Z0-9]+)\s*:\s*value\s+(-?\d+),\s*min\s+(-?\d+),\s*max\s+(-?\d+)(?:,\s*fuzz\s+\d+,\s*flat\s+(\d+))?");
                int code;
                if (axis.Success && AxisCodes.TryGetValue(axis.Groups[1].Value, out code))
                    current.Axes[code] = new AxisRange { Value=int.Parse(axis.Groups[2].Value), Min=int.Parse(axis.Groups[3].Value), Max=int.Parse(axis.Groups[4].Value), Flat=axis.Groups[5].Success ? int.Parse(axis.Groups[5].Value) : 0 };
            }
            result.RemoveAll(d => !d.HasButtons || !d.Axes.ContainsKey(0) || !d.Axes.ContainsKey(1));
            return result;
        }
        public static List<InputDevice> Discover(string serial)
        {
            List<TouchDevice> touches;
            return Discover(serial, out touches);
        }

        // One `getevent -lp` answers both questions: gamepads and touch panels.
        public static List<InputDevice> Discover(string serial, out List<TouchDevice> touches)
        {
            AdbResult result = Adb.Run("-s " + serial + " shell getevent -lp", 10000);
            if (result.Code != 0) throw new IOException("无法读取输入设备：" + result.Err);
            touches = TouchDevice.Parse(result.Out);
            List<InputDevice> devices = Parse(result.Out);
            if (devices.Count == 0) throw new IOException("未找到可读取的游戏手柄输入节点。系统可能限制 getevent 权限；本工具不会自动提权。" + result.Err);
            return devices;
        }
    }

    internal sealed class InputProfile
    {
        public readonly Dictionary<int, PadButton> Keys = new Dictionary<int, PadButton>();
        public int RightX, RightY, LeftTrigger, RightTrigger;
        public InputProfile(InputDevice device)
        {
            PadButton[] buttons = { PadButton.A,PadButton.B,PadButton.X,PadButton.Y,PadButton.L1,PadButton.R1,PadButton.L2,PadButton.R2,PadButton.L3,PadButton.R3,PadButton.Select,PadButton.Start,PadButton.Home,PadButton.Back,PadButton.Up,PadButton.Down,PadButton.Left,PadButton.Right };
            int[] codes = { 0x130,0x131,0x133,0x134,0x136,0x137,0x138,0x139,0x13d,0x13e,0x13a,0x13b,0x13c,0x9e,0x220,0x221,0x222,0x223 };
            for (int i=0;i<codes.Length;i++) Keys[codes[i]]=buttons[i];
            RightX=device.Axes.ContainsKey(3)&&device.Axes.ContainsKey(4)?3:2; RightY=RightX==3?4:5;
            LeftTrigger=device.Axes.ContainsKey(10)?10:RightX==3?2:-1;
            RightTrigger=device.Axes.ContainsKey(9)?9:RightX==3?5:-1;
        }
        public void Load(string prefix)
        {
            int axis;
            if(int.TryParse(Settings.Get(prefix+".rightX",""),out axis)) RightX=axis;
            if(int.TryParse(Settings.Get(prefix+".rightY",""),out axis)) RightY=axis;
            foreach (PadButton button in Enum.GetValues(typeof(PadButton)))
            {
                int code;
                if (int.TryParse(Settings.Get(prefix+"."+button, ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) Bind(button,code);
            }
        }
        public void Bind(PadButton button,int code)
        {
            List<int> remove=new List<int>(); foreach(var pair in Keys) if(pair.Value==button) remove.Add(pair.Key);
            foreach(int old in remove) Keys.Remove(old); if(code>=0) Keys[code]=button;
        }
    }

    internal sealed class PadDecoder
    {
        private readonly InputDevice device;
        public readonly InputProfile Profile;
        public PadState Current = new PadState();
        public int LastKey, KeySequence;
        public double DeviceSeconds;
        public bool Lost;
        public PadDecoder(InputDevice source,InputProfile profile)
        {
            device=source; Profile=profile;
            foreach(var axis in device.Axes) ApplyAxis(axis.Key,axis.Value.Value);
        }
        private void ApplyAxis(int code,int value)
        {
            if(code==16) { Current.Key(PadButton.Left,value<0); Current.Key(PadButton.Right,value>0); return; }
            if(code==17) { Current.Key(PadButton.Up,value<0); Current.Key(PadButton.Down,value>0); return; }
            AxisRange range; if(!device.Axes.TryGetValue(code,out range)) return;
            if(code==0) Current.LX=range.Normalize(value,false);
            else if(code==1) Current.LY=range.Normalize(value,false);
            else if(code==Profile.RightX) Current.RX=range.Normalize(value,false);
            else if(code==Profile.RightY) Current.RY=range.Normalize(value,false);
            else if(code==Profile.LeftTrigger) Current.LT=range.Normalize(value,true);
            else if(code==Profile.RightTrigger) Current.RT=range.Normalize(value,true);
        }
        public bool Feed(string line)
        {
            Match match=Regex.Match(line,@"\[\s*([0-9]+\.[0-9]+)\]\s*(?:/dev/input/event\d+:\s*)?([0-9a-fA-F]{4})\s+([0-9a-fA-F]{4})\s+([0-9a-fA-F]{8})\s*$");
            if(!match.Success) return false;
            DeviceSeconds=double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            int type=Convert.ToInt32(match.Groups[2].Value,16),code=Convert.ToInt32(match.Groups[3].Value,16);
            int value=unchecked((int)uint.Parse(match.Groups[4].Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture));
            if(type==0 && code==3) { Current=new PadState(); Lost=true; return true; }
            if(Lost) return false;
            if(type==1)
            {
                if(value==1) { LastKey=code; KeySequence++; }
                PadButton button; if(Profile.Keys.TryGetValue(code,out button)) Current.Key(button,value!=0);
            }
            else if(type==3) ApplyAxis(code,value);
            return type==0 && code==0;
        }
    }

    internal sealed class InputMonitor : IDisposable
    {
        private readonly object gate=new object();
        private Process process; private Thread reader;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private double origin;
        private StreamWriter timeline;
        private string timelinePath;
        private PadState committed=new PadState();
        public readonly PadDecoder Decoder;
        public string Error { get; private set; }
        private volatile bool stopping;
        // Touch panels read by their own getevent processes; failures never stop the gamepad.
        private readonly List<Process> touchProcesses=new List<Process>();
        private readonly List<Thread> touchReaders=new List<Thread>();
        private readonly List<TouchDecoder> touchDecoders=new List<TouchDecoder>();
        private readonly TouchHistory liveTouches=new TouchHistory(1500);
        private TouchPoint[] touches=new TouchPoint[0];
        public string TouchError { get; private set; }
        public int TouchPanels { get { lock(gate) return touchDecoders.Count; } }
        public InputMonitor(InputDevice device,InputProfile profile) { Decoder=new PadDecoder(device,profile); }
        // What the touch feedback should show right now.
        public TouchVisual Touches { get { lock(gate) return liveTouches.At(clock.Elapsed.TotalMilliseconds); } }
        public PadState State { get { lock(gate) return committed.Copy(); } }
        public int LastKey { get { lock(gate) return Decoder.LastKey; } }
        public int KeySequence { get { lock(gate) return Decoder.KeySequence; } }
        public void Bind(PadButton button,int code) { lock(gate) Decoder.Profile.Bind(button,code); }
        public void Calibrate(Dictionary<int,PadButton> bindings,int x,int y)
        {
            lock(gate)
            {
                Decoder.Profile.Keys.Clear();
                foreach(var pair in bindings) Decoder.Profile.Keys[pair.Key]=pair.Value;
                Decoder.Profile.RightX=x; Decoder.Profile.RightY=y;
                committed=new PadState();
                Decoder.Current.Buttons=0;
            }
        }
        public void Start(string serial,string node)
        {
            if(!Regex.IsMatch(node,@"^/dev/input/event\d+$")) throw new ArgumentException("输入节点无效");
            process=new Process { StartInfo=new ProcessStartInfo(Adb.ExePath,"-s "+serial+" shell getevent -t "+node) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
            process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(!stopping && !string.IsNullOrWhiteSpace(e.Data)) Error=e.Data; };
            Adb.StartBoundProcess(process,serial,10000); process.BeginErrorReadLine();
            reader=new Thread(delegate()
            {
                try
                {
                    string line;
                    while((line=process.StandardOutput.ReadLine())!=null)
                    {
                        lock(gate)
                        {
                            if(Decoder.Feed(line))
                            {
                                committed=Decoder.Current.Copy(); WriteState();
                                if(Decoder.Lost) { Error="输入事件丢失，请停止反馈后重新连接"; break; }
                            }
                        }
                        if(line.IndexOf("Permission denied",StringComparison.OrdinalIgnoreCase)>=0) Error="系统拒绝读取手柄事件";
                    }
                }
                catch(Exception failure) { if(!stopping) Error=failure.Message; }
                finally
                {
                    if(!stopping && Error==null) Error="手柄事件连接已结束";
                    lock(gate)
                    {
                        committed=new PadState(); WriteState();
                        if(timeline!=null && !stopping) { try { timeline.WriteLine("#warning=input-disconnected-or-lost"); } catch { } }
                    }
                }
            });
            reader.IsBackground=true; reader.Start();
            if(process.WaitForExit(300)) throw new IOException("按键反馈启动失败："+(Error??"getevent 已退出"));
        }
        public void StartTouch(string serial,List<TouchDecoder> decoders)
        {
            foreach(TouchDecoder decoder in decoders)
            {
                string node=decoder.Device.Path;
                if(!Regex.IsMatch(node,@"^/dev/input/event\d+$")) continue;
                Process touch=new Process { StartInfo=new ProcessStartInfo(Adb.ExePath,"-s "+serial+" shell getevent -t "+node) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
                touch.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(!stopping && !string.IsNullOrWhiteSpace(e.Data)) TouchError=e.Data; };
                Adb.StartBoundProcess(touch,serial,10000); touch.BeginErrorReadLine();
                lock(gate) { touchProcesses.Add(touch); touchDecoders.Add(decoder); }
                TouchDecoder owned=decoder;
                Thread reader=new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while((line=touch.StandardOutput.ReadLine())!=null)
                        {
                            if(line.IndexOf("Permission denied",StringComparison.OrdinalIgnoreCase)>=0) { TouchError="系统拒绝读取触摸事件"; break; }
                            lock(gate)
                            {
                                if(!owned.Feed(line)) continue;
                                owned.Lost=false; // SYN_DROPPED cleared the panel; later frames rebuild it
                                CommitTouches();
                            }
                        }
                    }
                    catch(Exception failure) { if(!stopping) TouchError=failure.Message; }
                    finally
                    {
                        if(!stopping && TouchError==null) TouchError="触摸事件连接已结束";
                        lock(gate) { touchDecoders.Remove(owned); CommitTouches(); }
                    }
                });
                reader.IsBackground=true; reader.Start();
                touchReaders.Add(reader);
            }
        }
        private void CommitTouches()
        {
            List<TouchPoint> all=new List<TouchPoint>();
            foreach(TouchDecoder decoder in touchDecoders) decoder.Collect(all);
            touches=all.ToArray();
            liveTouches.Add(clock.Elapsed.TotalMilliseconds,touches);
            WriteTouches();
        }
        private void WriteTouches()
        {
            if(timeline==null) return;
            try { timeline.WriteLine(TouchHistory.Format(Math.Max(0,clock.Elapsed.TotalMilliseconds-origin),touches)); }
            catch(Exception failure) { Error="按键时间线写入失败："+failure.Message; timeline.Dispose(); timeline=null; }
        }
        public void BeginTimeline(string video)
        {
            lock(gate)
            {
                timelinePath=video+".input.csv";
                timeline=new StreamWriter(new FileStream(timelinePath+".partial",FileMode.CreateNew,FileAccess.Write),new UTF8Encoding(false));
                timeline.AutoFlush=true; timeline.WriteLine("#thor-input-v1: host receipt milliseconds; video start approximate; deviceSeconds is raw kernel time");
                origin=clock.Elapsed.TotalMilliseconds; WriteState(); if(touchDecoders.Count>0) WriteTouches();
            }
        }
        private void WriteState()
        {
            if(timeline==null) return;
            try { timeline.WriteLine(PadTimeline.Format(Math.Max(0,clock.Elapsed.TotalMilliseconds-origin),Decoder.DeviceSeconds,committed)); }
            catch(Exception failure) { Error="按键时间线写入失败："+failure.Message; timeline.Dispose(); timeline=null; }
        }
        public void EndTimeline()
        {
            lock(gate)
            {
                if(timeline==null) return;
                WriteState();
                if(timeline==null) return;
                timeline.Dispose(); timeline=null;
                File.Move(timelinePath+".partial",timelinePath);
            }
        }
        public void Dispose()
        {
            stopping=true;
            try { if(process!=null && !process.HasExited) process.Kill(); } catch { }
            foreach(Process touch in touchProcesses.ToArray()) { try { if(!touch.HasExited) touch.Kill(); } catch { } }
            if(reader!=null) reader.Join(2000);
            foreach(Thread touchReader in touchReaders) touchReader.Join(1000);
            foreach(Process touch in touchProcesses.ToArray()) touch.Dispose();
            try { EndTimeline(); } finally { if(process!=null) process.Dispose(); }
        }
    }

    internal sealed class PadTimeline
    {
        public bool Interrupted;
        // Touch samples recorded alongside the gamepad (empty for older recordings).
        public readonly TouchHistory Touches=new TouchHistory(0);
        internal readonly List<double> Times=new List<double>();
        internal readonly List<PadState> States=new List<PadState>();
        public static string Format(double milliseconds,double deviceSeconds,PadState state)
        {
            return string.Format(CultureInfo.InvariantCulture,"{0:F3},{1:F6},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R}",milliseconds,deviceSeconds,(int)state.Buttons,state.LX,state.LY,state.RX,state.RY,state.LT,state.RT);
        }
        public static PadTimeline Load(string path)
        {
            PadTimeline timeline=new PadTimeline();
            if(new FileInfo(path).Length>64000000) throw new IOException("按键时间线过大");
            foreach(string line in File.ReadLines(path))
            {
                if(line.StartsWith("#warning=")) timeline.Interrupted=true;
                if(line.StartsWith("#") || line.Length==0) continue;
                if(line.StartsWith("T,"))
                {
                    double at; TouchPoint[] points;
                    TouchHistory.ParseRecord(line,out at,out points);
                    timeline.Touches.Add(at,points);
                    continue;
                }
                string[] c=line.Split(','); if(c.Length!=9) throw new IOException("按键时间线格式损坏");
                double time=double.Parse(c[0],CultureInfo.InvariantCulture);
                if(double.IsNaN(time)||double.IsInfinity(time)||time<0||(timeline.Times.Count>0 && time<timeline.Times[timeline.Times.Count-1])) throw new IOException("按键时间顺序无效");
                float[] axis=new float[6]; for(int i=0;i<6;i++) { axis[i]=float.Parse(c[i+3],CultureInfo.InvariantCulture); if(float.IsNaN(axis[i])||float.IsInfinity(axis[i])||Math.Abs(axis[i])>1) throw new IOException("摇杆数据无效"); }
                timeline.Times.Add(time); timeline.States.Add(new PadState { Buttons=(PadButton)int.Parse(c[2],CultureInfo.InvariantCulture),LX=axis[0],LY=axis[1],RX=axis[2],RY=axis[3],LT=axis[4],RT=axis[5] });
            }
            if(timeline.Times.Count==0) throw new IOException("按键时间线为空");
            return timeline;
        }
        public TouchVisual TouchesAt(double time) { return Touches.At(time); }
        public PadState At(double time)
        {
            int index=Times.BinarySearch(time); if(index<0) index=~index-1;
            return index<0 ? new PadState() : States[index].Copy();
        }
    }
}
