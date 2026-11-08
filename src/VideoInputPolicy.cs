using System;
using System.IO;
namespace ThorCapture
{
    internal static class VideoInputPolicy
    {
        // Imported originals use a fixed container and cannot open network protocols.
        internal static string Options(string path,bool restricted)
        {
            if(!restricted)return "";
            string extension=Path.GetExtension(path).ToLowerInvariant();
            if(extension==".mp4")return " -protocol_whitelist file,pipe -f mov -enable_drefs 0";
            if(extension==".webm")return " -protocol_whitelist file,pipe -f matroska";
            throw new ArgumentException("Unsupported imported video container");
        }
    }
}
