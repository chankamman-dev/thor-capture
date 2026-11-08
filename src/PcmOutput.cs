using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace ThorCapture
{
    internal interface IPcmOutput : IDisposable
    {
        void Write(byte[] samples,Func<bool> active);
        void Pause(bool value);
        void Reset();
    }
    // One 20 ms block at a time, rather than seconds of queued audio after a seek.
    internal sealed class PcmOutput : IPcmOutput
    {
        [StructLayout(LayoutKind.Sequential,Pack=2)]
        private struct Format { internal ushort tag,channels; internal uint rate,bytes; internal ushort align,bits,extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header { internal IntPtr data; internal uint length,recorded; internal UIntPtr user; internal uint flags,loops; internal IntPtr next; internal UIntPtr reserved; }
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr output,uint device,ref Format format,IntPtr callback,IntPtr instance,uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr output,IntPtr header,uint size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr output,IntPtr header,uint size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr output,IntPtr header,uint size);
        [DllImport("winmm.dll")] private static extern int waveOutPause(IntPtr output);
        [DllImport("winmm.dll")] private static extern int waveOutRestart(IntPtr output);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr output);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr output);
        private readonly object gate=new object();
        private IntPtr device;
        internal PcmOutput()
        {
            Format format=new Format {tag=1,channels=2,rate=48000,bytes=192000,align=4,bits=16};
            Check(waveOutOpen(out device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0));
        }
        private static void Check(int result) { if(result!=0) throw new Win32Exception(result,"电脑音频输出不可用（waveOut "+result+"）"); }
        public void Write(byte[] samples,Func<bool> active)
        {
            IntPtr data=Marshal.AllocHGlobal(samples.Length),header=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Header)));
            uint size=(uint)Marshal.SizeOf(typeof(Header)); bool prepared=false;
            try
            {
                Marshal.Copy(samples,0,data,samples.Length);
                Marshal.StructureToPtr(new Header {data=data,length=(uint)samples.Length},header,false);
                lock(gate) { Check(waveOutPrepareHeader(device,header,size)); prepared=true; Check(waveOutWrite(device,header,size)); }
                while((((Header)Marshal.PtrToStructure(header,typeof(Header))).flags&1)==0)
                { if(!active()) { Reset(); break; } Thread.Sleep(2); }
            }
            finally
            {
                if(prepared) lock(gate) { if(waveOutUnprepareHeader(device,header,size)!=0) { waveOutReset(device); Check(waveOutUnprepareHeader(device,header,size)); } }
                Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data);
            }
        }
        public void Pause(bool value) { lock(gate) if(device!=IntPtr.Zero) { if(value) waveOutPause(device); else waveOutRestart(device); } }
        public void Reset() { lock(gate) if(device!=IntPtr.Zero) waveOutReset(device); }
        public void Dispose() { lock(gate) if(device!=IntPtr.Zero) { waveOutReset(device); waveOutClose(device); device=IntPtr.Zero; } }
    }
}
