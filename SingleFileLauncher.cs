using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CampusReconnectPackage {
    static class Launcher {
        const string PackageHash = "__PACKAGE_HASH__";
        const string Version = "__APP_VERSION__";
        static readonly Assembly Self = Assembly.GetExecutingAssembly();
        static string Digest(Stream stream) {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string SafePath(string root, string relative) {
            if (Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid package path");
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escaped its directory");
            return target;
        }
        static Dictionary<string,string> ReadManifest() {
            Dictionary<string,string> files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            using (StreamReader reader = new StreamReader(Self.GetManifestResourceStream("files.sha256"), Encoding.UTF8)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    if (line.Length == 0) continue;
                    int separator = line.IndexOf('|');
                    if (separator != 64) throw new InvalidDataException("Invalid file manifest");
                    files.Add(line.Substring(65), line.Substring(0,64));
                }
            }
            return files;
        }
        static bool Matches(string file, string expected) {
            if (!File.Exists(file)) return false;
            using (FileStream stream = File.OpenRead(file)) return Digest(stream) == expected;
        }
        static string Prepare(string cache, out bool repaired) {
            string root = Path.Combine(cache, Version + "-" + PackageHash.Substring(0,16));
            Dictionary<string,string> files = ReadManifest();
            repaired = false;
            bool acquired = false;
            using (Mutex mutex = new Mutex(false, "Local\\CampusReconnectPackage-" + PackageHash.Substring(0,16))) {
                try {
                    try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Another copy is preparing the application. Please try again.");
                    bool valid = true;
                    foreach (KeyValuePair<string,string> file in files) {
                        if (!Matches(SafePath(root,file.Key),file.Value)) { valid = false; break; }
                    }
                    if (valid) return root;
                    using (Stream resource = Self.GetManifestResourceStream("payload.zip")) {
                        if (resource == null || Digest(resource) != PackageHash) throw new InvalidDataException("Application package integrity check failed");
                    }
                    Directory.CreateDirectory(root);
                    using (Stream resource = Self.GetManifestResourceStream("payload.zip"))
                    using (ZipArchive archive = new ZipArchive(resource,ZipArchiveMode.Read)) {
                        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ZipArchiveEntry entry in archive.Entries) {
                            string relative = entry.FullName.Replace('\\','/');
                            string expected;
                            if (!files.TryGetValue(relative,out expected) || !seen.Add(relative)) throw new InvalidDataException("Unexpected package entry");
                            string dest = SafePath(root,relative);
                            if (Matches(dest,expected)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            string temp = dest + ".new-" + Guid.NewGuid().ToString("N");
                            try {
                                using (Stream input = entry.Open())
                                using (FileStream output = new FileStream(temp,FileMode.CreateNew,FileAccess.Write)) input.CopyTo(output);
                                if (!Matches(temp,expected)) throw new InvalidDataException("Extracted file integrity check failed");
                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(temp,dest);
                            } finally { if (File.Exists(temp)) File.Delete(temp); }
                        }
                        if (seen.Count != files.Count) throw new InvalidDataException("Incomplete application package");
                    }
                    repaired = true;
                    return root;
                } finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        // Windows command-line quoting, including a trailing backslash in a quoted path.
        static string Quote(string value) {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\',slashes*2+1); result.Append('"'); }
                else { result.Append('\\',slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\',slashes*2); result.Append('"'); return result.ToString();
        }
        [STAThread]
        static int Main(string[] args) {
            bool diagnostic = false;
            try {
                List<string> forwarded = new List<string>(args);
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CampusReconnect","app");
                if (forwarded.Count >= 2 && forwarded[0] == "--cache-dir") {
                    cache = Path.GetFullPath(forwarded[1]); forwarded.RemoveRange(0,2);
                }
                diagnostic = forwarded.Count > 0 && forwarded[0].StartsWith("--",StringComparison.Ordinal);
                bool repaired;
                string root = Prepare(cache,out repaired);
                if (forwarded.Count == 2 && forwarded[0] == "--verify-package") {
                    File.WriteAllText(forwarded[1],"PASS: embedded package and extracted files verified\r\npath="+root+"\r\nrepaired="+repaired+"\r\n",Encoding.UTF8);
                    return 0;
                }
                StringBuilder arguments = new StringBuilder();
                foreach (string argument in forwarded) { if (arguments.Length > 0) arguments.Append(' '); arguments.Append(Quote(argument)); }
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(root,"CampusReconnect.exe"),arguments.ToString());
                info.WorkingDirectory = root; info.UseShellExecute = false;
                using (Process child = Process.Start(info)) {
                    if (diagnostic) { child.WaitForExit(); return child.ExitCode; }
                }
                return 0;
            } catch (Exception ex) {
                if (diagnostic) {
                    if (args.Length > 0) {
                        try { File.WriteAllText(args[args.Length-1],"FAIL: "+ex.Message,Encoding.UTF8); } catch { }
                    }
                } else MessageBox.Show("无法启动校园网助手："+ex.Message,"校园网助手",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
