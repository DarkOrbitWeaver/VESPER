using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Vesper.Engine;

/// <summary>
/// Current-user Windows DPAPI at rest. No new packages, keys, service, prompts or cookie persistence.
/// Legacy plaintext remains readable. Protected data from another Windows account fails explicitly, never
/// masquerading as a corrupt SQLite database. Non-Windows engine/test tooling has no DPAPI and stores plaintext.
/// </summary>
internal static class ProtectedMediaState
{
    private const string Prefix = "vesper-dpapi:1:";
    private const int MaximumBytes = 48 * 1024 * 1024;
    private sealed record CipherText(string Value);
    private static readonly ConditionalWeakTable<string, CipherText> Cache = new();

    internal static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows()) return value;
        // Plans are immutable and reused by checkpoint clones. Do not re-encrypt a large plan on every commit.
        return Cache.GetValue(value, key => new CipherText(Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(key), protect: true)))).Value;
    }

    internal static string? Unprotect(string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        if (!OperatingSystem.IsWindows())
            throw new CryptographicException("This media state is protected by Windows DPAPI. Open it using the original Windows account; the database was left untouched.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value[Prefix.Length..]); }
        catch (FormatException ex) { throw new CryptographicException("The protected media state is malformed; the database was left untouched.", ex); }
        byte[] plaintext = Transform(bytes, protect: false);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length > MaximumBytes) throw new CryptographicException("Media state exceeds the protected-storage limit.");
        byte[] entropy = Encoding.UTF8.GetBytes("VESPER/media-state/v1");
        var inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var source = new Blob { Length = input.Length, Data = inputPin.AddrOfPinnedObject() };
        var salt = new Blob { Length = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
        Blob output = default;
        try
        {
            bool ok = protect
                ? CryptProtectData(ref source, "VESPER source state", ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows could not protect/unprotect media state for the current account. No database replacement was attempted.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
            if (output.Length < 0 || output.Length > MaximumBytes || (output.Length > 0 && output.Data == IntPtr.Zero))
                throw new CryptographicException("Invalid protected media-state length.");
            var result = new byte[output.Length];
            if (result.Length > 0) Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            inputPin.Free(); entropyPin.Free();
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Length; internal IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob data, string description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob result);

    [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob result);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
