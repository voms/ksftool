using System;
using System.Collections.Generic;
using System.IO;

namespace KsfCompanion
{
    /// <summary>What a process has open, from /proc (only processes of the same user can be looked at).</summary>
    static class OpenFiles
    {
        /// <summary>
        /// The names of the files with this extension that the process has open for writing; empty when there's no
        /// such process, and null when its open files can't be read.
        /// </summary>
        public static HashSet<string> WrittenBy(int pid, string extension = ".dem")
        {
            if (!OperatingSystem.IsLinux()) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pid <= 0) return names;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                {
                    string target;
                    try { target = new FileInfo(fd).LinkTarget; }
                    catch (IOException) { continue; }
                    // Sockets and pipes aren't files; a file deleted while open reads "... (deleted)".
                    if (string.IsNullOrEmpty(target) || target[0] != '/' || !target.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsWriting(pid, Path.GetFileName(fd))) names.Add(Path.GetFileName(target));
                }
                return names;
            }
            catch (DirectoryNotFoundException) { return names; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Whether the file descriptor was opened for writing ("flags: 0100001" - the last octal digit is the access mode).</summary>
        static bool IsWriting(int pid, string fd)
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/{pid}/fdinfo/{fd}"))
                {
                    if (!line.StartsWith("flags:", StringComparison.Ordinal)) continue;
                    var flags = Convert.ToInt32(line.Substring(6).Trim(), 8);
                    // O_RDONLY 0, O_WRONLY 1, O_RDWR 2
                    return (flags & 3) != 0;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            return false;
        }
    }
}
