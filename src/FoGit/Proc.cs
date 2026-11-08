using System.Diagnostics;
using System.Text;

namespace FoGit;

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
