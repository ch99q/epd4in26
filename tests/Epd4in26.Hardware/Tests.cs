using Waveshare.Standalone;

namespace EpdTest.Hardware;

/// <summary>
/// One hardware test case. Carries everything the runner needs to drive the panel and brief
/// the human tester: a one-line summary, the visual checklist to inspect on the panel after
/// the test runs, and the action itself. Assertions inside <see cref="Run"/> catch protocol
/// regressions; the human catches anything the bus can't see (residue, ghosting, dead pixels).
/// </summary>
public sealed class TestCase
{
    public required string Name      { get; init; }
    public required string Summary   { get; init; }
    public required string LookFor   { get; init; }
    public required Action<Epd4in26> Run { get; init; }
}

public static class Tests
{
    public static readonly List<TestCase> All = new()
    {
        new TestCase
        {
            Name    = "clear",
            Summary = "Clear() drives the panel to all-white.",
            LookFor =
                "- Panel is uniformly white edge to edge.\n" +
                "- No faint residue from previous content; no stuck-on (black) or stuck-off (white) pixels.",
            Run = epd => epd.Clear(),
        },

        new TestCase
        {
            Name    = "baseline",
            Summary = "Render the 10x12 labelled tile grid as a fresh baseline.",
            LookFor =
                "- Every tile shows a label A1..J12 (col A-J, row 1-12). Read a few to confirm.\n" +
                "- All labels are sharp and complete (no missing pixels in any glyph).\n" +
                "- Grid lines are continuous and straight; no gaps or bowing.",
            Run = epd =>
            {
                // Any action is valid here -- Full (no cache yet), None (frame already on the
                // panel from a prior run), or Partial (cooldown after the preceding `clear` test
                // blocked promotion; the driver falls back to a full-frame 0x24 partial which
                // still renders the grid correctly).
                var r = epd.Render(Load("baseline-grid.png"));
                AssertOneOf(r.Action,
                    "first render must be Full, None, or Partial-via-cooldown-fallback",
                    RenderAction.Full, RenderAction.None, RenderAction.Partial);
            },
        },

        new TestCase
        {
            Name    = "noop",
            Summary = "Re-render an identical frame: the driver should send nothing to the panel.",
            LookFor =
                "- Panel did NOT flicker on the second render (no full-refresh flash, no partial flash).\n" +
                "- Grid still readable, identical to before.",
            Run = epd =>
            {
                var frame = Load("baseline-grid.png");
                epd.Render(frame);
                var r = epd.Render(frame);
                AssertEqual(RenderAction.None, r.Action,  "second render of identical frame should be None");
                AssertEqual(0,                 r.DirtyTiles, "no dirty tiles when frame matches cache");
            },
        },

        new TestCase
        {
            Name    = "partial-single",
            Summary = "Partial refresh of one tile: E6 flips to an inverted '02' on black.",
            LookFor =
                "- ONLY tile E6 changed; every other tile in the grid is untouched.\n" +
                "- The 'E6' label is GONE inside the inverted region -- no faint outline of the old text\n" +
                "  visible behind the white '02' (this is the residue check).\n" +
                "- '02' is crisp white-on-black; no grey halo or speckle.\n" +
                "- The four edges of the inverted tile align with the original grid lines.",
            Run = epd =>
            {
                epd.Render(Load("baseline-grid.png"));
                var r = epd.Render(Load("partial-single.png"));
                AssertEqual(RenderAction.Partial, r.Action,        "single-tile change must be Partial");
                AssertEqual(1,                    r.PartialChunks, "one dirty region -> one chunk");
            },
        },

        new TestCase
        {
            Name    = "partial-bbox",
            Summary = "Two corner tiles change (A1 + J12). The driver coalesces to one bounding-box chunk.",
            LookFor =
                "- A1 (top-left) and J12 (bottom-right) are now inverted with 'XX' / 'YY'.\n" +
                "- ALL tiles between them are UNCHANGED -- still showing their original labels.\n" +
                "  This is the key check: the bounding-box region is huge but the partial waveform only\n" +
                "  transitions pixels that actually differ from the previous frame.\n" +
                "- No partial-update banding or gradient across the middle of the panel.",
            Run = epd =>
            {
                epd.Render(Load("baseline-grid.png"));
                var r = epd.Render(Load("partial-bbox.png"));
                AssertEqual(RenderAction.Partial, r.Action,        "two isolated changes must still be Partial");
                AssertEqual(1,                    r.PartialChunks, "driver coalesces to a single bounding-box rect");
            },
        },

        new TestCase
        {
            Name    = "partial-two-locations",
            Summary = "Partial at B3, then a second partial at I10. The B3 update must NOT disappear.",
            LookFor =
                "- After the FIRST partial: only B3 is inverted ('AA'). Note the position.\n" +
                "- After the SECOND partial: B3 is STILL inverted ('AA') AND I10 is now inverted ('BB').\n" +
                "- CRITICAL: if B3 reverted to its baseline 'B3' label, the post-drive mirror to RAM\n" +
                "  0x26 isn't keeping the previous-frame reference in sync. The second partial's\n" +
                "  waveform then transitions B3 back toward the baseline 'B3' image -- the first\n" +
                "  partial 'disappears' and the panel falls back toward the last full render.\n" +
                "- No flicker between the two partials; both should be smooth partial-refresh transitions.",
            Run = epd =>
            {
                epd.Render(Load("baseline-grid.png"));
                var ra = epd.Render(Load("partial-loc-a.png"));
                AssertEqual(RenderAction.Partial, ra.Action,        "first partial (B3) must be Partial");
                AssertEqual(1,                    ra.PartialChunks, "single dirty tile -> one chunk");

                var rb = epd.Render(Load("partial-loc-b.png"));
                AssertEqual(RenderAction.Partial, rb.Action,        "second partial (I10) must be Partial");
                AssertEqual(1,                    rb.PartialChunks, "second diff is only I10 vs the cached frame -> one chunk");
            },
        },

        new TestCase
        {
            Name    = "ghost-budget",
            Summary = "Five back-to-back partial refreshes write 01..05 into E6. Stress test for residue.",
            LookFor =
                "- After step 1: E6 shows '01' inverted.\n" +
                "- After step 5: E6 shows '05' inverted ONLY.\n" +
                "- CRITICAL: the digits 01, 02, 03, 04 are NOT faintly visible behind the '05'.\n" +
                "  If you can read any prior digit through the current one, partial-refresh residue is\n" +
                "  accumulating beyond what the waveform clears.\n" +
                "- The inverted tile's black background remains uniformly black across all 5 steps.",
            Run = epd =>
            {
                epd.Render(Load("baseline-grid.png"));
                for (int n = 1; n <= 5; n++)
                {
                    var r = epd.Render(Load($"ghost-step-{n}.png"));
                    AssertEqual(RenderAction.Partial, r.Action, $"ghost step {n} must be Partial");
                }
            },
        },

        new TestCase
        {
            Name    = "auto-promote",
            Summary = "Sixth partial would overflow MaxPartialRefreshes -> Render auto-promotes to Full.",
            LookFor =
                "- Visible full-refresh flicker (panel briefly inverts, then settles).\n" +
                "- E6 shows '06' inverted; rest of grid clean and free of any ghosting from prior partials.\n" +
                "- If you saw NO flicker, the driver did a partial instead of promoting -- either the\n" +
                "  cooldown blocked the Full (lower EPD_TEST_INTERVAL), or the promotion logic regressed.",
            Run = epd =>
            {
                // Self-contained setup: 5 partials to spend the budget, then the 6th.
                epd.Render(Load("baseline-grid.png"));
                for (int n = 1; n <= 5; n++)
                    epd.Render(Load($"ghost-step-{n}.png"));

                var r = epd.Render(Load("auto-promote.png"));
                AssertFullOrCooldownFallback(epd, r,
                    "6th partial after a spent ghost budget should auto-promote to Full");
            },
        },

        new TestCase
        {
            Name    = "promote-large",
            Summary = "Change covers > 50% of tiles in one render -> Render promotes to Full immediately.",
            LookFor =
                "- Visible full-refresh flicker.\n" +
                "- Final image: alternating rows of black/white, each 1 tile tall, with their labels\n" +
                "  inverted on the black rows.\n" +
                "- No residue of the previous baseline grid visible on the new pattern.",
            Run = epd =>
            {
                epd.Render(Load("baseline-grid.png"));
                var r = epd.Render(Load("promote-large.png"));
                AssertFullOrCooldownFallback(epd, r,
                    ">= 50% dirty tiles should promote to Full");
            },
        },

        new TestCase
        {
            Name    = "sleep-wake",
            Summary = "Sleep() -> Init() -> Render: panel comes back from deep sleep cleanly.",
            LookFor =
                "- After Init, the baseline grid renders cleanly (visible full-refresh flicker).\n" +
                "- No garbage / static / partial frame from the wake transition.\n" +
                "- All 120 labels readable as in the 'baseline' test.",
            Run = epd =>
            {
                epd.Sleep();
                Thread.Sleep(1000);
                epd.Init();
                var r = epd.Render(Load("baseline-grid.png"));
                AssertEqual(RenderAction.Full, r.Action, "first render after Init must be Full");
            },
        },
    };

    public static TestCase? Find(string name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static byte[] Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "patterns", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Test pattern '{fileName}' not found at {path}. Run `EpdTest generate` to (re)create the PNGs.",
                path);
        return Epd4in26.PackFromFile(path);
    }

    static void AssertEqual<T>(T expected, T actual, string hint)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, got {actual} -- {hint}");
    }

    static void AssertOneOf<T>(T actual, string hint, params T[] allowed)
    {
        foreach (var a in allowed)
            if (EqualityComparer<T>.Default.Equals(actual, a))
                return;
        throw new Exception($"expected one of [{string.Join(", ", allowed)}], got {actual} -- {hint}");
    }

    // Tests that expect a Full refresh have to coexist with the 180 s panel-protection floor.
    // When Render WANTS to promote to Full but the cooldown blocks it, the driver falls back to
    // a Partial (full-frame 0x24 write + partial waveform) -- the panel renders the change
    // correctly, only the RenderAction label differs. Accept that path; require strict Full
    // only when the cooldown gate is open.
    static void AssertFullOrCooldownFallback(Epd4in26 epd, RenderResult r, string hint)
    {
        if (r.Action == RenderAction.Full) return;
        var remaining = epd.TimeUntilNextFullRefreshAllowed;
        if (r.Action == RenderAction.Partial && remaining > TimeSpan.Zero)
        {
            Console.WriteLine(
                $"      note: cooldown {remaining.TotalSeconds:F0}s -- Render fell back to Partial. " +
                "Lower EPD_TEST_INTERVAL to exercise the Full-promotion path.");
            return;
        }
        throw new Exception($"expected Full, got {r.Action} with cooldown clear -- {hint}");
    }
}
