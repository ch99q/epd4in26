# end4in26

A standalone .NET driver for the **Waveshare 4.26" e-Paper HAT** — 800 × 480, 1-bit
black-and-white, SSD1677 controller. Single file, drop-in or NuGet-installable, with
built-in panel protection.

```csharp
using var epd = new Epd4in26();
epd.Init();
epd.Render(Epd4in26.PackFromFile("hello.png"));   // first call → full refresh (~4 s)
epd.Render(Epd4in26.PackFromFile("hello2.png"));  // subsequent calls → partial when possible
epd.Sleep();
```

`Render` caches the previous frame, diffs against the new one, and picks full-vs-partial
automatically while honoring the 180 s full-refresh interval — back-to-back full refreshes
can **permanently damage the panel**. See [Refresh modes & cadence](#refresh-modes--cadence).

For the wire-level protocol, the RAM 0x24 vs 0x26 model, and the gotchas that made this
driver diverge from Waveshare's reference, see [PROTOCOL.md](PROTOCOL.md).

## Highlights

- **One file, one class.** [`src/Epd4in26/Epd4in26.cs`](src/Epd4in26/Epd4in26.cs) copy-pastes into any .NET 8+ project; two NuGet refs and you're done.
- **Smart `Render`.** Tile-based dirty detection, automatic full-vs-partial routing, automatic promotion to a full refresh when too much changed or the ghosting budget is spent.
- **Built-in panel protection.** Enforced 180 s minimum between full refreshes and a 5-partial ghosting budget; the cooldown clock is preserved across `Init` so callers can't bypass the limiter by reinitialising.
- **Correct multi-region partial refreshes.** Works around the SSD1677 "SetWindow blanking" gotcha that erases previously-updated regions when later partials drive elsewhere ([details](PROTOCOL.md#the-setwindow-blanking-gotcha)).
- **Honest BUSY handling.** Polls the SSD1677's HIGH-busy polarity correctly (opposite of UC8xxx panels) and surfaces stuck panels as a `TimeoutException`.
- **Hardware test suite** with [nine human-verified scenarios](tests/Epd4in26.Hardware/VISUAL.md) so regressions surface fast.

## Table of contents

- [Install](#install)
- [Quick start](#quick-start)
- [Hardware](#hardware)
- [Refresh modes & cadence](#refresh-modes--cadence)
- [API reference](#api-reference)
- [Tuning](#tuning)
- [Build](#build)
- [Hardware test suite](#hardware-test-suite)
- [Deploy to a Pi](#deploy-to-a-pi)
- [Troubleshooting](#troubleshooting)
- [Project layout](#project-layout)
- [Contributing](#contributing)
- [License](#license)

## Install

Either reference the NuGet package:

```xml
<PackageReference Include="Waveshare.Epd4in26" Version="0.1.0" />
```

…or copy [`src/Epd4in26/Epd4in26.cs`](src/Epd4in26/Epd4in26.cs) into your project and add
the two upstream dependencies it needs:

```xml
<PackageReference Include="System.Device.Gpio"   Version="3.2.0" />
<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />
```

Targets `net8.0` and newer. The driver compiles anywhere but `GpioController` only works
at runtime on Linux with `/dev/spidev*` and `/dev/gpiochip*` available — i.e. a Raspberry
Pi (or compatible SBC) with SPI enabled in `raspi-config`.

## Quick start

Render a PNG, inspect the outcome, sleep the panel:

```csharp
using Waveshare.Standalone;

using var epd = new Epd4in26();
epd.Init();

var frame  = Epd4in26.PackFromFile("hello.png");   // any ImageSharp format → 1bpp packed
var result = epd.Render(frame);

Console.WriteLine($"{result.Action} — {result.DirtyTiles}/{result.TotalTiles} tiles, "
                + $"{result.PartialChunks} chunks, waited {result.WaitedForCooldown.TotalSeconds:F0}s");

epd.Sleep();
```

Typical output across a session:

```
Full    — 120/120 tiles, 0 chunks, waited 0s     ← first frame (no cache)
None    — 0/120 tiles, 0 chunks, waited 0s       ← identical frame, nothing sent
Partial — 3/120 tiles, 1 chunks, waited 0s       ← small overlay change
Full    — 78/120 tiles, 0 chunks, waited 142s    ← big change AND inside cooldown
```

If you already have an `Image<Rgba32>` from ImageSharp drawing operations, you can skip
the explicit `PackFromFile` step:

```csharp
using var img = new Image<Rgba32>(800, 480, Color.White);
img.Mutate(ctx => ctx.DrawText("Hello", font, Color.Black, new PointF(20, 20)));
epd.Render(img);   // packs to 1 bpp internally
```

For thread-safe usage where you'd rather drop a frame than queue, use `TryRender` /
`TryClear` — they return `false` immediately if another refresh is already in progress.

## Hardware

### Specifications

| Spec | Value | Source |
|------|-------|--------|
| Resolution | 800 × 480, 1 bpp B/W | Datasheet §3 |
| Active area | 92.8 × 55.8 mm (219 PPI) | Datasheet §3 |
| Controller | SSD1677 | Datasheet §3 |
| Logic supply (VCI) | 2.2 – 3.6 V (typ 3.3 V) | Datasheet §7.1 |
| SPI clock | up to 20 MHz write / 2.5 MHz read | Datasheet §7.4 |
| Operating temperature | 0 – 50 °C | Datasheet §6 |
| Lifetime | ~1,000,000 refreshes / 5 years | Datasheet §7.3 |
| Operating current | 8 mA typ (white state) | Datasheet §7.1 |
| Sleep current | 40 µA typ (RAM retained) | Datasheet §7.1 |
| Deep sleep current | 2 µA typ | Datasheet §7.1 |

Datasheets are committed under [`resources/datasheets/`](resources/datasheets/).

### Pinout

Raspberry Pi 40-pin header, BCM numbering. The driver constructor defaults match this
table; override any pin via the constructor.

| HAT pin | Pi pin (BCM) | Function |
|---------|--------------|----------|
| `VCC` | 3.3 V | Logic supply |
| `GND` | GND | Ground |
| `DIN` | GPIO 10 (MOSI) | SPI data in |
| `CLK` | GPIO 11 (SCLK) | SPI clock |
| `CS`  | GPIO 8 | Chip select (active low) |
| `DC`  | GPIO 25 | Data / command select |
| `RST` | GPIO 17 | Hardware reset |
| `BUSY`| GPIO 24 | Busy (HIGH = busy on SSD1677) |
| `PWR` | GPIO 18 | Newer HATs gate panel VCC here — must be HIGH |

Detail on the SPI parameters, BUSY polarity, and the wire-level command/data discrimination
lives in [PROTOCOL.md](PROTOCOL.md#hardware-interface).

## Refresh modes & cadence

E-paper panels can be **physically destroyed by software** if you violate refresh-cadence
rules. Internalise these before touching display code.

### Hard rules (failure modes are permanent)

1. **At least one full refresh every 24 hours.** Otherwise ghosting / image sticking sets in. Datasheet §13(5).
2. **Minimum 180 s between full refreshes** in steady-state operation. Lowering this risks permanent panel damage. Partial refreshes are exempt from the 180 s floor.
3. **Never run partial-only forever.** After ~5 partials between full refreshes, residual ghosting accumulates and can damage the panel. Waveshare wiki: *"the residual image problem will become more and more serious, or even damage the screen."*
4. **Always sleep or power off after a refresh.** Leaving the panel powered up between updates burns out the panel — explicitly listed by Waveshare as unrepairable damage. Deep sleep pulls ~2 µA.
5. **Re-init after deep sleep.** Once `0x10 0x03` (deep sleep mode 2) is sent, the panel ignores image data until a new init sequence runs. The first wake also tends to refresh dirty — do a full refresh once before any partials.
6. **Switching from partial back to full requires a fresh init**, not just a different `0x22` byte. Waveshare wiki: *"Why can't the image be displayed when full refresh after partial refresh?"*
7. **Ship and store the panel showing a fully white image** to avoid bake-in.

### What the driver enforces vs. what you own

| Rule | Enforced by driver | Caller's responsibility |
|------|--------------------|-------------------------|
| 180 s minimum between full refreshes | ✅ `MinFullRefreshInterval` (blocks `DisplayBase`/`Clear`, defers `Render` promotions) | — |
| Max ~5 partials before a full refresh | ✅ `MaxPartialRefreshes` (auto-promote in `Render`) | — |
| 24 h max without a full refresh | ❌ | Schedule one in your app |
| Always sleep / power off after a refresh | ❌ | Call `Sleep()` |
| Re-init after deep sleep | ❌ | Call `Init()` after wake |
| Ship / store with a white image | ❌ | Call `Clear()` before shutdown |

### Refresh timings (at 23 °C, datasheet §7.1)

| Mode | `0x22` arg | Typical time | When `Render` picks it |
|------|------------|--------------|------------------------|
| **Full** (Normal) | `0xF7` | ~4 s | First render, cache invalid, change ratio ≥ 50 % of tiles, ghosting budget overflow |
| **Fast** | `0xC7` | ~1 s | Selected per-Init via `Init(RefreshMode.Fast)`; replaces Normal as the full-refresh waveform |
| **Partial** | `0xFF` | ~0.76 s | Cache hit + small change + cooldown active or change ratio under 50 % |

Fast mode loads a lower-power LUT from OTP at Init time (`0x1A 0x5A` then `0x22 0x91`).
It clears less ghosting per cycle than Normal, so pair it with periodic Normal refreshes
if you switch.

## API reference

### Construction

```csharp
public Epd4in26(
    int spiBus = 0, int spiChipSelect = 0, int spiClockHz = 20_000_000,
    int resetPin = 17, int dcPin = 25, int csPin = 8, int busyPin = 24, int powerPin = 18,
    TimeSpan? minFullRefreshInterval = null,
    int maxPartialRefreshes = 5);
```

All hardware-side parameters are overridable for non-default wiring; the safety knobs
default to the values Waveshare's documentation recommends. `minFullRefreshInterval`
defaults to `TimeSpan.FromSeconds(180)` if you pass `null`.

### Public members

| Member | Returns | What it does |
|--------|---------|--------------|
| `Init(RefreshMode mode = Normal)` | `void` | Run the power-up / reset / register-setup sequence. Required after construction and after `Sleep`. Preserves the 180 s cooldown clock. Pass `RefreshMode.Fast` to use the ~1 s fast waveform for subsequent full refreshes. |
| `Render(byte[] frame)` | `RenderResult` | Main entry point. Diffs against the cached previous frame and chooses full / partial / no-op. Honours the cooldown limiter. |
| `Render(Image<Rgba32> image, byte threshold = 128)` | `RenderResult` | Same, but accepts an ImageSharp image and packs it to 1 bpp first. |
| `TryRender(Image<Rgba32> image, byte threshold = 128)` | `bool` | Drop-on-busy variant. Returns `false` immediately if another refresh is in progress. |
| `Clear()` | `void` | Full refresh to white. Counts against the 180 s cooldown. |
| `TryClear()` | `bool` | Drop-on-busy variant of `Clear`. |
| `Sleep()` | `void` | Deep sleep mode 2 (~2 µA). Call `Init` to wake. |
| `Dispose()` | `void` | Drive all output pins low, release GPIO and SPI handles. |
| `static PackFromFile(string path, byte threshold = 128)` | `byte[]` | Load any ImageSharp-supported file and pack to the 1 bpp packed frame format. |
| `static PackFromImage(Image<Rgba32> image, byte threshold = 128)` | `byte[]` | Pack an in-memory image to the 1 bpp frame format. |

### Read-only observables

| Property | Type | Meaning |
|----------|------|---------|
| `MinFullRefreshInterval` | `TimeSpan` | Floor between full refreshes (set via constructor). |
| `MaxPartialRefreshes` | `int` | Ghosting budget before `Render` auto-promotes. |
| `TimeUntilNextFullRefreshAllowed` | `TimeSpan` | `Zero` when a full refresh is allowed; otherwise the remaining cooldown. |

### Constants

| Constant | Value | Meaning |
|----------|-------|---------|
| `Epd4in26.Width` | `800` | Panel pixel width |
| `Epd4in26.Height` | `480` | Panel pixel height |
| `Epd4in26.FrameSize` | `48000` | Bytes per packed 1 bpp frame (`Width × Height / 8`) |

### `RenderResult`

```csharp
public readonly record struct RenderResult(
    RenderAction Action,        // None | Partial | Full
    int DirtyTiles,
    int TotalTiles,             // currently 120 (10 × 12 tile grid)
    int PartialChunks,
    TimeSpan WaitedForCooldown  // > 0 only when the limiter blocked before a Full
);

public double ChangedRatio => TotalTiles > 0 ? (double)DirtyTiles / TotalTiles : 0;
```

`Action` is the contract you assert against in tests:

- **`None`** — frame matched the cache; nothing sent to the panel.
- **`Partial`** — one partial-refresh chunk was sent for the dirty region.
- **`Full`** — first frame, large change, ghosting-budget overflow, or stale partial base.

### Frame format

- **Layout:** 1 bpp packed, MSB-first per byte. **Bit 1 = white**, bit 0 = black.
- **Size:** `Width × Height / 8 = 48 000 bytes` (`Epd4in26.FrameSize`).
- **X coordinates and widths must be multiples of 8** — the SSD1677 ignores the low 3 bits of the X cursor.

Full byte-layout and packing details in [PROTOCOL.md](PROTOCOL.md#frame-format).

## Tuning

Both safety knobs are constructor parameters; they're surfaced as read-only properties on
the instance.

| Parameter | Default | Effect of lowering |
|-----------|---------|--------------------|
| `minFullRefreshInterval` | `TimeSpan.FromSeconds(180)` | **Risks permanent panel damage.** Use only in development against a panel you can replace. Visible regressions: faster ghost accumulation, visible flicker bands, ultimately stuck pixels. |
| `maxPartialRefreshes` | `5` | More aggressive ghost accumulation between full refreshes. Lower to be more conservative, raise carefully. |

The tile grid (80 × 40, 10 × 12 = 120 tiles) and the 0.5 promote-to-Full threshold are
compile-time `private const` — change them in [Epd4in26.cs](src/Epd4in26/Epd4in26.cs) if
you're tuning for a different workload profile. They aren't surfaced as runtime knobs
because they affect cache compatibility across the lifetime of the driver instance.

### Example: development iteration

```csharp
// Dangerous on a production panel; fine when iterating on a test rig.
using var epd = new Epd4in26(minFullRefreshInterval: TimeSpan.FromSeconds(5));
```

The hardware test runner exposes this as `EPD_TEST_INTERVAL` so the suite can complete
in a few minutes rather than ~30.

## Build

```sh
dotnet build                                       # builds both projects
dotnet build src/Epd4in26                          # just the library
dotnet build tests/Epd4in26.Hardware               # just the test runner (binary: EpdTest)
```

`Directory.Build.props` sets `TreatWarningsAsErrors=true` and `Nullable=enable` for both
projects. The TFM is pinned in `Directory.Build.props` so changing it once updates the
publish path in `deploy.sh` automatically.

The library project is NuGet-pack-ready:

```sh
dotnet pack src/Epd4in26 -c Release
# -> src/Epd4in26/bin/Release/Waveshare.Epd4in26.0.1.0.nupkg
```

## Hardware test suite

[`tests/Epd4in26.Hardware/`](tests/Epd4in26.Hardware/) is a console runner (binary
`EpdTest`) that drives the panel through nine scripted scenarios. Each test pairs a
`RenderResult` assertion (catches protocol regressions) with a printed visual checklist
and a `y/n/skip` prompt (catches everything the SPI bus can't see — residue, ghosting,
dead pixels). The full follow-along is in
[`tests/Epd4in26.Hardware/VISUAL.md`](tests/Epd4in26.Hardware/VISUAL.md).

```sh
EpdTest list                  # show tests + summaries
EpdTest all                   # run all tests, prompt y/n after each
EpdTest partial-single        # run one test with the prompt
EpdTest --no-prompt all       # CI / smoke: assertions only, no visual judgement
EpdTest generate              # (re)render PNGs into resources/patterns/
EpdTest image ./pic.png       # ad-hoc: render an 800x480 image file
```

### Tests

| Name | Asserts | Visual focus |
|------|---------|--------------|
| `clear` | (visual only) | Uniformly white, no residue or dead pixels |
| `baseline` | First `Render` after `Init` is `Full`, `None`, or `Partial` (cooldown fallback) | All 120 tile labels A1–J12 sharp & complete |
| `noop` | Re-rendering the same frame is `None`, 0 dirty tiles | Second render does not flicker |
| `partial-single` | One tile change → `Partial`, exactly 1 chunk | Replaced tile has no residue of prior label |
| `partial-bbox` | Two corner-tile changes → `Partial`, 1 chunk (bounding box) | Middle tiles untouched by the huge bbox |
| `partial-two-locations` | Two partials at different locations → both `Partial` | First update still on panel after the second (catches the SetWindow-blanking regression) |
| `ghost-budget` | 5 successive partials to the same tile → all `Partial` | No accumulated digit ghosting after step 5 |
| `auto-promote` | 6th partial after a full budget → auto-promotes to `Full` (cooldown allowing) | Visible full-refresh flicker; clean result |
| `promote-large` | ≥ 50 % of tiles dirty in one render → `Full` (cooldown allowing) | Clean alternating-row pattern, no residue |
| `sleep-wake` | `Sleep` → `Init` → `Render` returns `Full` | Clean wake; no garbage between frames |

Test PNGs are pre-rendered by
[`PatternGenerator.cs`](tests/Epd4in26.Hardware/PatternGenerator.cs) (ImageSharp +
SixLabors.Fonts) and committed under
[`tests/Epd4in26.Hardware/resources/patterns/`](tests/Epd4in26.Hardware/resources/patterns/)
so the runner has no font or drawing-library dependencies at test time. Regenerate after
editing the generator: `EpdTest generate`. The bundled
[`Inter-Bold.ttf`](tests/Epd4in26.Hardware/resources/fonts/Inter-Bold.ttf) (SIL OFL 1.1)
ships alongside.

### Environment variables

| Variable | Default | Notes |
|----------|---------|-------|
| `EPD_TEST_INTERVAL` | `180` | `MinFullRefreshInterval` in seconds, forwarded by `deploy.sh`. Below 180 s, the test runner prints a warning and the cooldown-related tests (`auto-promote`, `promote-large`) exercise the strict Full-promotion path rather than the cooldown-fallback path. |
| `NO_COLOR` | unset | Any value suppresses ANSI colours. |

## Deploy to a Pi

[`deploy.sh`](deploy.sh) publishes the test runner self-contained, rsyncs to the Pi, and
optionally runs the suite over SSH. It auto-detects the TFM from `Directory.Build.props`
so a TFM bump doesn't desync the publish path. Defaults target a Pi Zero 2 W
(`linux-arm64`, hostname `raspberrypi.local`, user `pi`); an SSH preflight runs before
publishing so a missing host fails fast.

```sh
./deploy.sh                                # build + rsync, no run
./deploy.sh run                            # build + rsync + run "all"
./deploy.sh run partial-single             # build + rsync + run a single test
./deploy.sh run --no-prompt all            # CI / smoke mode
./deploy.sh run image ./pic.png            # ad-hoc image render
```

Override defaults via env vars:

```sh
PI_HOST=epd.local PI_USER=christian RID=linux-arm ./deploy.sh run
EPD_TEST_INTERVAL=5 ./deploy.sh run        # forward the interval to the Pi
```

The publish is self-contained (~80 MB), so no .NET runtime install is needed on the Pi.
The Pi just needs SPI enabled (`sudo raspi-config` → Interface Options → SPI) and the HAT
wired correctly.

### VS Code tasks

[`.vscode/tasks.json`](.vscode/tasks.json) exposes the common loops:

- **build** — `dotnet build tests/Epd4in26.Hardware` (default build task)
- **deploy** — `./deploy.sh`
- **deploy + run all tests (visual prompts)** — `./deploy.sh run`
- **deploy + run all tests (--no-prompt)** — assertion-only mode
- **deploy + run test** — pick one from the dropdown (default test task)
- **deploy + render image** — prompts for an image path
- **regenerate test PNGs (local)** — runs the generator on your laptop, no Pi roundtrip

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `TimeoutException: BUSY pin still high after 50000 ms` | Panel disconnected, `PWR` pin low on newer HATs, or controller hung. | Check wiring (especially `PWR` = GPIO 18 HIGH on newer HATs). Power-cycle the panel. |
| Garbled or scrambled pixels | SPI mode/clock wrong; cable too long; wrong RID published. | Confirm SPI Mode 0; lower `spiClockHz` to 2 MHz in the constructor; check `RID=linux-arm` vs `linux-arm64`. |
| Partial refresh "loses" a previous region when you partial elsewhere | You're on an older version of the driver, or a downstream fork has reverted the full-frame `0x24` write. | Update to a build that includes the `partial-two-locations` test and the matching `DisplayPartial` implementation. Details in [PROTOCOL.md](PROTOCOL.md#the-setwindow-blanking-gotcha). |
| Visible ghosting that doesn't go away | More than ~5 consecutive partials without a full refresh. | Lower `maxPartialRefreshes` or call `Clear()` periodically. |
| `dotnet publish` succeeds but the binary crashes on Pi | Published with the wrong RID for the OS bitness. | 32-bit Raspberry Pi OS → `RID=linux-arm`; 64-bit → `RID=linux-arm64`. |
| Image looks inverted (black/white swapped) | Threshold or bit-packing inverted in your pipeline. | Remember: **bit 1 = white**. `PackFromFile` / `PackFromImage` handle this correctly by default. |
| `Render` returns `Partial` when you expected `Full` | Cooldown was still active when the promotion check fired; the driver fell back to a full-frame partial instead of `Thread.Sleep`-ing the UI thread. | Wait `epd.TimeUntilNextFullRefreshAllowed` and retry, or call `Clear()` / construct a fresh frame which forces a `Full` after the cooldown elapses. |

## Project layout

```
.
├── src/
│   └── Epd4in26/                       # net8.0+ class library (NuGet-pack ready)
│       ├── Epd4in26.csproj
│       └── Epd4in26.cs                 # the driver (single file, drop-in)
├── tests/
│   └── Epd4in26.Hardware/              # hardware integration test suite (binary: EpdTest)
│       ├── Epd4in26.Hardware.csproj
│       ├── Program.cs                  # CLI: list / all / <name> / generate / image
│       ├── Tests.cs                    # test catalogue + assertions + visual checklists
│       ├── PatternGenerator.cs         # ImageSharp + SixLabors.Fonts PNG generator
│       ├── VISUAL.md                   # human follow-along; one section per test
│       └── resources/
│           ├── fonts/                  # Inter-{Bold,Regular}.ttf (SIL OFL 1.1)
│           └── patterns/               # committed 1bpp test PNGs (regen via EpdTest generate)
├── PROTOCOL.md                         # wire protocol reference, RAM model, diagrams
├── README.md                           # you are here
├── Directory.Build.props               # shared csproj props (TFM, nullable, warnings)
├── end4in26.slnx
├── deploy.sh                           # publish + rsync to Pi, optional remote run
├── .vscode/
│   └── tasks.json                      # build / deploy / deploy+run shortcuts
├── resources/
│   ├── datasheets/                     # panel user manual + SSD1677 controller PDFs
│   └── e-Paper-reference/              # Waveshare upstream repo (gitignored)
├── .editorconfig
├── LICENSE                             # MIT
└── CLAUDE.md                           # notes for AI assistants working on this repo
```

[`resources/e-Paper-reference/`](resources/) is an optional shallow clone of
[waveshareteam/e-Paper](https://github.com/waveshareteam/e-Paper) for cross-referencing
the canonical C/Python driver. It's gitignored; clone it locally if you want to compare:

```sh
git clone --depth=1 https://github.com/waveshareteam/e-Paper.git resources/e-Paper-reference
```

## Contributing

### Commit messages

This repo uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>[optional scope]: <description>

[optional body]
```

`<type>` is one of `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`. Examples:

```
feat(driver): add 4-gray render path
fix(tests): correct expected chunk count for partial-multi
docs(protocol): document the SetWindow blanking gotcha
refactor: extract ValidatePartialRegion helper
```

**Please don't co-author commits with AI assistants** — it muddles attribution and isn't
an accurate reflection of who contributed.

### Before submitting changes that touch display code

Read [`PROTOCOL.md`](PROTOCOL.md) and the hardware refresh-cadence rules
[above](#hard-rules-failure-modes-are-permanent). Back-to-back full refreshes have
already destroyed one panel on this project.

If your change might affect the partial-refresh path, run the
[`partial-two-locations`](tests/Epd4in26.Hardware/VISUAL.md) test in particular — it
catches the failure mode where a later partial erases an earlier one.

## License

MIT — see [LICENSE](LICENSE). Mirrors and improves upon the Waveshare reference at
[github.com/waveshareteam/e-Paper](https://github.com/waveshareteam/e-Paper)
(`RaspberryPi_JetsonNano/c/lib/e-Paper`).
