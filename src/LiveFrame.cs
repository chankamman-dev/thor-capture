using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ThorCapture
{
    internal static class LiveFrame
    {
        // screencap writes packed rows; modern Android adds a colorspace word.
        public static Bitmap Decode(byte[] bytes)
        { return Decode(bytes,-1); }
        internal static Bitmap Decode(byte[] bytes,int maxHeight)
        {
            if(bytes==null || bytes.Length<12) throw new IOException("取景数据不完整");
            int w=BitConverter.ToInt32(bytes,0),h=BitConverter.ToInt32(bytes,4),format=BitConverter.ToInt32(bytes,8);
            if(w<1 || h<1 || w>8192 || h>8192 || (format!=1 && format!=2 && format!=5)) throw new IOException("不支持的原始取景格式");
            long size=(long)w*h*4; int header=(int)(bytes.LongLength-size);
            if(header!=12 && header!=16) throw new IOException("取景长度不匹配");
            if(header==16 && BitConverter.ToInt32(bytes,12)>1) throw new IOException("广色域画面改用 PNG 取景");
            Bitmap full=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            try
            {
                BitmapData data=full.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row=new byte[w*4];
                    for(int y=0;y<h;y++)
                    {
                        Buffer.BlockCopy(bytes,header+y*row.Length,row,0,row.Length);
                        for(int x=0;x<row.Length;x+=4)
                        { if(format!=5) { byte r=row[x]; row[x]=row[x+2]; row[x+2]=r; } row[x+3]=255; }
                        Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),row.Length);
                    }
                }
                finally { full.UnlockBits(data); }
                return maxHeight<0?Resize(full):ResizePng(full,maxHeight);
            }
            finally { full.Dispose(); }
        }
        internal static Bitmap ResizePng(Image source,int maxHeight)
        {
            if(maxHeight==0) return new Bitmap(source);
            Size size=new PreviewOptions { MaxHeight=maxHeight }.SizeFor(new DeviceDisplay {Width=source.Width,Height=source.Height});
            Bitmap frame=new Bitmap(size.Width,size.Height);
            using(Graphics g=Graphics.FromImage(frame)) { g.InterpolationMode=InterpolationMode.Bilinear; g.DrawImage(source,0,0,frame.Width,frame.Height); }
            return frame;
        }
        internal static Bitmap CapturePng(string serial,string physicalId,int maxHeight)
        {
            string error;
            byte[] png=Adb.RunBinary("-s "+serial+" exec-out screencap -p -d "+physicalId,5000,out error);
            if(png==null) throw new IOException(error);
            using(MemoryStream stream=new MemoryStream(png))
            using(Image source=Image.FromStream(stream)) return ResizePng(source,maxHeight);
        }
        internal static Bitmap CaptureConfigured(string serial,string physicalId,int maxHeight)
        {
            string error;
            byte[] bytes=Adb.RunBinary("-s "+serial+" exec-out screencap -d "+physicalId,5000,out error);
            try { return Decode(bytes,maxHeight); }
            catch(IOException) { return CapturePng(serial,physicalId,maxHeight); }
        }
        private static Bitmap Resize(Image source)
        {
            double ratio=Math.Min(1,850.0/source.Width);
            Bitmap small=new Bitmap(Math.Max(1,(int)(source.Width*ratio)),Math.Max(1,(int)(source.Height*ratio)));
            using(Graphics g=Graphics.FromImage(small)) { g.InterpolationMode=InterpolationMode.Bilinear; g.DrawImage(source,0,0,small.Width,small.Height); }
            return small;
        }
        public static Bitmap Capture(string serial,string physicalId,bool raw,out bool usedRaw)
        {
            string error; usedRaw=false;
            if(raw)
            {
                byte[] bytes=Adb.RunBinary("-s "+serial+" exec-out screencap -d "+physicalId,5000,out error);
                try { Bitmap frame=Decode(bytes); usedRaw=true; return frame; } catch(IOException) { }
            }
            byte[] png=Adb.RunBinary("-s "+serial+" exec-out screencap -p -d "+physicalId,5000,out error);
            if(png==null) throw new IOException(error);
            using(MemoryStream stream=new MemoryStream(png))
            using(Image decoded=Image.FromStream(stream)) return Resize(decoded);
        }
    }
}
