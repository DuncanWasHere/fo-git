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
