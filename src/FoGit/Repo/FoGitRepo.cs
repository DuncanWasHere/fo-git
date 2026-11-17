using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FoGit.Text;

namespace FoGit.Repo;

public sealed class FoGitRepo {
    public string RootDir { get; }
    public RepoConfig RepoConfig { get; }
    public LocalConfig LocalConfig { get; }

    public string RepoConfigPath => Path.Combine(RootDir, "fo-git.json");
    public string LocalConfigPath => Path.Combine(RootDir, "fo-git.local.json");

    public string EsmPath => Path.Combine(RootDir, RepoConfig.Master);
    public string TextTreeDir => Path.Combine(RootDir, NormalizePath(RepoConfig.SpriggitFolder));

    public string CacheDir => Path.Combine(RootDir, ".fo-git");
    public string BuildInfoPath => Path.Combine(CacheDir, "build-stamp.json");
    public string MergeMapDir => Path.Combine(CacheDir, "merge-maps");
    public string MergePluginsFolder => Path.GetFullPath(Path.Combine(RootDir, NormalizePath(LocalConfig.MergePluginsFolder)));
    public string EsmCacheDir => Path.Combine(CacheDir, "esm-cache");

    private FoGitRepo(string rootDir) {
        RootDir = rootDir;
        RepoConfig = RepoConfig.Load(RepoConfigPath);
        LocalConfig = LocalConfig.Load(LocalConfigPath);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// Find fo-git.json in the working directory or a parent folder and parse the config into a new FoGitRepo instance.  
    /// </summary>
    /// <returns>The configured FoGitRepo instance associated with the working directory.</returns>
    /// <exception cref="UserError">There is no fo-git.json in the working directory or its parent folders.</exception>
    public static FoGitRepo FindRepo() {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }) {
            var dir = new DirectoryInfo(start);
            while (dir != null) {
                if (File.Exists(Path.Combine(dir.FullName, "fo-git.json"))) {
                    return new FoGitRepo(dir.FullName);
                }

                dir = dir.Parent;
            }
        }
        
        throw new UserError("Couldn't find fo-git.json");
    }

    public static string NormalizePath(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    // ---------------------------------------------------------------- Git

    public (int code, string stdout, string stderr) GitRaw(params string[] args) => Proc.Run("git", args, RootDir);

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
            var dir = Path.Combine(CacheDir, "spriggit-cli", RepoConfig.SpriggitVersion);
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "Spriggit.CLI.exe" : "Spriggit.CLI");
            if (File.Exists(exe)) {
                return exe;
            }

            Ui.Info($"Downloading Spriggit {RepoConfig.SpriggitVersion}");
            var asset = OperatingSystem.IsWindows() ? "SpriggitCLI.zip" : "SpriggitLinuxCLI.zip";
            var url = $"https://github.com/Mutagen-Modding/Spriggit/releases/download/{RepoConfig.SpriggitVersion}/{asset}";
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
        var code = Proc.RunStreaming(SpriggitCli, args, RootDir, line => {
            if (line.Contains("ERR") || line.Contains("WRN") || line.Contains("Exception")) {
                Ui.Info(line.Trim());
            }
        });
        if (code != 0) {
            throw new UserError($"Spriggit failed with exit code {code}. Run this command for the full output.\n  \"{SpriggitCli}\" {string.Join(' ', args.Select(Proc.Quote))}");
        }
    }

    public void GenerateText(string esmPath, string textDir) {
        var tmp = TempDir("ser");
        try {
            RunSpriggit("serialize", "-i", esmPath, "-o", tmp, "-g", "FalloutNV",
                "-p", RepoConfig.SpriggitPackage, "-v", RepoConfig.SpriggitVersion, "-u", "true");
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
        var d = Path.Combine(CacheDir, "tmp", prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
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
    public static string HashFile(string path) {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs))[..16];
    }

    // Git tree hash of the spriggit folder
    public string TextTreeHash() {
        if (!Directory.Exists(TextTreeDir)) {
            return "missing";
        }

        var index = Path.Combine(CacheDir, "text-index");
        var realIndex = Path.GetFullPath(Path.Combine(RootDir, Git("rev-parse", "--git-path", "index")));
        if (File.Exists(realIndex)) {
            File.Copy(realIndex, index, overwrite: true);
        } else {
            TryDelete(index);
        }

        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        var folder = RepoConfig.SpriggitFolder.TrimEnd('/');
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
        var (code, stdout, stderr) = Proc.Run("git", args, RootDir, env);
        if (code != 0) {
            throw new UserError($"git {string.Join(' ', args)} failed.\n{stderr}{stdout}");
        }

        return stdout.Trim();
    }
    
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

    /// Update the build-stamp to record the current version of the ESM as synced with the current text tree (by checksums)
    public void WriteBuildInfo(string textTreeHash) {
        var esmFileInfo = new FileInfo(EsmPath);
        var info = new BuildInfo(textTreeHash, HashFile(EsmPath), esmFileInfo.Length, esmFileInfo.LastWriteTimeUtc, DateTime.Now);
        File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(info, Json.Options));
        
        CacheEsm(textTreeHash);
    }

    // ---------------------------------------------------------------- ESM cache

    // Copies of the ESM for each text git tree
    private string CachedEsmPath(string tree) => Path.Combine(EsmCacheDir, RepoConfig.SpriggitVersion, $"{tree}-{RepoConfig.Master}");

    public int CachedEsmCount => Directory.Exists(EsmCacheDir)
        ? Directory.EnumerateFiles(EsmCacheDir, "*", SearchOption.AllDirectories).Count()
        : 0;

    public bool RestoreCachedEsm(string tree) {
        var cached = CachedEsmPath(tree);
        if (LocalConfig.EsmCacheSize <= 0 || !File.Exists(cached)) {
            return false;
        }

        CopyWithRetry(cached, EsmPath);
        // Creation time is used as the last use time for pruning
        File.SetCreationTimeUtc(cached, DateTime.UtcNow);
        return true;
    }

    private void CacheEsm(string tree) {
        if (LocalConfig.EsmCacheSize <= 0 || tree == "missing" || !File.Exists(EsmPath)) {
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
            .OrderByDescending(f => f.CreationTimeUtc).Skip(LocalConfig.EsmCacheSize).ToList();
        foreach (var f in old) {
            TryDelete(f.FullName);
        }
        DeleteEmptyFolders(EsmCacheDir);
    }

    public bool RebaseInProgress() =>
        new[] { "rebase-merge", "rebase-apply" }.Any(d => Directory.Exists(Path.GetFullPath(Path.Combine(RootDir, Git("rev-parse", "--git-path", d)))));

    private bool EsmMatches(BuildInfo info) {
        var esm = new FileInfo(EsmPath);
        return (info.EsmSize == esm.Length && info.EsmWriteTime == esm.LastWriteTimeUtc) || HashFile(EsmPath) == info.EsmHash;
    }

    public BuildState GetBuildState() {
        var info = ReadBuildInfo();
        
        if (!File.Exists(EsmPath)) {
            return BuildState.EsmMissing;
        }

        if (info == null) {
            return BuildState.EsmNotTracked;
        }

        if (!EsmMatches(info)) {
            return BuildState.TextOutOfDate;
        }

        var textTreeHash = TextTreeHash();
        if (textTreeHash == info.TextTreeHash) {
            return BuildState.UpToDate;
        }

        return BuildState.EsmOutOfDate;
    }
}
