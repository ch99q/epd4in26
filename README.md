# epd4in26

> Single-file .NET driver for the Waveshare 4.26" e-Paper HAT.

Show images, dashboards, clocks, or status screens on the 800 × 480 black-and-white
e-paper panel from a .NET app on a Raspberry Pi. Drop one source file into your project,
call `Render`, and the driver picks the right refresh path while keeping the panel
within the manufacturer's safety limits.

```csharp
using Waveshare.Standalone;
using SixLabors.ImageSharp;

using var epd = new Epd4in26();
epd.Init();
epd.Render(Image.Load<Rgba32>("hello.png"));
epd.Sleep();
```

That's the whole API for the common case. The first `Render` after `Init` does a full
refresh (~4 seconds); subsequent calls do a partial refresh (~0.76 seconds) when the
change is small.

## Is this for you?

**Yes if you have:**

- The [Waveshare 4.26" e-Paper HAT](https://www.waveshare.com/4.26inch-e-paper-hat.htm) — the 800 × 480 black-and-white one driven by the SSD1677 controller
- A Raspberry Pi (or compatible SBC) with SPI enabled
- A .NET 8 or newer project
- You like your life

**No if you want:**

- A color, three-color, or grayscale e-paper panel — this driver is B/W only
- To run the driver on Windows or macOS as the target — it compiles anywhere, but `GpioController` only opens `/dev/gpiochip*` on Linux
- A NuGet package — there isn't one, you copy the source file in

## Why use this

- **One source file.** Copy [`Epd4in26.cs`](src/Epd4in26/Epd4in26.cs) into your project; no NuGet package to track.
- **Automatic refresh routing.** You call `Render`; the driver chooses full vs partial based on what actually changed.
- **Panel protection built in.** The two refresh-cadence rules from the datasheet (180 s between full refreshes; 5-partial ghosting budget) are enforced in code — you can't accidentally damage your panel by spamming `Render`.
- **Reliable multi-region partial refreshes.** A common bug in SSD1677 drivers is that updating region A, then region B, erases A. This driver works around it ([details](PROTOCOL.md#the-setwindow-blanking-gotcha)).
- **Fail-fast on stuck panels.** Disconnected wires or wedged controllers raise a `TimeoutException` instead of silently rendering garbage.
- **Hardware-tested.** Nine human-verified test scenarios catch regressions before they hit your panel — see [VISUAL.md](tests/Epd4in26.Hardware/VISUAL.md).

## Table of contents

- [Install](#install)
- [Use it](#use-it)
- [Hardware setup](#hardware-setup)
- [Panel safety](#panel-safety)
- [API reference](#api-reference)
- [Tuning](#tuning)
- [Hardware test suite](#hardware-test-suite)
- [Deploy to a Raspberry Pi](#deploy-to-a-raspberry-pi)
- [Troubleshooting](#troubleshooting)
- [Project layout](#project-layout)
- [Contributing](#contributing)
- [License](#license)

## Install

Copy [`src/Epd4in26/Epd4in26.cs`](src/Epd4in26/Epd4in26.cs) into your project and add
its two upstream dependencies:

```xml
<PackageReference Include="System.Device.Gpio"   Version="3.2.0" />
<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />
```

One source file, two NuGet refs. Targets `net8.0` and newer.

## Use it

### Render an image file

```csharp
using var epd = new Epd4in26();
epd.Init();
epd.Render(Image.Load<Rgba32>("dashboard.png"));
epd.Sleep();
```

The image must be exactly 800 × 480 pixels. Any format ImageSharp supports works
(PNG, JPEG, BMP, GIF, TIFF, WebP, TGA, PBM, QOI).

### Render an in-memory image

If you're drawing dynamically with ImageSharp:

```csharp
using var img = new Image<Rgba32>(800, 480, Color.White);
img.Mutate(ctx => ctx.DrawText("Hello, e-paper", font, Color.Black, new PointF(20, 20)));
epd.Render(img);
```

> **Tip:** disable anti-aliasing when drawing for a 1-bit panel —
> `ctx.SetGraphicsOptions(g => g.Antialias = false)`. AA edges threshold poorly and
> end up as grey speckle.

### Inspect what `Render` did

```csharp
var result = epd.Render(img);
Console.WriteLine(result.Action);   // None, Partial, or Full
```

A typical session prints:

```
Full     ← first frame (no cache yet)
None     ← identical frame, nothing sent to the panel
Partial  ← small overlay change, just the dirty region refreshed
Full     ← big change, the whole panel cycled
```

See [API reference → RenderResult](#renderresult) for all the fields.

### Don't block when busy

`Render` is synchronous and not thread-safe. If you want concurrent callers to drop
their frame rather than queue, use `TryRender`:

```csharp
if (!epd.TryRender(img))
    // another refresh is already in progress; this frame is dropped
```

## Hardware setup

### Pinout

Wire the HAT to a Raspberry Pi using the standard 40-pin header. The driver's
constructor defaults match this wiring; override any pin if your setup differs.

| HAT pin | Pi pin (BCM) | Function |
|---------|--------------|----------|
| `VCC`   | 3.3 V          | Logic supply |
| `GND`   | GND            | Ground |
| `DIN`   | GPIO 10 (MOSI) | SPI data in |
| `CLK`   | GPIO 11 (SCLK) | SPI clock |
| `CS`    | GPIO 8         | Chip select |
| `DC`    | GPIO 25        | Data / command select |
| `RST`   | GPIO 17        | Hardware reset |
| `BUSY`  | GPIO 24        | Controller-busy signal |
| `PWR`   | GPIO 18        | Panel power gate (newer HATs only — must be HIGH) |

Enable SPI on the Pi if it isn't already:

```sh
sudo raspi-config   # → Interface Options → SPI → Enable
```

### SPI buffer size

The driver pushes the entire 48,000-byte framebuffer in one SPI transfer so the
panel sees a contiguous frame and doesn't tear across writes. Linux's default
`spidev` buffer is 4 KiB, which is too small — `Render` will fail with a partial
write. Raise the limit by appending `spidev.bufsiz=65536` to
`/boot/firmware/cmdline.txt` (or `/boot/cmdline.txt` on older Pi OS) and rebooting:

```sh
sudo sed -i 's/$/ spidev.bufsiz=65536/' /boot/firmware/cmdline.txt
sudo reboot
cat /sys/module/spidev/parameters/bufsiz    # expected: 65536
```

### Panel specs

| Spec | Value |
|------|-------|
| Resolution | 800 × 480, 1 bpp B/W |
| Active area | 92.8 × 55.8 mm (219 PPI) |
| Controller | SSD1677 |
| Operating temperature | 0 – 50 °C |
| Lifetime | ~1,000,000 refreshes / 5 years |
| Sleep current | ~2 µA (deep sleep) |

Datasheets are committed under [`resources/datasheets/`](resources/datasheets/).
The wire-level protocol (SPI parameters, BUSY polarity, command/data framing) is in
[PROTOCOL.md](PROTOCOL.md#hardware-interface).

## Panel safety

E-paper panels can be **permanently damaged by software** — the failure modes below
are unrepairable. Read this before changing display code.

> **The driver enforces the first two rules in code.** The rest are your
> responsibility — the driver can't observe them.

### Rules the driver enforces

1. **Minimum 180 seconds between full refreshes.** Back-to-back full refreshes destroy
   the panel. The driver blocks `Clear()` calls and defers `Render`'s full-refresh
   promotions until the cooldown elapses. The clock is preserved across `Init()` so
   you can't bypass it by reinitialising.
2. **At most 5 partial refreshes between full refreshes.** Beyond ~5 partials,
   residual ghosting accumulates and can damage the screen. `Render` auto-promotes
   to a full refresh once the budget is spent.

### Rules you must follow yourself

3. **Do at least one full refresh every 24 hours.** Otherwise long-term ghosting and
   image sticking set in. Schedule one in your app loop.
4. **Always sleep or power off after a refresh.** Leaving the panel powered up
   between updates burns out the high-voltage drive rail — vendor-documented
   unrepairable damage. Call `Sleep()` when you're done.
5. **Re-initialise after deep sleep.** Once you call `Sleep()`, the panel ignores
   commands until you call `Init()` again.
6. **Ship and store the panel showing a white image.** Avoids bake-in. Call
   `Clear()` before shutdown.

### Refresh timings (at 23 °C)

| Mode | Time | Used by `Render` when… |
|------|------|------------------------|
| **Full** | ~4 s | First call after `Init`, big change (≥ 50 % of tiles), or ghosting budget spent |
| **Fast** | ~1 s | You opt in via `Init(RefreshMode.Fast)`; replaces Normal as the full-refresh waveform |
| **Partial** | ~0.76 s | Cache hit + small change |

Fast mode clears less ghosting per cycle — pair it with periodic Normal refreshes if
you use it.

## API reference

### Constructor

```csharp
new Epd4in26();                                  // defaults: pins as in Hardware setup, 180 s safety floor
new Epd4in26(minFullRefreshInterval: TimeSpan.FromSeconds(60));  // override safety knobs
```

Full constructor signature with all overrides:

```csharp
public Epd4in26(
    int spiBus = 0, int spiChipSelect = 0, int spiClockHz = 20_000_000,
    int resetPin = 17, int dcPin = 25, int csPin = 8, int busyPin = 24, int powerPin = 18,
    TimeSpan? minFullRefreshInterval = null,        // default: 180 s
    int maxPartialRefreshes = 5);
```

### Methods you'll use

| Method | What it does |
|--------|--------------|
| `Init(RefreshMode mode = Normal)` | Power-up sequence. Call after construction and after `Sleep`. Preserves the 180 s safety clock. |
| `Render(Image<Rgba32> image, byte threshold = 128)` | Main entry point. Picks full / partial / no-op automatically. |
| `Render(byte[] frame)` | Same, but takes an already-packed 1 bpp frame. |
| `TryRender(Image<Rgba32> image, byte threshold = 128)` | Returns `false` immediately if another refresh is in progress. |
| `Clear()` | Full refresh to white. Counts against the 180 s safety floor. |
| `Sleep()` | Deep sleep (~2 µA). Call `Init` to wake. |
| `Dispose()` | Release GPIO and SPI handles. Use `using var epd = ...`. |
| `static PackFromFile(string path, byte threshold = 128)` | Load an image file and pre-pack it to 1 bpp, e.g. for caching across renders. |
| `static PackFromImage(Image<Rgba32> image, byte threshold = 128)` | Same, from an in-memory image. |

### `RenderResult`

`Render` returns a struct describing what it did:

| Field | Meaning |
|-------|---------|
| `Action` | `None` (nothing sent), `Partial` (small region refreshed), or `Full` (whole panel cycled) |
| `DirtyTiles` / `TotalTiles` | How many 80 × 40 tiles changed out of 120 total |
| `PartialChunks` | How many partial-refresh chunks the driver issued (currently always 0 or 1) |
| `WaitedForCooldown` | How long the driver blocked waiting for the 180 s safety floor; > 0 only when a `Full` was deferred |
| `ChangedRatio` | `DirtyTiles / TotalTiles` as a convenience |

### Observable state

| Property | Meaning |
|----------|---------|
| `MinFullRefreshInterval` | The configured safety floor (set via constructor). |
| `MaxPartialRefreshes` | The configured ghosting budget. |
| `TimeUntilNextFullRefreshAllowed` | `Zero` if a full refresh is allowed now; otherwise the remaining cooldown. |

### Constants

| Constant | Value |
|----------|-------|
| `Epd4in26.Width` | `800` |
| `Epd4in26.Height` | `480` |
| `Epd4in26.FrameSize` | `48000` (bytes in a packed 1 bpp frame) |

Frame-format details (bit packing, alignment requirements) are in
[PROTOCOL.md](PROTOCOL.md#frame-format) — most callers don't need them because
`Render(image)` handles packing internally.

## Tuning

The two safety knobs are constructor parameters:

| Parameter | Default | When to lower it |
|-----------|---------|------------------|
| `minFullRefreshInterval` | `180 s` | **Never on a production panel** — risks permanent damage. Fine for development against a panel you can replace. |
| `maxPartialRefreshes` | `5` | Lower it for a more conservative ghost budget; raise it carefully if your imagery doesn't ghost in your environment. |

### Development iteration

To make `Render` willing to promote to a full refresh more often while you're testing:

```csharp
// Dangerous on a production panel; fine on a test rig.
using var epd = new Epd4in26(minFullRefreshInterval: TimeSpan.FromSeconds(5));
```

The hardware test runner exposes this as `EPD_TEST_INTERVAL` so the full suite can
complete in a few minutes rather than ~30.

## Build

```sh
dotnet build                                  # both projects
dotnet build src/Epd4in26                     # just the library
dotnet build tests/Epd4in26.Hardware          # just the test runner (binary: EpdTest)
```

`Directory.Build.props` pins the target framework and turns warnings into errors for
both projects. Bump the TFM once there and the deploy script's publish path follows
automatically.

## Hardware test suite

[`tests/Epd4in26.Hardware/`](tests/Epd4in26.Hardware/) is a console runner that drives
the panel through nine scripted scenarios. Each test pairs an assertion on
`RenderResult` (catches code regressions) with a printed checklist and a `y / n / skip`
prompt (catches what the SPI bus can't see — ghosting, residue, dead pixels). The full
follow-along is in
[`tests/Epd4in26.Hardware/VISUAL.md`](tests/Epd4in26.Hardware/VISUAL.md).

```sh
EpdTest list                  # show tests + summaries
EpdTest all                   # run all tests, prompt y/n after each
EpdTest partial-single        # run one test
EpdTest --no-prompt all       # CI / smoke: assertions only
EpdTest generate              # (re)render the test PNGs
EpdTest image ./pic.png       # ad-hoc: render a single image file
```

### Tests

| Name | What it checks |
|------|----------------|
| `clear` | `Clear()` produces a uniformly white panel |
| `baseline` | First render after `Init` draws a clean labelled grid |
| `noop` | Re-rendering the same frame doesn't touch the panel |
| `partial-single` | A one-tile change updates only that tile, with no residue |
| `partial-bbox` | Two corner-tile changes are coalesced into one bounding-box partial |
| `partial-two-locations` | Updating region B doesn't erase a previous update at region A |
| `ghost-budget` | Five back-to-back partials at the same tile leave no visible ghost |
| `auto-promote` | The 6th partial after a full budget promotes to a full refresh |
| `promote-large` | A change covering ≥ 50 % of tiles promotes to a full refresh |
| `sleep-wake` | `Sleep` → `Init` → `Render` brings the panel back cleanly |

Test PNGs are pre-rendered by
[`PatternGenerator.cs`](tests/Epd4in26.Hardware/PatternGenerator.cs) and committed
under [`resources/patterns/`](tests/Epd4in26.Hardware/resources/patterns/) so the
runner has no font or drawing-library dependencies at test time. Regenerate after
editing the generator: `EpdTest generate`. The bundled
[`Inter-Bold.ttf`](tests/Epd4in26.Hardware/resources/fonts/Inter-Bold.ttf)
(SIL OFL 1.1) ships alongside.

### Environment variables

| Variable | Default | Notes |
|----------|---------|-------|
| `EPD_TEST_INTERVAL` | `180` | `MinFullRefreshInterval` in seconds. Below 180 s the runner prints a warning. Lower it during development. |
| `NO_COLOR` | unset | Any value suppresses ANSI colours. |

## Deploy to a Raspberry Pi

[`deploy.sh`](deploy.sh) publishes the test runner self-contained, rsyncs it to a Pi,
and optionally runs the suite over SSH. Defaults target a Pi Zero 2 W
(`linux-arm64`, hostname `raspberrypi.local`, user `pi`); override via env vars.

```sh
./deploy.sh                              # build + rsync, don't run
./deploy.sh run                          # build + rsync + run all tests
./deploy.sh run partial-single           # run one test
./deploy.sh run --no-prompt all          # assertion-only smoke run
./deploy.sh run image ./pic.png          # ad-hoc image render
```

```sh
# common overrides
PI_HOST=epd.local PI_USER=christian RID=linux-arm ./deploy.sh run
EPD_TEST_INTERVAL=5 ./deploy.sh run
```

The publish is self-contained (~80 MB) so the Pi doesn't need a .NET runtime
installed. It does need SPI enabled (see [Hardware setup](#hardware-setup)).

### VS Code tasks

[`.vscode/tasks.json`](.vscode/tasks.json) exposes the common loops as Task Runner
entries:

- **build** — `dotnet build tests/Epd4in26.Hardware` (default build task)
- **deploy** — `./deploy.sh`
- **deploy + run all tests (visual prompts)** — `./deploy.sh run`
- **deploy + run all tests (--no-prompt)** — assertion-only mode
- **deploy + run test** — pick one from a dropdown
- **deploy + render image** — prompts for an image path
- **regenerate test PNGs (local)** — runs the generator on your laptop, no Pi roundtrip

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `TimeoutException: BUSY pin still high after 50000 ms` | Panel disconnected, `PWR` (GPIO 18) low on newer HATs, or controller hung. | Check wiring, power-cycle the panel. |
| Garbled / scrambled pixels | SPI mode or clock wrong, ribbon cable too long, or the wrong runtime ID was published. | Confirm SPI Mode 0; lower `spiClockHz` to 2 MHz in the constructor; check `RID=linux-arm` vs `linux-arm64`. |
| `Render` fails or panel shows a blank / partial frame | Linux `spidev.bufsiz` default of 4 KiB is smaller than the 48 KB framebuffer. | Raise the kernel buffer; see [Hardware setup → SPI buffer size](#spi-buffer-size). |
| Earlier partial update erased when you refresh elsewhere | You're on a driver that doesn't apply the SetWindow-blanking workaround. | Update to a version that includes the `partial-two-locations` test. [Details in PROTOCOL.md.](PROTOCOL.md#the-setwindow-blanking-gotcha) |
| Visible ghosting that doesn't clear up | More than ~5 partials between full refreshes. | Lower `maxPartialRefreshes`, or call `Clear()` more often. |
| `dotnet publish` succeeds, but the binary crashes on the Pi | Published with the wrong runtime ID. | 32-bit Raspberry Pi OS → `RID=linux-arm`; 64-bit → `RID=linux-arm64`. |
| Image looks inverted (black / white swapped) | Threshold or bit-packing inverted somewhere in your pipeline. | `PackFromFile` / `PackFromImage` produce the correct polarity by default (bit 1 = white). |
| `Render` returned `Partial` when you expected `Full` | The 180 s safety floor was still active; the driver fell back to a partial instead of blocking the UI thread. | Wait `epd.TimeUntilNextFullRefreshAllowed` and retry. |

## License

MIT — see [LICENSE](LICENSE). Mirrors and improves upon the Waveshare reference at
[github.com/waveshareteam/e-Paper](https://github.com/waveshareteam/e-Paper)
(`RaspberryPi_JetsonNano/c/lib/e-Paper`).
