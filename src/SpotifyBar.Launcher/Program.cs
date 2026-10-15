using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SpotifyBar
{
    // Uses the .NET Framework already included with Windows. This starts before the bundled
    // .NET 10 host, so its extraction path can be set without modifying user/system variables.
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            try
            {
                var root = PortablePaths.GetDataRoot(arguments.Contains("--smoke-test"), Environment.CurrentDirectory);
                var runtime = Path.Combine(root, "Runtime");
                var cache = Path.Combine(runtime, "Cache");
                var playerDirectory = Path.Combine(runtime, BuildInfo.PayloadSha256);
                Directory.CreateDirectory(playerDirectory);
                Directory.CreateDirectory(cache);
                var player = Path.Combine(playerDirectory, "SpotifyBar.Player.exe");
                EnsurePlayer(player);
                var start = new ProcessStartInfo(player)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    Arguments = string.Join(" ", arguments.Select(PortablePaths.QuoteArgument))
                };
                start.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = cache;
                using (var child = Process.Start(start))
                {
                    if (child == null) throw new IOException("The player could not be started.");
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (Exception exception)
            {
                MessageBox(IntPtr.Zero, "Spotify Bar could not start. It needs write access to your Documents folder.\n\n" + exception.Message,
                    "Spotify Bar", 0x10);
                return 1;
            }
        }

        private static void EnsurePlayer(string path)
        {
            if (File.Exists(path) && HashFile(path) == BuildInfo.PayloadSha256) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpotifyBar.Player"))
                {
                    if (resource == null) throw new IOException("The executable is missing its embedded player.");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        resource.CopyTo(output);
                }
                if (HashFile(temporary) != BuildInfo.PayloadSha256) throw new IOException("The player could not be verified.");
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else
                {
                    try { File.Move(temporary, path); }
                    catch (IOException)
                    {
                        // Another launch may have extracted the identical payload concurrently.
                        if (!File.Exists(path) || HashFile(path) != BuildInfo.PayloadSha256) throw;
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    }
}
