using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoGit;

public sealed class Repo {
    public string Root { get; }
    public RepoConfig Config { get; }
    public LocalConfig Local { get; }

    public string ConfigPath => Path.Combine(Root, "fo-git.json");
    public string LocalConfigPath => Path.Combine(Root, "fo-git.local.json");

    public string StateDir => Path.Combine(Root, ".fo-git");
    public string MasterPath => Path.Combine(Root, Config.Master);
    public string SpriggitDir => Path.Combine(Root, Norm(Config.SpriggitFolder));
    public string BuildInfoPath => Path.Combine(StateDir, "build-stamp.json");
    public string KnownIssuesPath => Path.Combine(Root, Norm(Config.KnownIssuesFile));
    public HashSet<string> KnownIssues => TextOps.LoadKnownIssues(KnownIssuesPath);

    public string MergeMapDir => Path.Combine(StateDir, "merge-maps");

    public string PluginFolder => Path.GetFullPath(Path.Combine(Root, Norm(Local.PluginFolder ?? Config.PluginFolder)));

    private Repo(string root) {
        Root = root;
        Config = RepoConfig.Load(ConfigPath);
        Local = LocalConfig.Load(LocalConfigPath);
        Directory.CreateDirectory(StateDir);
    }

    public static Repo Find() {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }) {
            var dir = new DirectoryInfo(start);
            while (dir != null) {
                if (File.Exists(Path.Combine(dir.FullName, "fo-git.json"))) {
                    return new Repo(dir.FullName);
                }

                dir = dir.Parent;
            }
        }
        throw new UserError("Couldn't find fo-git.json");
    }

    public static string Norm(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    // ---------------------------------------------------------------- Git

    public (int code, string stdout, string stderr) GitRaw(params string[] args) => Proc.Run("git", args, Root);

    public string Git(params string[] args) {
        var (code, stdout, stderr) = GitRaw(args);
        if (code != 0) {
            throw new UserError($"git {string.Join(' ', args)} failed.\n{stderr}{stdout}");
        }

        return stdout.TrimEnd();
    }

    public bool IsDirty(string relPath) => Git("status", "--porcelain", "--", relPath).Length > 0;

    public string? HeadTree() {
        var (code, stdout, _) = GitRaw("rev-parse", "HEAD:" + Config.SpriggitFolder);
        return code == 0 ? stdout.Trim() : null;
    }

    // ---------------------------------------------------------------- Spriggit

    public string SpriggitCli {
        get {
            var dir = Path.Combine(StateDir, "spriggit-cli", Config.SpriggitVersion);
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "Spriggit.CLI.exe" : "Spriggit.CLI");
            if (File.Exists(exe)) {
                return exe;
            }

            Ui.Info($"Downloading Spriggit {Config.SpriggitVersion}");
            var asset = OperatingSystem.IsWindows() ? "SpriggitCLI.zip" : "SpriggitLinuxCLI.zip";
            var url = $"https://github.com/Mutagen-Modding/Spriggit/releases/download/{Config.SpriggitVersion}/{asset}";
            using var http = new HttpClient();
            var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            Directory.CreateDirectory(dir);
            using (var ms = new MemoryStream(bytes)) {
                ZipFile.ExtractToDirectory(ms, dir, overwriteFiles: true);
            }

            if (!File.Exists(exe)) {
                throw new UserError($"Spriggit download is missing {exe}");
            }

            return exe;
        }
    }

    private void RunSpriggit(params string[] args) {
        var sw = Stopwatch.StartNew();
        var code = Proc.RunStreaming(SpriggitCli, args, Root, line => {
            if (line.Contains("ERR") || line.Contains("WRN") || line.Contains("Exception")) {
                Ui.Info(line.Trim());
            }
        });
        if (code != 0) {
            throw new UserError($"Spriggit failed with exit code {code}. Run this command for the full output.\n  \"{SpriggitCli}\" {string.Join(' ', args.Select(Proc.Quote))}");
        }

        Ui.Info($"    (took {sw.Elapsed:m\\:ss})");
    }

    public void Serialize(string pluginPath, string targetDir) {
        var tmp = TempDir("ser");
        try {
            RunSpriggit("serialize", "-i", pluginPath, "-o", tmp, "-g", "FalloutNV",
                "-p", Config.SpriggitPackage, "-v", Config.SpriggitVersion, "-u", "true");
            SyncFolder(tmp, targetDir);
        } finally {
            TryDelete(tmp);
        }
    }

    public void Deserialize(string sourceDir, string pluginPath) {
        var tmpDir = TempDir("de");
        var tmp = Path.Combine(tmpDir, Path.GetFileName(pluginPath));
        try {
            RunSpriggit("deserialize", "-i", sourceDir, "-o", tmp, "-b", "0");
            CopyWithRetry(tmp, pluginPath);
        } finally {
            TryDelete(tmpDir);
        }
    }

    public static void CopyWithRetry(string src, string dst) {
        while (true) {
            try {
                File.Copy(src, dst, overwrite: true);
                return;
            } catch (IOException e) when (!Console.IsInputRedirected) {
                Ui.Warn($"Couldn't write {Path.GetFileName(dst)} ({e.Message.Trim()}). Close any programs using the file and press any key to retry.");
                Console.ReadKey(true);
            }
        }
    }

    public string TempDir(string prefix) {
        var d = Path.Combine(StateDir, "tmp", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void SyncFolder(string src, string dst) {
        Directory.CreateDirectory(dst);
        var srcFiles = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(src, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).ToList()) {
            if (!srcFiles.Contains(Path.GetRelativePath(dst, f))) {
                File.Delete(f);
            }
        }
        Parallel.ForEach(srcFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, rel => {
            WriteIfChanged(Path.Combine(dst, rel), StripCr(File.ReadAllBytes(Path.Combine(src, rel))));
        });
        DeleteEmptyFolders(dst);
    }

    public static void WriteIfChanged(string path, byte[] content) {
        if (File.Exists(path) && StripCr(File.ReadAllBytes(path)).AsSpan().SequenceEqual(content)) {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public static void DeleteEmptyFolders(string root) {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length)) {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) {
                Directory.Delete(dir);
            }
        }
    }

    public static byte[] StripCr(byte[] bytes) {
        if (Array.IndexOf(bytes, (byte)'\r') < 0) {
            return bytes;
        }

        var buf = new byte[bytes.Length];
        int n = 0;
        foreach (var b in bytes) {
            if (b != (byte)'\r') {
                buf[n++] = b;
            }
        }

        Array.Resize(ref buf, n);
        return buf;
    }

    public static void TryDelete(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            } else if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
        }
    }

    // ---------------------------------------------------------------- Build info

    public static string HashFolder(string dir) {
        if (!Directory.Exists(dir)) {
            return "missing";
        }

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (rel: Path.GetRelativePath(dir, f).Replace('\\', '/'), full: f))
            .OrderBy(x => x.rel, StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, full) in files) {
            sha.AppendData(Encoding.UTF8.GetBytes(rel + "\0"));
            sha.AppendData(StripCr(File.ReadAllBytes(full)));
            sha.AppendData([0]);
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }

    public static string HashFile(string path) {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs))[..16];
    }

    public sealed record BuildInfo(string TextHash, string EsmHash, long EsmSize, DateTime EsmWriteTime, string? Tree, DateTime BuiltAt);

    public BuildInfo? ReadBuildInfo() {
        if (!File.Exists(BuildInfoPath)) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(BuildInfoPath), Json.Options);
        } catch {
            return null;
        }
    }

    public void WriteBuildInfo(string textHash) {
        string? tree = null;
        try {
            if (!IsDirty(Config.SpriggitFolder)) {
                tree = HeadTree();
            }
        } catch (UserError) {
        }
        var fi = new FileInfo(MasterPath);
        var info = new BuildInfo(textHash, HashFile(MasterPath), fi.Length, fi.LastWriteTimeUtc, tree, DateTime.Now);
        File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(info, Json.Options));
    }

    public enum BuildState {
        Missing,
        UpToDate,
        TextChanged,
        EsmEdited,
    }

    public BuildState GetBuildState() {
        var info = ReadBuildInfo();
        if (!File.Exists(MasterPath) || info == null) {
            return BuildState.Missing;
        }

        var fi = new FileInfo(MasterPath);
        bool esmSame = (info.EsmSize == fi.Length && info.EsmWriteTime == fi.LastWriteTimeUtc) || HashFile(MasterPath) == info.EsmHash;
        if (!esmSame) {
            return BuildState.EsmEdited;
        }

        if (info.Tree != null && info.Tree == HeadTree() && !IsDirty(Config.SpriggitFolder)) {
            return BuildState.UpToDate;
        }

        var hash = HashFolder(SpriggitDir);
        if (hash != info.TextHash) {
            return BuildState.TextChanged;
        }
        // Save the tree hash to make the next check faster
        if (info.Tree == null && !IsDirty(Config.SpriggitFolder)) {
            WriteBuildInfo(hash);
        }

        return BuildState.UpToDate;
    }
}

public static class Proc {
    public static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    public static (int, string, string) Run(string exe, IEnumerable<string> args, string cwd, IDictionary<string, string>? env = null) {
        var psi = new ProcessStartInfo(exe) {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["FOGIT_NOHOOK"] = "1";
        if (env != null) {
            foreach (var (k, v) in env) {
                psi.Environment[k] = v;
            }
        }

        foreach (var a in args) {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public static int RunStreaming(string exe, IEnumerable<string> args, string cwd, Action<string> onLine) {
        var psi = new ProcessStartInfo(exe) {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => {
            if (e.Data != null) {
                onLine(e.Data);
            }
        };
        p.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                onLine("ERR " + e.Data);
            }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }
}

public sealed class GitBlobReader : IDisposable {
    private readonly Process _git;
    private readonly Stream _stdout;

    public GitBlobReader(string repoRoot) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch");
        psi.Environment["FOGIT_NOHOOK"] = "1";
        _git = Process.Start(psi)!;
        _stdout = _git.StandardOutput.BaseStream;
    }

    public string? Read(string commit, string path) {
        _git.StandardInput.Write($"{commit}:{path}\n");
        _git.StandardInput.Flush();
        var header = ReadLine();
        if (header.EndsWith(" missing") || header.EndsWith(" ambiguous")) {
            return null;
        }

        var parts = header.Split(' ');
        if (parts.Length < 3 || parts[1] != "blob") {
            throw new InvalidOperationException("Unexpected output from git cat-file. " + header);
        }

        var size = int.Parse(parts[2]);
        var buf = new byte[size];
        int read = 0;
        while (read < size) {
            var n = _stdout.Read(buf, read, size - read);
            if (n <= 0) {
                throw new EndOfStreamException();
            }

            read += n;
        }
        _stdout.ReadByte(); // trailing newline
        return Encoding.UTF8.GetString(buf).Replace("\r\n", "\n");
    }

    private string ReadLine() {
        var sb = new StringBuilder();
        int b;
        while ((b = _stdout.ReadByte()) >= 0 && b != '\n') {
            sb.Append((char)b);
        }

        return sb.ToString();
    }

    public void Dispose() {
        try {
            _git.StandardInput.Close();
            _git.WaitForExit(2000);
        } catch {
        }
        _git.Dispose();
    }
}

public static class Ui {
    public static int Warnings { get; private set; }
    public static int Errors { get; private set; }

    public static bool Window { get; set; }

    public static void Info(string msg) => Console.WriteLine(msg);

    public static void Step(string msg) {
        Console.WriteLine();
        Console.WriteLine("== " + msg);
    }

    public static void Warn(string msg) {
        Warnings++;
        Write(ConsoleColor.Yellow, "[warning] " + msg);
    }

    public static void Error(string msg) {
        Errors++;
        Write(ConsoleColor.Red, "[error] " + msg);
    }

    public static void Good(string msg) => Write(ConsoleColor.Green, msg);

    private static void Write(ConsoleColor c, string msg) {
        var old = Console.ForegroundColor;
        try {
            Console.ForegroundColor = c;
            Console.WriteLine(msg);
        } finally {
            Console.ForegroundColor = old;
        }
    }

    public static void Finish() {
        if (!Window || Console.IsInputRedirected) {
            return;
        }

        Console.WriteLine();
        if (Warnings > 0 || Errors > 0) {
            Console.WriteLine($"Finished with {Errors} error(s) and {Warnings} warning(s). Press any key to close.");
        } else {
            Good("Done. Press any key to close.");
        }
        Console.ReadKey(true);
    }
}
