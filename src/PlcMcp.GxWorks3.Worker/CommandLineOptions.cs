using System;
using System.IO;

namespace PlcMcp.GxWorks3.Worker
{
    public sealed class CommandLineOptions
    {
        public string GxExe { get; }
        public string ExpectedVersion { get; }

        private CommandLineOptions(string gxExe, string expectedVersion)
        {
            GxExe = gxExe;
            ExpectedVersion = expectedVersion;
        }

        public static bool TryParse(string[] args, out CommandLineOptions? options, out string? errorMessage)
        {
            options = null;
            errorMessage = null;

            if (args == null || args.Length == 0)
            {
                errorMessage = "Missing required arguments. Usage: --gx-exe <absolute-path> --expected-version <exact-version>";
                return false;
            }

            string? gxExe = null;
            string? expectedVersion = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (string.Equals(arg, "--gx-exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (gxExe != null)
                    {
                        errorMessage = "Duplicate argument: --gx-exe";
                        return false;
                    }
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = "Missing value for --gx-exe";
                        return false;
                    }
                    gxExe = args[++i];
                }
                else if (string.Equals(arg, "--expected-version", StringComparison.OrdinalIgnoreCase))
                {
                    if (expectedVersion != null)
                    {
                        errorMessage = "Duplicate argument: --expected-version";
                        return false;
                    }
                    if (i + 1 >= args.Length)
                    {
                        errorMessage = "Missing value for --expected-version";
                        return false;
                    }
                    expectedVersion = args[++i];
                }
                else
                {
                    errorMessage = string.Format("Unknown argument: '{0}'. Only --gx-exe and --expected-version are accepted.", arg);
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(gxExe))
            {
                errorMessage = "Mandatory argument --gx-exe was not provided.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                errorMessage = "Mandatory argument --expected-version was not provided.";
                return false;
            }

            // Path validations
            if (string.IsNullOrWhiteSpace(gxExe) || ContainsInvalidChars(gxExe!))
            {
                errorMessage = "Path contains invalid characters or injection attempts.";
                return false;
            }

            if (!Path.IsPathRooted(gxExe))
            {
                errorMessage = "The --gx-exe path must be an absolute path.";
                return false;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(gxExe);
            }
            catch (Exception ex)
            {
                errorMessage = "Invalid path format: " + ex.Message;
                return false;
            }

            if (!string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "The --gx-exe target must have an .exe extension.";
                return false;
            }

            if (!File.Exists(normalized))
            {
                errorMessage = "Target executable does not exist: " + normalized;
                return false;
            }

            // Check for symlinks/reparse points
            try
            {
                var fileInfo = new FileInfo(normalized);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errorMessage = "Reparse points or symbolic links are strictly prohibited: " + normalized;
                    return false;
                }

                var dir = fileInfo.Directory;
                while (dir != null)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errorMessage = "Directory path contains reparse point or symbolic link: " + dir.FullName;
                        return false;
                    }
                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                errorMessage = "Failed to inspect file attributes: " + ex.Message;
                return false;
            }

            options = new CommandLineOptions(normalized, expectedVersion!.Trim());
            return true;
        }

        private static bool ContainsInvalidChars(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return true;
            if (path.IndexOf('\0') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0) return true;
            if (path.IndexOf('\"') >= 0 || path.IndexOf('\'') >= 0 || path.IndexOf(';') >= 0 || path.IndexOf('&') >= 0 || path.IndexOf('|') >= 0) return true;
            return false;
        }
    }
}
