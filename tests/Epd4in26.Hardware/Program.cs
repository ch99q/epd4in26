// EpdTest -- hardware integration test runner for the Waveshare 4.26" e-Paper HAT driver.
//
// Usage:
//   EpdTest                       # show available commands and tests
//   EpdTest list                  # same
//   EpdTest all                   # run every test in sequence, prompt y/n after each
//   EpdTest <test-name>           # run a single test with the same prompt
//   EpdTest --no-prompt all       # CI mode: assertions only, no visual prompts
//   EpdTest --no-prompt <name>    # same, single test
//   EpdTest generate              # (re)render the PNGs under resources/patterns/
//   EpdTest image <path>          # ad-hoc: render an 800x480 image file
//
// The full visual checklist for every test lives in tests/Epd4in26.Hardware/VISUAL.md --
// read it alongside the panel when running tests interactively.
//
// Env vars:
//   EPD_TEST_INTERVAL=180         # MinFullRefreshInterval seconds. Below 180 risks panel damage.
//   NO_COLOR=1                    # suppress ANSI color output

using System.Diagnostics;
using EpdTest.Hardware;
using Waveshare.Standalone;

var (noPrompt, positional) = ParseArgs(args);
var cmd = positional.Count > 0 ? positional[0] : "list";

if (cmd is "list" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (cmd == "generate")
{
    return RunGenerate();
}

// All remaining commands talk to the hardware. Honour the cooldown override env var.
var intervalSec = double.TryParse(
    Environment.GetEnvironmentVariable("EPD_TEST_INTERVAL"),
    System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture,
    out var s) ? s : 180.0;

if (intervalSec < 180.0)
{
    Console.WriteLine(Ansi.Yellow($"!! MinFullRefreshInterval = {intervalSec}s (below 180s safety floor)."));
    Console.WriteLine(Ansi.Yellow($"!! Lowered intervals risk permanent panel damage. Dev iteration only."));
    Console.WriteLine();
}

using var epd = new Epd4in26(minFullRefreshInterval: TimeSpan.FromSeconds(intervalSec));
epd.Init();

int failures = 0;
var totalSw = Stopwatch.StartNew();
try
{
    failures = cmd switch
    {
        "image" => RunImage(epd, positional),
        "all"   => RunSuite(epd, Tests.All, noPrompt),
        _ when Tests.Find(cmd) is { } single
                => RunSuite(epd, new List<TestCase> { single }, noPrompt),
        _       => Unknown(cmd),
    };
}
finally
{
    epd.Sleep();
}
totalSw.Stop();

if (cmd == "all" || Tests.Find(cmd) is not null)
{
    int total  = cmd == "all" ? Tests.All.Count : 1;
    int passed = total - failures;
    var summary = failures == 0
        ? Ansi.Green($"{passed}/{total} passed")
        : $"{Ansi.Green($"{passed} passed")}, {Ansi.Red($"{failures} failed")}";
    Console.WriteLine();
    Console.WriteLine(Ansi.Bold($"  {summary}  ({totalSw.Elapsed.TotalSeconds:F1}s)"));
}

return failures == 0 ? 0 : 1;


static (bool noPrompt, List<string> positional) ParseArgs(string[] argv)
{
    bool noPrompt = false;
    var rest = new List<string>(argv.Length);
    foreach (var a in argv)
    {
        if (a is "--no-prompt" or "--quiet") noPrompt = true;
        else rest.Add(a);
    }
    return (noPrompt, rest);
}

static void PrintHelp()
{
    Console.WriteLine("Commands:");
    Console.WriteLine("  EpdTest list                 # this message");
    Console.WriteLine("  EpdTest all                  # run every test, prompt y/n after each");
    Console.WriteLine("  EpdTest <test-name>          # run one test with the y/n prompt");
    Console.WriteLine("  EpdTest --no-prompt all|<name>  # assertion-only mode (CI)");
    Console.WriteLine("  EpdTest generate             # (re)render PNGs into resources/patterns/");
    Console.WriteLine("  EpdTest image <path>         # ad-hoc render an 800x480 image");
    Console.WriteLine();
    Console.WriteLine("Tests:");
    foreach (var t in Tests.All)
        Console.WriteLine($"  {Ansi.Cyan(t.Name.PadRight(16))} {Ansi.Dim(t.Summary)}");
}

static int RunGenerate()
{
    // resources/patterns/ is a Content folder in the csproj. We write to the SOURCE tree so
    // the committed PNGs update; tests load from the build output (where the same files get
    // copied by the SDK). Falls back to bin/<>/resources/patterns/ when the source path isn't
    // resolvable (e.g. running the published binary on the Pi).
    var sourceDir = FindSourcePatternsDir();
    var outDir = sourceDir ?? Path.Combine(AppContext.BaseDirectory, "resources", "patterns");
    PatternGenerator.GenerateAll(outDir);
    Console.WriteLine($"Wrote patterns to {outDir}");
    if (sourceDir is null)
        Console.WriteLine(Ansi.Yellow("Note: wrote to build output; commit by re-running locally with the source tree present."));
    return 0;
}

static string? FindSourcePatternsDir()
{
    // Walk up from AppContext.BaseDirectory looking for tests/Epd4in26.Hardware/resources/patterns/.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "resources", "patterns");
        if (Directory.Exists(Path.Combine(dir.FullName, "resources", "fonts"))
            && File.Exists(Path.Combine(dir.FullName, "Epd4in26.Hardware.csproj")))
        {
            Directory.CreateDirectory(candidate);
            return candidate;
        }
        dir = dir.Parent;
    }
    return null;
}

static int RunSuite(Epd4in26 epd, List<TestCase> tests, bool noPrompt)
{
    int failures = 0;
    foreach (var test in tests)
    {
        Console.WriteLine();
        Console.WriteLine(Ansi.Bold($"  {test.Name}"));
        Console.WriteLine($"  {Ansi.Dim(test.Summary)}");

        var sw = Stopwatch.StartNew();
        Exception? failure = null;
        try { test.Run(epd); }
        catch (Exception ex) { failure = ex; }
        sw.Stop();

        if (failure is not null)
        {
            failures++;
            Console.WriteLine($"  {Ansi.Red("ASSERT FAIL")} {Ansi.Dim($"({sw.Elapsed.TotalSeconds:F1}s)")}");
            Console.WriteLine($"      {Ansi.Red(failure.GetType().Name)}: {failure.Message}");
            continue;
        }

        Console.WriteLine($"  {Ansi.Green("ASSERT PASS")} {Ansi.Dim($"({sw.Elapsed.TotalSeconds:F1}s)")}");

        if (noPrompt) continue;

        Console.WriteLine($"  {Ansi.Bold("Look for on the panel:")}");
        foreach (var line in test.LookFor.Split('\n'))
            Console.WriteLine($"    {line.TrimEnd()}");

        Console.Write($"  {Ansi.Bold("Pass visual check?")} [y/n/skip] ");
        var verdict = ReadVerdict();
        switch (verdict)
        {
            case 'y':
                Console.WriteLine($"  {Ansi.Green("VISUAL PASS")}");
                break;
            case 'n':
                failures++;
                Console.WriteLine($"  {Ansi.Red("VISUAL FAIL")}");
                Console.Write($"  Note (optional, Enter to skip): ");
                var note = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(note))
                    Console.WriteLine($"  {Ansi.Dim($"note: {note}")}");
                break;
            default:
                Console.WriteLine($"  {Ansi.Yellow("VISUAL SKIPPED")}");
                break;
        }
    }
    return failures;
}

static char ReadVerdict()
{
    while (true)
    {
        var line = Console.ReadLine();
        if (line is null) return 's';
        line = line.Trim().ToLowerInvariant();
        if (line is "y" or "yes")  return 'y';
        if (line is "n" or "no")   return 'n';
        if (line is "s" or "skip" or "") return 's';
        Console.Write("    Please answer y, n, or skip: ");
    }
}

static int RunImage(Epd4in26 epd, List<string> argv)
{
    if (argv.Count < 2)
    {
        Console.Error.WriteLine("Usage: EpdTest image <path>");
        return 1;
    }
    var path = argv[1];
    Console.WriteLine($"Loading {path}...");
    var frame = Epd4in26.PackFromFile(path);
    var sw = Stopwatch.StartNew();
    var r = epd.Render(frame);
    sw.Stop();
    Console.WriteLine($"  {r.Action} ({sw.Elapsed.TotalSeconds:F1}s, {r.DirtyTiles}/{r.TotalTiles} tiles, {r.PartialChunks} chunks)");
    if (r.WaitedForCooldown > TimeSpan.Zero)
        Console.WriteLine($"  waited {r.WaitedForCooldown.TotalSeconds:F0}s for cooldown");
    return 0;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown command/test '{cmd}'. Run 'list' for help.");
    return 1;
}


static class Ansi
{
    static readonly bool Enabled =
        !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static string Green(string s)  => Wrap(s, "[32m");
    public static string Red(string s)    => Wrap(s, "[31m");
    public static string Yellow(string s) => Wrap(s, "[33m");
    public static string Cyan(string s)   => Wrap(s, "[36m");
    public static string Dim(string s)    => Wrap(s, "[2m");
    public static string Bold(string s)   => Wrap(s, "[1m");

    static string Wrap(string s, string code) => Enabled ? $"{code}{s}[0m" : s;
}
