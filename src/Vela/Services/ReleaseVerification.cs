using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Vela.Services;

internal sealed record ReleaseFileCheck(string Sha256, bool TrustedSignature, uint TrustStatus);
internal static class ReleaseVerification
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo { public uint Size; public IntPtr Path, Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint UiChoice, Revocation, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State, Url;
        public uint ProviderFlags, Context;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    internal static ReleaseFileCheck Inspect(string path)
    {
        if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".msi")) throw new ArgumentException("Alege un instalator EXE sau MSI.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var name = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = name }, infoPointer, false);
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = infoPointer,
                ProviderFlags = 0x1000 // Cache-only trust verification: no background certificate network requests.
            };
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return new(hash, status == 0, status);
        }
        finally { Marshal.FreeHGlobal(infoPointer); Marshal.FreeCoTaskMem(name); }
    }
}
