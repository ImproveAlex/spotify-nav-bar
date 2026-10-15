using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SpotifyBar.Core;

namespace SpotifyBar;

public sealed class Preferences
{
    public string ClientId { get; set; } = "";
    public string Monitor { get; set; } = "";
    public bool ReserveSpace { get; set; } = true;
}

public static class Storage
{
    public static string Root => PortablePaths.GetDataRoot(Environment.GetCommandLineArgs().Contains("--smoke-test"), Environment.CurrentDirectory);

    public static void Initialize(bool migrateLegacy, bool createPreferences)
    {
        var legacy = migrateLegacy
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyBar") : null;
        InitializeDirectory(Root, legacy, createPreferences);
    }

    public static void InitializeDirectory(string root, string? legacyRoot = null, bool createPreferences = true)
    {
        Directory.CreateDirectory(root);
        if (legacyRoot is not null && Directory.Exists(legacyRoot)
            && !Path.GetFullPath(legacyRoot).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "settings.json", "session.dat", "errors.log" })
            {
                var source = Path.Combine(legacyRoot, name);
                if (!File.Exists(source)) continue;
                var destination = Path.Combine(root, name);
                if (File.Exists(destination))
                {
                    var previous = Path.Combine(root, "PreviousData");
                    Directory.CreateDirectory(previous);
                    destination = Path.Combine(previous, Path.GetFileNameWithoutExtension(name) + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                }
                File.Move(source, destination);
            }
            // Leave unrelated legacy files and directories alone.
        }
        if (createPreferences && !File.Exists(Path.Combine(root, "settings.json")))
            WriteAtomic(Path.Combine(root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(new Preferences()));
        WriteDocumentation(Path.Combine(root, "README.txt"), UserDocumentation.Readme);
        WriteDocumentation(Path.Combine(root, "SPOTIFY-SETUP.txt"), UserDocumentation.Setup);
    }

    private static void WriteDocumentation(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(content));
    }
    public static Preferences LoadPreferences()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }
    public static void SavePreferences(Preferences preferences) => WriteAtomic(Path.Combine(Root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(preferences));
    internal static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Windows DPAPI encrypts tokens for the signed-in Windows user; the app never stores plaintext tokens.
public sealed class WindowsTokenStore(string? path = null) : ITokenStore
{
    private readonly string _path = path ?? Path.Combine(Storage.Root, "session.dat");
    public TokenSet? Load()
    {
        try { return JsonSerializer.Deserialize<TokenSet>(Transform(File.ReadAllBytes(_path), false)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
    public void Save(TokenSet tokens) => Storage.WriteAtomic(_path, Transform(JsonSerializer.SerializeToUtf8Bytes(tokens), true));
    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = encrypt ? CryptProtectData(ref input, "Spotify Bar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows could not protect the Spotify session.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
