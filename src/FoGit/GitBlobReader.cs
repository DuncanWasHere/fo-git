using System.Diagnostics;
using System.Text;

namespace FoGit;

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
