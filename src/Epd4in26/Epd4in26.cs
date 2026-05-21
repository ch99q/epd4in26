// ---------------------------------------------------------------------------------
// Single-file driver for the Waveshare 4.26" e-Paper HAT (800 x 480, B/W, SSD1677).
//
// Drop this file into any .NET 8+ project; add these NuGet refs:
//   <PackageReference Include="System.Device.Gpio"   Version="3.2.0" />
//   <PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />
//
// Pinout (Raspberry Pi 40-pin header, BCM numbering):
//   VCC -> 3.3V       DIN (MOSI) -> GPIO 10
//   GND -> GND        CLK (SCLK) -> GPIO 11
//   CS  -> GPIO  8    DC         -> GPIO 25
//   RST -> GPIO 17    BUSY       -> GPIO 24
//   PWR -> GPIO 18    (newer HATs gate panel VCC through this pin -- must be HIGH)
//
// Typical use:
//   using var epd = new Epd4in26();
//   epd.Init();
//   epd.Render(Epd4in26.PackFromFile("a.png"));         // first call: full refresh
//   epd.Render(myImage);                                // ImageSharp Image<Rgba32> overload
//   epd.Sleep();
//
// Render() decides full-vs-partial automatically, caches the previous frame, and
// honors the 180s full-refresh limiter -- back-to-back full refreshes can permanently
// damage the panel.
//
// License: MIT. Mirrors the Waveshare reference at
// https://github.com/waveshareteam/e-Paper (RaspberryPi_JetsonNano/c/lib/e-Paper).
// ---------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

[assembly: InternalsVisibleTo("EpdTest")]

namespace Waveshare.Standalone
{
    public enum RenderAction
    {
        /// <summary>The new frame matched the cache; nothing was sent to the panel.</summary>
        None,
        /// <summary>A full refresh ran (first frame, large change, or ghosting budget overflow).</summary>
        Full,
        /// <summary>One or more partial-refresh chunks were sent for the dirty regions.</summary>
        Partial,
    }

    public enum RefreshMode
    {
        /// <summary>Full ghost-clearing waveform (0xF7, ~4 s @ 23°C, datasheet §7.1). Default.</summary>
        Normal,
        /// <summary>Fast waveform (0xC7, ~1 s @ 23°C, datasheet §7.1). Less ghost-clearing — pair with periodic Normal refreshes.</summary>
        Fast,
    }

    /// <summary>Outcome of a <see cref="Epd4in26.Render(byte[])"/> call.</summary>
    public readonly record struct RenderResult(
        RenderAction Action,
        int DirtyTiles,
        int TotalTiles,
        int PartialChunks,
        TimeSpan WaitedForCooldown)
    {
        /// <summary>Fraction of tiles that differed (0 to 1).</summary>
        public double ChangedRatio => TotalTiles > 0 ? (double)DirtyTiles / TotalTiles : 0;
    }

    public sealed class Epd4in26 : IDisposable
    {
        public const int Width      = 800;
        public const int Height     = 480;
        public const int FrameSize  = Width * Height / 8; // 1 bpp packed MSB-first

        // Dirty-region tile grid. 80x40 -> 10x12 grid, 120 tiles total. Tuned for typical
        // UI updates (clocks, status overlays) where dirty regions cluster.
        private const int DiffTileWidth      = 80;
        private const int DiffTileHeight     = 40;
        private const int TilesX             = Width  / DiffTileWidth;   // 10
        private const int TilesY             = Height / DiffTileHeight;  // 12
        private const int TotalTiles         = TilesX * TilesY;          // 120
        private const int FrameBytesPerRow   = Width / 8;                // 100
        private const int TileBytesPerRow    = DiffTileWidth / 8;        // 10
        private const double FullRefreshChangeThreshold = 0.5;           // promote partial -> full

        private readonly int _resetPin;
        private readonly int _dcPin;
        private readonly int _csPin;
        private readonly int _busyPin;
        private readonly int _powerPin;
        private readonly GpioController _gpio;
        private readonly SpiDevice _spi;
        private bool _disposed;

        private DateTimeOffset? _lastFullRefresh;
        private RefreshMode _refreshMode = RefreshMode.Normal;
        private int _partialRefreshesSinceLastFull;
        private bool _baseImageLoaded;
        private byte[]? _cache;

        // Drop-on-busy flag for TryRender. 0 = idle, 1 = a refresh is in
        // progress on another thread. Atomic test-and-set via Interlocked
        // means a concurrent caller is rejected rather than corrupting the
        // SPI bus or the partial-refresh bookkeeping.
        private int _renderInFlight;

        /// <summary>
        /// Minimum delay enforced between any two full refreshes. Waveshare's documented floor is
        /// 180s; lowering this risks permanent panel damage. Set via constructor parameter.
        /// </summary>
        public TimeSpan MinFullRefreshInterval { get; }

        /// <summary>
        /// Ceiling on consecutive partial refreshes before <see cref="Render(byte[])"/> auto-promotes to a
        /// full refresh to clear ghosting. Default 5 per Waveshare wiki. Set via constructor.
        /// </summary>
        public int MaxPartialRefreshes { get; internal set; }

        /// <summary>Time the caller must wait before the next full refresh is permitted. Zero if ready.</summary>
        public TimeSpan TimeUntilNextFullRefreshAllowed
        {
            get
            {
                if (_lastFullRefresh is null) return TimeSpan.Zero;
                var remaining = MinFullRefreshInterval - (DateTimeOffset.UtcNow - _lastFullRefresh.Value);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public Epd4in26(
            int spiBus = 0, int spiChipSelect = 0, int spiClockHz = 20_000_000,
            int resetPin = 17, int dcPin = 25, int csPin = 8, int busyPin = 24, int powerPin = 18,
            TimeSpan? minFullRefreshInterval = null,
            int maxPartialRefreshes = 5)
        {
            _resetPin = resetPin;
            _dcPin    = dcPin;
            _csPin    = csPin;
            _busyPin  = busyPin;
            _powerPin = powerPin;
            MinFullRefreshInterval = minFullRefreshInterval ?? TimeSpan.FromSeconds(180);
            MaxPartialRefreshes    = maxPartialRefreshes;

            _gpio = new GpioController();
            try
            {
                _gpio.OpenPin(_resetPin, PinMode.Output);
                _gpio.OpenPin(_dcPin,    PinMode.Output);
                _gpio.OpenPin(_csPin,    PinMode.Output);
                _gpio.OpenPin(_busyPin,  PinMode.Input);
                _gpio.OpenPin(_powerPin, PinMode.Output);

                _gpio.Write(_csPin,    PinValue.High);
                _gpio.Write(_powerPin, PinValue.High); // newer HATs gate panel VCC through this pin

                _spi = SpiDevice.Create(new SpiConnectionSettings(spiBus, spiChipSelect)
                {
                    ClockFrequency = spiClockHz,
                    Mode = SpiMode.Mode0
                });
            }
            catch
            {
                // SpiDevice.Create can throw if the SPI bus isn't available; release the GPIO
                // pins we already opened so the OS doesn't keep them claimed.
                _gpio.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Run the power-up / reset / register-setup sequence. <paramref name="mode"/> selects the
        /// refresh waveform that subsequent full refreshes will use: <see cref="RefreshMode.Normal"/>
        /// (0xF7, ~4 s) or <see cref="RefreshMode.Fast"/> (0xC7, ~1 s, less ghost-clearing).
        /// Switching modes requires a re-Init.
        /// </summary>
        public void Init(RefreshMode mode = RefreshMode.Normal)
        {
            Reset();
            Thread.Sleep(100);

            WaitUntilReady();
            SendCommand(0x12); // SWRESET
            WaitUntilReady();

            SendCommand(0x18); SendData(0x80);                               // temp sensor: internal
            SendCommand(0x0C); SendData(0xAE, 0xC7, 0xC3, 0xC0, 0x80);       // soft start
            SendCommand(0x01); SendData((byte)((Height - 1) & 0xFF),         // driver output control
                                        (byte)(((Height - 1) >> 8) & 0xFF),
                                        0x02);
            SendCommand(0x3C); SendData(0x01);                               // border waveform
            SendCommand(0x11); SendData(0x01);                               // data entry: X+, Y-

            SetWindow(0, Height - 1, Width - 1, 0);
            SetCursor(0, 0);
            WaitUntilReady();

            if (mode == RefreshMode.Fast)
            {
                // Load the fast LUT from OTP into the controller's working RAM. 0x5A is a "fake
                // high temperature" hint that tells the chip to skip thermal-compensation warm-up.
                SendCommand(0x1A); SendData(0x5A);
                SendCommand(0x22); SendData(0x91);
                SendCommand(0x20);
                WaitUntilReady();
            }
            _refreshMode = mode;

            // Cooldown clock is intentionally preserved across Init -- otherwise callers could
            // bypass the limiter by reinitializing in a loop. Cache and partial-base flag DO
            // reset because the controller is freshly initialized.
            _partialRefreshesSinceLastFull = 0;
            _baseImageLoaded = false;
            _cache = null;
        }

        /// <summary>Clear the display to white. Counts as a full refresh.</summary>
        public void Clear()
        {
            EnforceFullRefreshInterval();
            ResetFullPanelWindow();

            SendCommand(0x24); WriteFullFrame(0xFF);
            SendCommand(0x26); WriteFullFrame(0xFF);
            Refresh();

            EnsureCache();
            Array.Fill(_cache!, (byte)0xFF);
            MarkFullRefreshComplete(baseImageLoaded: true);
        }

        /// <summary>
        /// Show <paramref name="frame"/> using the cheapest correct path: full refresh on the
        /// first call (or after Init), no-op when nothing changed, otherwise partial chunks for
        /// the coalesced dirty-tile row spans -- promoted to a full refresh when too much changed
        /// or the partial-ghosting budget would overflow. Subject to <see cref="MinFullRefreshInterval"/>.
        /// </summary>
        public RenderResult Render(byte[] frame)
        {
            ValidateFullFrame(frame);

            var cooldownBefore = TimeUntilNextFullRefreshAllowed;

            // No cache, or RAM 0x26 stale -- only DisplayBase can correctly seed the panel for
            // partial refreshes.
            if (_cache is null || !_baseImageLoaded)
            {
                DisplayBase(frame);
                return new RenderResult(RenderAction.Full, TotalTiles, TotalTiles, 0, cooldownBefore);
            }

            var dirty = new bool[TotalTiles];
            int dirtyCount = 0;
            for (int ty = 0; ty < TilesY; ty++)
            {
                for (int tx = 0; tx < TilesX; tx++)
                {
                    for (int row = 0; row < DiffTileHeight; row++)
                    {
                        int rowOff = (ty * DiffTileHeight + row) * FrameBytesPerRow + tx * TileBytesPerRow;
                        if (!_cache.AsSpan(rowOff, TileBytesPerRow).SequenceEqual(frame.AsSpan(rowOff, TileBytesPerRow)))
                        {
                            dirty[ty * TilesX + tx] = true;
                            dirtyCount++;
                            break;
                        }
                    }
                }
            }

            if (dirtyCount == 0)
                return new RenderResult(RenderAction.None, 0, TotalTiles, 0, TimeSpan.Zero);

            var rects = CoalesceDirtyTiles(dirty);
            double ratio = (double)dirtyCount / TotalTiles;

            // We'd prefer a full refresh when the change is large or the partial budget is
            // spent, but only do it when the cooldown actually allows it — otherwise
            // DisplayBase would Thread.Sleep until MinFullRefreshInterval elapses and freeze
            // the UI. If a full is wanted but blocked, fall through and do partials anyway;
            // the next render after the cooldown will pick up the full refresh.
            bool wantFull =
                ratio >= FullRefreshChangeThreshold ||
                _partialRefreshesSinceLastFull + rects.Count > MaxPartialRefreshes;
            bool canFull = cooldownBefore == TimeSpan.Zero;

            if (wantFull && canFull)
            {
                DisplayBase(frame);
                return new RenderResult(RenderAction.Full, dirtyCount, TotalTiles, 0, cooldownBefore);
            }

            foreach (var rect in rects)
                DisplayPartial(frame, rect.X, rect.Y, rect.W, rect.H);
            return new RenderResult(RenderAction.Partial, dirtyCount, TotalTiles, rects.Count, TimeSpan.Zero);
        }

        /// <summary>
        /// Convert an ImageSharp <see cref="Image{Rgba32}"/> to a packed 1bpp frame and render it.
        /// Same threshold / luma rules as <see cref="PackFromImage"/>. Convenience for callers that
        /// already build frames with ImageSharp drawing extensions.
        /// </summary>
        public RenderResult Render(Image<Rgba32> image, byte threshold = 128) =>
            Render(PackFromImage(image, threshold));

        /// <summary>
        /// Drop-on-busy variant of <see cref="Render(Image{Rgba32}, byte)"/>. Returns true once the
        /// refresh has completed, or false immediately if another thread is already inside a
        /// Render/Clear/TryRender call. The e-paper SPI transaction and the driver's dirty-tile
        /// bookkeeping are not reentrant, so callers that race must drop rather than wait.
        /// </summary>
        public bool TryRender(Image<Rgba32> image, byte threshold = 128)
        {
            if (Interlocked.Exchange(ref _renderInFlight, 1) == 1) return false;
            try
            {
                Render(image, threshold);
                return true;
            }
            finally { Interlocked.Exchange(ref _renderInFlight, 0); }
        }

        /// <summary>
        /// Drop-on-busy variant of <see cref="Clear"/>. Returns false if another refresh is
        /// already in progress.
        /// </summary>
        public bool TryClear()
        {
            if (Interlocked.Exchange(ref _renderInFlight, 1) == 1) return false;
            try
            {
                Clear();
                return true;
            }
            finally { Interlocked.Exchange(ref _renderInFlight, 0); }
        }

        /// <summary>Put the controller into deep sleep (~2 µA). Call <see cref="Init"/> to wake.</summary>
        public void Sleep()
        {
            SendCommand(0x10);
            SendData(0x03);
            Thread.Sleep(100);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _gpio.Write(_csPin,    PinValue.Low);
            _gpio.Write(_dcPin,    PinValue.Low);
            _gpio.Write(_resetPin, PinValue.Low);
            _gpio.Write(_powerPin, PinValue.Low);
            _gpio.Dispose();
            _spi.Dispose();
        }

        // ----- Static image helpers -----

        /// <summary>
        /// Load any ImageSharp-readable file (PNG/JPEG/BMP/GIF/TIFF/WebP/TGA/PBM/QOI) and pack it
        /// to 1bpp. Image must be exactly <see cref="Width"/>x<see cref="Height"/>.
        /// </summary>
        public static byte[] PackFromFile(string path, byte threshold = 128)
        {
            using var image = Image.Load<Rgba32>(path);
            return PackFromImage(image, threshold);
        }

        /// <summary>
        /// Pack an in-memory <see cref="Image{Rgba32}"/> to 1bpp MSB-first (bit 1 = white).
        /// Image must be exactly <see cref="Width"/>x<see cref="Height"/>. Per-pixel BT.601 luma is
        /// compared against <paramref name="threshold"/>. Disable ImageSharp anti-aliasing
        /// (<c>GraphicsOptions.Antialias = false</c>) when drawing — AA grays threshold poorly
        /// on a 1-bit panel.
        /// </summary>
        public static byte[] PackFromImage(Image<Rgba32> image, byte threshold = 128)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width != Width || image.Height != Height)
                throw new ArgumentException(
                    $"Image must be exactly {Width}x{Height} (got {image.Width}x{image.Height}).",
                    nameof(image));

            var frame = new byte[FrameSize];
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int xByte = 0; xByte < FrameBytesPerRow; xByte++)
                    {
                        byte packed = 0;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            var p = row[xByte * 8 + bit];
                            int lum = (p.R * 299 + p.G * 587 + p.B * 114 + 500) / 1000; // ITU-R BT.601 luma
                            if (lum >= threshold) packed |= (byte)(0x80 >> bit); // 1 = white
                        }
                        frame[y * FrameBytesPerRow + xByte] = packed;
                    }
                }
            });
            return frame;
        }

        // ----- Internal (test-visible): forced full refresh + base-image seed -----

        /// <summary>
        /// Push a full frame and trigger a full refresh, priming both RAM banks so the next
        /// <see cref="Render(byte[])"/> can do partial updates. Used by tests that need a known clean
        /// baseline regardless of carried-over driver state.
        /// </summary>
        internal void DisplayBase(byte[] frame)
        {
            ValidateFullFrame(frame);
            EnforceFullRefreshInterval();
            ResetFullPanelWindow();

            SendCommand(0x24); SendData(frame);
            SendCommand(0x26); SendData(frame);
            Refresh();

            UpdateCacheFrom(frame);
            MarkFullRefreshComplete(baseImageLoaded: true);
        }

        // Restore the RAM window and cursor that Init() set up, so a full refresh
        // covers the whole panel. DisplayPartial narrows the window to its region
        // and the chip keeps that state across commands until we reset it.
        private void ResetFullPanelWindow()
        {
            SetWindow(0, Height - 1, Width - 1, 0);
            SetCursor(0, 0);
        }

        // ----- Private: partial protocol, limiter, cache, validation, tile coalescing -----

        private void DisplayPartial(byte[] targetFrame, int x, int y, int width, int height)
        {
            // Re-arm temp sensor and float the border for the partial waveform.
            SendCommand(0x18); SendData(0x80);
            SendCommand(0x3C); SendData(0x80);

            // Push the WHOLE target frame to 0x24 with the addressing window covering the
            // entire panel, even though only the (x, y, width, height) rect has actually
            // changed vs the cache. Two reasons we don't do a regional 0x24 write:
            //
            //  1. The SSD1677 partial waveform treats the active SetWindow rect as the
            //     "drive area" and pulls pixels OUTSIDE the window back toward their 0x26
            //     value. With a regional window, any region driven by a PREVIOUS partial
            //     gets blanked when the next partial sets its window somewhere else --
            //     "first partial disappears" the moment a second partial at a different
            //     location runs. Reproduces in the `partial-two-locations` test.
            //
            //  2. The chip-wide compare against 0x26 (which still holds the DisplayBase
            //     baseline) gives the partial LUT exactly the diff we want: pixels where
            //     targetFrame matches the baseline pick up the diagonal "stay" LUT cell
            //     (no drive); pixels where it differs get the transition LUT.
            //
            // This matches the Waveshare reference pattern -- 0x26 is only ever written by
            // DisplayBase, and every partial sends a full-frame 0x24 with the full window.
            // We accept the panel-stress trade-off: regions touched by prior partials get
            // re-driven each subsequent partial (since they still differ from 0x26-baseline),
            // which is what MaxPartialRefreshes is there to bound.
            ResetFullPanelWindow();
            SendCommand(0x24);
            SendData(targetFrame);

            SendCommand(0x22); SendData(0xFF); // partial-refresh waveform (vs 0xF7 full / 0xC7 fast)
            SendCommand(0x20);
            WaitUntilReady();

            UpdateCacheRegion(targetFrame, x, y, width, height);
            _partialRefreshesSinceLastFull++;
        }

        private void EnforceFullRefreshInterval()
        {
            var remaining = TimeUntilNextFullRefreshAllowed;
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
        }

        private void MarkFullRefreshComplete(bool baseImageLoaded)
        {
            _lastFullRefresh = DateTimeOffset.UtcNow;
            _partialRefreshesSinceLastFull = 0;
            _baseImageLoaded = baseImageLoaded;
        }

        private static void ValidateFullFrame(byte[] frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Length != FrameSize)
                throw new ArgumentException($"Frame must be exactly {FrameSize} bytes (got {frame.Length}).", nameof(frame));
        }

        private readonly record struct TileRect(int X, int Y, int W, int H);

        private static List<TileRect> CoalesceDirtyTiles(bool[] dirty)
        {
            // One bounding-box rect over every dirty tile. Each separate rect counts toward
            // MaxPartialRefreshes and adds its own ghosting boundary, so we'd rather refresh
            // a single larger region than several smaller ones. Worst case for unrelated
            // dirty patches (clock at top + steps in centre) the box covers the whole panel
            // and Render() promotes it to a full refresh — which is what we want anyway.
            int minTx = TilesX, minTy = TilesY, maxTx = -1, maxTy = -1;
            for (int ty = 0; ty < TilesY; ty++)
            {
                for (int tx = 0; tx < TilesX; tx++)
                {
                    if (!dirty[ty * TilesX + tx]) continue;
                    if (tx < minTx) minTx = tx;
                    if (ty < minTy) minTy = ty;
                    if (tx > maxTx) maxTx = tx;
                    if (ty > maxTy) maxTy = ty;
                }
            }
            if (maxTx < 0) return new List<TileRect>();
            return new List<TileRect>
            {
                new TileRect(
                    X: minTx * DiffTileWidth,
                    Y: minTy * DiffTileHeight,
                    W: (maxTx - minTx + 1) * DiffTileWidth,
                    H: (maxTy - minTy + 1) * DiffTileHeight)
            };
        }

        private void EnsureCache()
        {
            if (_cache is null || _cache.Length != FrameSize)
                _cache = new byte[FrameSize];
        }

        private void UpdateCacheFrom(byte[] frame)
        {
            EnsureCache();
            Buffer.BlockCopy(frame, 0, _cache!, 0, FrameSize);
        }

        private void UpdateCacheRegion(byte[] fullFrame, int x, int y, int width, int height)
        {
            if (_cache is null) return;
            int bytesPerRow = width / 8;
            int xByte = x / 8;
            for (int row = 0; row < height; row++)
            {
                int offset = (y + row) * FrameBytesPerRow + xByte;
                Buffer.BlockCopy(fullFrame, offset, _cache, offset, bytesPerRow);
            }
        }

        // ----- Low-level protocol -----

        private void Reset()
        {
            _gpio.Write(_resetPin, PinValue.High); Thread.Sleep(20);
            _gpio.Write(_resetPin, PinValue.Low);  Thread.Sleep(2);
            _gpio.Write(_resetPin, PinValue.High); Thread.Sleep(20);
        }

        private void SendCommand(byte cmd)
        {
            _gpio.Write(_dcPin, PinValue.Low);
            _gpio.Write(_csPin, PinValue.Low);
            _spi.WriteByte(cmd);
            _gpio.Write(_csPin, PinValue.High);
        }

        private void SendData(byte b)
        {
            _gpio.Write(_dcPin, PinValue.High);
            _gpio.Write(_csPin, PinValue.Low);
            _spi.WriteByte(b);
            _gpio.Write(_csPin, PinValue.High);
        }

        private void SendData(params byte[] data)
        {
            _gpio.Write(_dcPin, PinValue.High);
            _gpio.Write(_csPin, PinValue.Low);
            _spi.Write(data);
            _gpio.Write(_csPin, PinValue.High);
        }

        private void WriteFullFrame(byte fill)
        {
            var line = new byte[FrameBytesPerRow];
            Array.Fill(line, fill);
            _gpio.Write(_dcPin, PinValue.High);
            _gpio.Write(_csPin, PinValue.Low);
            for (int y = 0; y < Height; y++) _spi.Write(line);
            _gpio.Write(_csPin, PinValue.High);
        }

        private void SetWindow(int xStart, int yStart, int xEnd, int yEnd)
        {
            SendCommand(0x44);
            SendData((byte)(xStart & 0xFF), (byte)((xStart >> 8) & 0x03),
                     (byte)(xEnd   & 0xFF), (byte)((xEnd   >> 8) & 0x03));
            SendCommand(0x45);
            SendData((byte)(yStart & 0xFF), (byte)((yStart >> 8) & 0x03),
                     (byte)(yEnd   & 0xFF), (byte)((yEnd   >> 8) & 0x03));
        }

        private void SetCursor(int x, int y)
        {
            SendCommand(0x4E); SendData((byte)(x & 0xFF), (byte)((x >> 8) & 0x03));
            SendCommand(0x4F); SendData((byte)(y & 0xFF), (byte)((y >> 8) & 0x03));
        }

        private void Refresh()
        {
            SendCommand(0x22); SendData(_refreshMode == RefreshMode.Fast ? (byte)0xC7 : (byte)0xF7);
            SendCommand(0x20);
            WaitUntilReady();
        }

        private void WaitUntilReady(int timeoutMs = 50_000)
        {
            // SSD1677 drives BUSY high while busy and low when ready -- opposite polarity to older
            // UC8xxx panels. Throws on timeout so a stuck panel surfaces as a clear failure.
            var sw = Stopwatch.StartNew();
            while (_gpio.Read(_busyPin) == PinValue.High)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException(
                        $"e-Paper BUSY pin still high after {timeoutMs} ms; the controller may be unresponsive or the panel disconnected.");
                Thread.Sleep(20);
            }
        }
    }
}
