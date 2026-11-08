using System;
using System.Runtime.InteropServices;

namespace ThorCapture.Backend
{
    // Windows argv parsing preserves legacy quoted callers while migrating their process ownership.
    internal static class LegacyArguments
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr allocation);
        internal static string[] Parse(string arguments)
        {
            if (arguments == null || arguments.Length > 1024 * 1024 || arguments.IndexOf('\0') >= 0) throw new ArgumentException("Invalid legacy argv");
            int count; IntPtr argv = CommandLineToArgvW("\"owned-tool\" " + arguments, out count);
            if (argv == IntPtr.Zero) throw new ArgumentException("Invalid legacy argv");
            try { string[] result = new string[count - 1]; for (int i = 1; i < count; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)); return result; }
            finally { LocalFree(argv); }
        }
    }
}
