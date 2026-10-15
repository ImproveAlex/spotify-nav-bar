using System;
using System.IO;
using System.Text;

namespace SpotifyBar
{
    // Shared with the launcher; deliberately compatible with the Windows .NET Framework compiler.
    internal static class PortablePaths
    {
        public static string GetDataRoot(bool smokeTest, string workingDirectory)
        {
            if (smokeTest)
                return Path.GetFullPath(Path.Combine(workingDirectory, "artifacts", "portable-smoke", "Documents", "SpotifyBar"));
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents) || !Path.IsPathRooted(documents))
                throw new IOException("Windows did not provide a usable Documents folder.");
            return Path.Combine(documents, "SpotifyBar");
        }

        public static string QuoteArgument(string argument)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                slashes = 0;
                result.Append(character);
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
