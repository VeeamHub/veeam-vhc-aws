using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VeeamVhcAws.Core.Config;

public static class SecretKeyStore
{
    private static readonly AsyncLocal<string?> OverridePath = new();
    private static readonly AsyncLocal<string?> ThreadConfigDirectory = new();
    private static readonly object Gate = new();
    private static string? _processConfigDirectory;
    private static byte[]? _cachedKey;
    private static string? _cachedPath;

    internal static IDisposable UseKeyFile(string path)
    {
        var prior = OverridePath.Value;
        OverridePath.Value = path;
        return new Reset(() => OverridePath.Value = prior);
    }

    public static void BindThreadDirectory(string configPath)
    {
        var dir = DirectoryOf(configPath);
        if (!string.IsNullOrEmpty(dir))
            ThreadConfigDirectory.Value = dir;
    }

    public static void BindProcessDirectory(string configPath)
    {
        BindThreadDirectory(configPath);
        var dir = DirectoryOf(configPath);
        if (!string.IsNullOrEmpty(dir))
            Interlocked.CompareExchange(ref _processConfigDirectory, dir, null);
    }

    internal static byte[] CurrentKey()
    {
        var path = ResolvePath();
        lock (Gate)
        {
            if (_cachedKey != null && _cachedPath == path)
            {
                if (!OperatingSystem.IsWindows())
                    EnsureOwnerOnly(path);
                return _cachedKey;
            }

            _cachedKey = LoadOrCreate(path);
            _cachedPath = path;
            return _cachedKey;
        }
    }

    private static string? DirectoryOf(string configPath)
    {
        var full = Path.GetFullPath(configPath);
        return Path.GetDirectoryName(full);
    }

    private static string ResolvePath()
    {
        if (!string.IsNullOrEmpty(OverridePath.Value))
            return Path.GetFullPath(OverridePath.Value);
        var env = Environment.GetEnvironmentVariable("VEEAM_VHC_AWS_KEY_FILE");
        if (!string.IsNullOrEmpty(env))
            return Path.GetFullPath(env);
        if (!string.IsNullOrEmpty(ThreadConfigDirectory.Value))
            return Path.Combine(ThreadConfigDirectory.Value, ".veeam-vhc-aws.key");
        if (!string.IsNullOrEmpty(_processConfigDirectory))
            return Path.Combine(_processConfigDirectory, ".veeam-vhc-aws.key");
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "VHC", "secret.key");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".vhc", "secret.key");
    }

    private static byte[] LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return Read(path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var key = RandomNumberGenerator.GetBytes(32);
        var file = OperatingSystem.IsWindows()
            ? WindowsKey.Wrap(key)
            : Concat(Encoding.ASCII.GetBytes("VHCK"), new byte[] { 1 }, key);

        try
        {
            WriteNew(path, file);
        }
        catch (IOException) when (File.Exists(path))
        {
            return Read(path);
        }

        return key;
    }

    private static void WriteNew(string path, byte[] file)
    {
        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = ownerOnly;
        using (var stream = new FileStream(path, options))
            stream.Write(file);
        if (OperatingSystem.IsWindows())
            WindowsKey.RestrictAcl(path);
        else
        {
            File.SetUnixFileMode(path, ownerOnly);
            EnsureOwnerOnly(path);
        }
    }

    private static byte[] Read(string path)
    {
        if (!OperatingSystem.IsWindows())
            EnsureOwnerOnly(path);

        var file = File.ReadAllBytes(path);
        if (file.Length < 5 || Encoding.ASCII.GetString(file, 0, 4) != "VHCK")
            throw new InvalidOperationException($"Key file {path} is not a VHC key.");

        var version = file[4];
        if (version == 1)
        {
            if (file.Length != 37)
                throw new InvalidOperationException($"Key file {path} has an unexpected length.");
            return file[5..];
        }

        if (version == 2)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException($"Key file {path} is DPAPI-wrapped and can only be read on Windows.");
            return WindowsKey.Unwrap(file[5..]);
        }

        throw new InvalidOperationException($"Key file {path} has unsupported version {version}.");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void EnsureOwnerOnly(string path)
    {
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode loose =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & loose) != 0)
            throw new InvalidOperationException(
                $"Key file {path} is readable by group or other. Restrict it to the owner (chmod 600) and retry.");
    }

    private static byte[] Concat(byte[] left, byte[] mid, byte[] right)
    {
        var result = new byte[left.Length + mid.Length + right.Length];
        Buffer.BlockCopy(left, 0, result, 0, left.Length);
        Buffer.BlockCopy(mid, 0, result, left.Length, mid.Length);
        Buffer.BlockCopy(right, 0, result, left.Length + mid.Length, right.Length);
        return result;
    }

    private sealed class Reset : IDisposable
    {
        private readonly Action _onDispose;
        public Reset(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class WindowsKey
    {
        private const uint CryptProtectLocalMachine = 0x4;

        public static byte[] Wrap(byte[] key)
        {
            var blob = Protect(key);
            var file = new byte[5 + blob.Length];
            Encoding.ASCII.GetBytes("VHCK").CopyTo(file, 0);
            file[4] = 2;
            blob.CopyTo(file, 5);
            return file;
        }

        public static byte[] Unwrap(byte[] blob) => Unprotect(blob);

        public static void RestrictAcl(string path)
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var args = string.Join(' ',
                Quote(path), "/inheritance:r", "/grant:r",
                Quote($"{user}:(R)"),
                Quote(@"NT AUTHORITY\SYSTEM:(R)"),
                Quote(@"BUILTIN\Administrators:(R)"));
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null || !proc.WaitForExit(5000) || proc.ExitCode != 0)
                throw new InvalidOperationException($"Could not restrict the ACL on key file {path}.");
        }

        private static byte[] Protect(byte[] plain)
        {
            var input = ToBlob(plain);
            var output = new DataBlob();
            try
            {
                if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectLocalMachine, ref output))
                    throw new InvalidOperationException("CryptProtectData failed: " + Marshal.GetLastWin32Error());
                return FromBlob(output);
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }

        private static byte[] Unprotect(byte[] blob)
        {
            var input = ToBlob(blob);
            var output = new DataBlob();
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    throw new InvalidOperationException("CryptUnprotectData failed: " + Marshal.GetLastWin32Error());
                return FromBlob(output);
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }

        private static DataBlob ToBlob(byte[] data)
        {
            var ptr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            return new DataBlob { cbData = data.Length, pbData = ptr };
        }

        private static byte[] FromBlob(DataBlob blob)
        {
            var data = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, data, 0, blob.cbData);
            return data;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, IntPtr szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
