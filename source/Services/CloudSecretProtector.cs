using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace Archestro.MeetingVault.Services;

/// <summary>Protects provider credentials for the current Windows user using DPAPI.</summary>
public static class CloudSecretProtector
{
    private const int CryptprotectUiForbidden = 0x1;

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var inputPtr = Marshal.AllocHGlobal(inputBytes.Length);
        try
        {
            Marshal.Copy(inputBytes, 0, inputPtr, inputBytes.Length);
            var input = new DataBlob(inputBytes.Length, inputPtr);
            if (!CryptProtectData(ref input, "Archestro intelligence provider key", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect the provider key.");
            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return Convert.ToBase64String(protectedBytes);
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Array.Clear(inputBytes);
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return string.Empty;
        byte[] inputBytes;
        try { inputBytes = Convert.FromBase64String(protectedBase64); }
        catch (FormatException) { throw new SecurityException("The saved provider credential is invalid."); }
        var inputPtr = Marshal.AllocHGlobal(inputBytes.Length);
        try
        {
            Marshal.Copy(inputBytes, 0, inputPtr, inputBytes.Length);
            var input = new DataBlob(inputBytes.Length, inputPtr);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var output))
                throw new SecurityException("The saved provider credential cannot be unlocked for this Windows user.");
            try
            {
                var secretBytes = new byte[output.Size];
                Marshal.Copy(output.Data, secretBytes, 0, output.Size);
                try { return System.Text.Encoding.UTF8.GetString(secretBytes); }
                finally { Array.Clear(secretBytes); }
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Array.Clear(inputBytes);
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
        public DataBlob(int size, IntPtr data) { Size = size; Data = data; }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
