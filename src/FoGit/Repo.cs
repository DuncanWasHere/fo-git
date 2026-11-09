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
    public string EsmPath => Path.Combine(Root, Config.Master);
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

    // Parents of the HEAD commit. Two for a merge commit
    public string[] HeadParents() =>
        Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];

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

    public void GenerateText(string esmPath, string textDir) {
        var tmp = TempDir("ser");
        try {
            RunSpriggit("serialize", "-i", esmPath, "-o", tmp, "-g", "FalloutNV",
                "-p", Config.SpriggitPackage, "-v", Config.SpriggitVersion, "-u", "true");
            SyncFolder(tmp, textDir, SpriggitText.Normalize);
        } finally {
            TryDelete(tmp);
        }
    }

    public void BuildEsm(string textDir, string esmPath) {
        var tmpDir = TempDir("de");
        var tmp = Path.Combine(tmpDir, Path.GetFileName(esmPath));
        try {
            RunSpriggit("deserialize", "-i", textDir, "-o", tmp, "-b", "0");
            CopyWithRetry(tmp, esmPath);
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

    public void WithTempDir(string prefix, Action<string> action) {
        var dir = TempDir(prefix);
        try {
            action(dir);
        } finally {
            TryDelete(dir);
        }
    }

    public static void SyncFolder(string src, string dst, Func<byte[], byte[]>? transform = null) {
        Directory.CreateDirectory(dst);
        var srcFiles = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(src, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).ToList()) {
            if (!srcFiles.Contains(Path.GetRelativePath(dst, f))) {
                File.Delete(f);
            }
        }
        Parallel.ForEach(srcFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, rel => {
            var content = StripCr(File.ReadAllBytes(Path.Combine(src, rel)));
            WriteIfChanged(Path.Combine(dst, rel), transform == null ? content : transform(content));
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

    // Git tree hash of the spriggit folder
    public string TextTree() {
        if (!Directory.Exists(SpriggitDir)) {
            return "missing";
        }

        var index = Path.Combine(StateDir, "text-index");
        var realIndex = Path.GetFullPath(Path.Combine(Root, Git("rev-parse", "--git-path", "index")));
        if (File.Exists(realIndex)) {
            File.Copy(realIndex, index, overwrite: true);
        } else {
            TryDelete(index);
        }

        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        var folder = Config.SpriggitFolder.TrimEnd('/');
        try {
            // Drop everything outside the folder
            Run(env, "rm", "--sparse", "-r", "-q", "-f", "--cached", "--ignore-unmatch", "--", ".", $":(exclude){folder}");
            Run(env, "add", "--sparse", "-A", "--", folder);
            return Run(env, "write-tree", $"--prefix={folder}/");
        } finally {
            TryDelete(index);
        }
    }

    private string Run(Dictionary<string, string> env, params string[] args) {
        var (code, stdout, stderr) = Proc.Run("git", args, Root, env);
        if (code != 0) {
            throw new UserError($"git {string.Join(' ', args)} failed.\n{stderr}{stdout}");
        }

        return stdout.Trim();
    }

    public sealed record BuildInfo(string TextHash, string EsmHash, long EsmSize, DateTime EsmWriteTime, DateTime BuiltAt);

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
        var fi = new FileInfo(EsmPath);
        var info = new BuildInfo(textHash, HashFile(EsmPath), fi.Length, fi.LastWriteTimeUtc, DateTime.Now);
        File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(info, Json.Options));
        CacheEsm(textHash);
    }

    // ---------------------------------------------------------------- ESM cache

    // Copies of the ESM for each text git tree
    public string EsmCacheDir => Path.Combine(StateDir, "esm-cache");

    private string CachedEsmPath(string tree) => Path.Combine(EsmCacheDir, Config.SpriggitVersion, $"{tree}-{Config.Master}");

    public int CachedEsmCount => Directory.Exists(EsmCacheDir)
        ? Directory.EnumerateFiles(EsmCacheDir, "*", SearchOption.AllDirectories).Count()
        : 0;

    public bool RestoreCachedEsm(string tree) {
        var cached = CachedEsmPath(tree);
        if (Local.EsmCacheSize <= 0 || !File.Exists(cached)) {
            return false;
        }

        CopyWithRetry(cached, EsmPath);
        // Creation time is used as the last use time for pruning
        File.SetCreationTimeUtc(cached, DateTime.UtcNow);
        return true;
    }

    private void CacheEsm(string tree) {
        if (Local.EsmCacheSize <= 0 || tree == "missing" || !File.Exists(EsmPath)) {
            return;
        }

        var cached = CachedEsmPath(tree);
        if (!File.Exists(cached)) {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            var tmp = cached + ".tmp";
            File.Copy(EsmPath, tmp, overwrite: true);
            File.Move(tmp, cached, overwrite: true);
        }
        File.SetCreationTimeUtc(cached, DateTime.UtcNow);

        var old = new DirectoryInfo(EsmCacheDir).EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderByDescending(f => f.CreationTimeUtc).Skip(Local.EsmCacheSize).ToList();
        foreach (var f in old) {
            TryDelete(f.FullName);
        }
        DeleteEmptyFolders(EsmCacheDir);
    }

    public bool RebaseInProgress() =>
        new[] { "rebase-merge", "rebase-apply" }.Any(d => Directory.Exists(Path.GetFullPath(Path.Combine(Root, Git("rev-parse", "--git-path", d)))));

    public enum BuildState {
        Missing,
        UpToDate,
        TextChanged,
        EsmEdited,
    }

    private bool EsmMatches(BuildInfo info) {
        var fi = new FileInfo(EsmPath);
        return (info.EsmSize == fi.Length && info.EsmWriteTime == fi.LastWriteTimeUtc) || HashFile(EsmPath) == info.EsmHash;
    }

    // Edited since fo-git built it
    public bool EsmEdited() {
        var info = ReadBuildInfo();
        return info != null && File.Exists(EsmPath) && !EsmMatches(info);
    }

    // Edited since fo-git built it, or not built by fo-git at all
    public bool EsmHasEdits() {
        var info = ReadBuildInfo();
        return File.Exists(EsmPath) && (info == null || !EsmMatches(info));
    }

    public BuildState GetBuildState() {
        var info = ReadBuildInfo();
        if (!File.Exists(EsmPath) || info == null) {
            return BuildState.Missing;
        }

        if (!EsmMatches(info)) {
            return BuildState.EsmEdited;
        }

        var tree = TextTree();
        if (tree == info.TextHash) {
            return BuildState.UpToDate;
        }

        // Compatibility check for updating from older fo-git
        if (info.TextHash.Length == 16 && HashFolder(SpriggitDir) == info.TextHash) {
            WriteBuildInfo(tree);
            return BuildState.UpToDate;
        }

        return BuildState.TextChanged;
    }
}
