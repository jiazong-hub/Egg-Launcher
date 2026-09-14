using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core.Security;

namespace Launcher.ChatGPT.Configuration;

public static class ProtectedConfigBackup
{
    private static readonly byte[] Magic = "CGL-DPAPI-1\n"u8.ToArray();
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("ChatGPT Local Launcher config backup v1"));

    public static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var protectedBytes = Protect(plaintext.Span);
        var payload = new byte[Magic.Length + sizeof(int) + protectedBytes.Length];
        Magic.CopyTo(payload, 0);
        BitConverter.TryWriteBytes(payload.AsSpan(Magic.Length, sizeof(int)), protectedBytes.Length);
        protectedBytes.CopyTo(payload, Magic.Length + sizeof(int));

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("加密备份必须位于一个目录中。");
        PrivateFilePermissions.HardenDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken).ConfigureAwait(false);
            PrivateFilePermissions.HardenFile(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<byte[]> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        if (payload.Length <= Magic.Length + sizeof(int)
            || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("配置备份不是受支持的 DPAPI 密文格式。");
        }

        var protectedLength = BitConverter.ToInt32(payload, Magic.Length);
        if (protectedLength <= 0 || payload.Length != Magic.Length + sizeof(int) + protectedLength)
        {
            throw new InvalidDataException("配置备份密文长度无效。");
        }

        return Unprotect(payload.AsSpan(Magic.Length + sizeof(int), protectedLength));
    }

    private static byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        Transform(plaintext, protect: true);

    private static byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) =>
        Transform(protectedBytes, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("配置备份加密需要 Windows DPAPI。");
        }

        var inputBlob = AllocateBlob(input);
        var entropyBlob = AllocateBlob(Entropy);
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    "ChatGPT Local Launcher configuration backup",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    protect ? "DPAPI 配置备份加密失败。" : "DPAPI 配置备份解密失败。");
            }

            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    _ = LocalFree(outputBlob.Data);
                }
            }
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static DataBlob AllocateBlob(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return default;
        }

        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes.ToArray(), 0, pointer, bytes.Length);
        return new DataBlob { Length = bytes.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
