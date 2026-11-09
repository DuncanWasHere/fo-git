namespace FoGit;

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

    public static bool? Confirm(string problem, string prompt, bool canAbort) {
        Warnings++;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) {
            Write(ConsoleColor.Yellow, "[warning] " + problem);
            return null;
        }

        if (!Console.IsInputRedirected) {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"[warning] {problem} {prompt} [Y/N]" + (canAbort ? " [Esc to abort] " : " "));
            Console.ForegroundColor = old;
            while (true) {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Y || k.Key == ConsoleKey.N) {
                    Console.WriteLine(k.Key);
                    return k.Key == ConsoleKey.Y;
                }
                if (k.Key == ConsoleKey.Escape && canAbort) {
                    Console.WriteLine("Esc");
                    return null;
                }
            }
        }

        Write(ConsoleColor.Yellow, "[warning] " + problem);
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        var text = $"{problem}\n\n{prompt}" + (canAbort ? "\n\nPress Cancel to abort." : "");
        const uint yesNoCancel = 0x3, yesNo = 0x4, warning = 0x30, foreground = 0x10000, topmost = 0x40000;
        return MessageBoxW(IntPtr.Zero, text, "fo-git", (canAbort ? yesNoCancel : yesNo) | warning | foreground | topmost) switch {
            6 => true,
            7 => false,
            _ => null,
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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
