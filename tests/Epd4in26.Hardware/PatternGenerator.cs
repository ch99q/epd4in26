using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Waveshare.Standalone;

namespace EpdTest.Hardware;

/// <summary>
/// Renders the 1 bpp test PNGs the hardware suite loads. Pure ImageSharp + SixLabors.Fonts;
/// no GPIO/SPI access — runs anywhere. Output is anti-aliased OFF so the PNGs are already
/// strictly black/white and survive the driver's threshold pack at load time without artifacts.
/// </summary>
public static class PatternGenerator
{
    public const int TileWidth  = 80;
    public const int TileHeight = 40;
    public const int Cols = Epd4in26.Width  / TileWidth;   // 10  -> A..J
    public const int Rows = Epd4in26.Height / TileHeight;  // 12  -> 1..12

    static readonly FontFamily _interBold = LoadFont("Inter-Bold.ttf");

    static FontFamily LoadFont(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "fonts", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Bundled font missing at {path}. Did the project build copy resources/fonts/?", path);
        var collection = new FontCollection();
        return collection.Add(path);
    }

    public static string TileLabel(int col, int row) => $"{(char)('A' + col)}{row + 1}";

    /// <summary>Render every pattern PNG into <paramref name="outputDir"/>.</summary>
    public static void GenerateAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        Save(outputDir, "baseline-grid.png",   BaselineGrid());
        Save(outputDir, "partial-single.png",  WithBigTile(col: 4, row: 5, "02"));
        Save(outputDir, "partial-bbox.png",    WithBigTiles((0, 0, "XX"), (9, 11, "YY")));
        // partial-two-locations: first frame changes B3, second frame ALSO changes I10.
        // The second render's diff is only I10, but the panel must still show B3 -- that
        // proves the post-drive mirror to RAM 0x26 is keeping the previous-frame reference
        // in sync with what's on the panel.
        Save(outputDir, "partial-loc-a.png",   WithBigTile(col: 1, row: 2, "AA"));
        Save(outputDir, "partial-loc-b.png",   WithBigTiles((1, 2, "AA"), (8, 9, "BB")));
        for (int n = 1; n <= 5; n++)
            Save(outputDir, $"ghost-step-{n}.png", WithBigTile(col: 4, row: 5, $"0{n}"));
        Save(outputDir, "auto-promote.png",    WithBigTile(col: 4, row: 5, "06"));
        Save(outputDir, "promote-large.png",   PromoteLarge());
    }

    static void Save(string dir, string name, Image<Rgba32> image)
    {
        using (image) image.SaveAsPng(Path.Combine(dir, name));
    }

    // ---- Pattern builders ------------------------------------------------------------------

    public static Image<Rgba32> BaselineGrid()
    {
        var img = NewWhiteCanvas();
        DrawTileGrid(img);
        return img;
    }

    public static Image<Rgba32> WithBigTile(int col, int row, string text)
    {
        var img = BaselineGrid();
        DrawBigInvertedTile(img, col, row, text);
        return img;
    }

    public static Image<Rgba32> WithBigTiles(params (int col, int row, string text)[] tiles)
    {
        var img = BaselineGrid();
        foreach (var (col, row, text) in tiles)
            DrawBigInvertedTile(img, col, row, text);
        return img;
    }

    /// <summary>
    /// Half the tiles inverted (alternate rows). Forces &gt; 50% of the diff-tile grid dirty,
    /// which trips <c>FullRefreshChangeThreshold</c> and makes <c>Render</c> promote to Full.
    /// </summary>
    public static Image<Rgba32> PromoteLarge()
    {
        var img = NewWhiteCanvas();
        img.Mutate(ctx =>
        {
            ctx.SetGraphicsOptions(g => g.Antialias = false);
            for (int r = 0; r < Rows; r += 2)
                ctx.Fill(Color.Black, new RectangleF(0, r * TileHeight, Epd4in26.Width, TileHeight));
        });
        DrawTileGridLines(img);
        DrawTileLabels(img, invertEvenRows: true);
        return img;
    }

    // ---- Primitives ------------------------------------------------------------------------

    static Image<Rgba32> NewWhiteCanvas() =>
        new Image<Rgba32>(Epd4in26.Width, Epd4in26.Height, Color.White);

    static void DrawTileGrid(Image<Rgba32> img)
    {
        DrawTileGridLines(img);
        DrawTileLabels(img, invertEvenRows: false);
    }

    static void DrawTileGridLines(Image<Rgba32> img)
    {
        img.Mutate(ctx =>
        {
            ctx.SetGraphicsOptions(g => g.Antialias = false);
            var pen = Pens.Solid(Color.Black, 1f);

            for (int r = 0; r <= Rows; r++)
            {
                float y = Math.Min(r * TileHeight, Epd4in26.Height - 1);
                ctx.DrawLine(pen, new PointF(0, y), new PointF(Epd4in26.Width - 1, y));
            }
            for (int c = 0; c <= Cols; c++)
            {
                float x = Math.Min(c * TileWidth, Epd4in26.Width - 1);
                ctx.DrawLine(pen, new PointF(x, 0), new PointF(x, Epd4in26.Height - 1));
            }
        });
    }

    static void DrawTileLabels(Image<Rgba32> img, bool invertEvenRows)
    {
        var font = _interBold.CreateFont(14f);
        img.Mutate(ctx =>
        {
            ctx.SetGraphicsOptions(g => g.Antialias = false);
            for (int r = 0; r < Rows; r++)
            {
                bool inverted = invertEvenRows && (r % 2 == 0);
                var color = inverted ? Color.White : Color.Black;
                for (int c = 0; c < Cols; c++)
                {
                    var opts = new RichTextOptions(font)
                    {
                        Origin = new PointF(c * TileWidth + TileWidth / 2f, r * TileHeight + TileHeight / 2f),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    ctx.DrawText(opts, TileLabel(c, r), color);
                }
            }
        });
    }

    static void DrawBigInvertedTile(Image<Rgba32> img, int col, int row, string text)
    {
        int x = col * TileWidth;
        int y = row * TileHeight;
        var font = _interBold.CreateFont(28f);

        img.Mutate(ctx =>
        {
            ctx.SetGraphicsOptions(g => g.Antialias = false);
            ctx.Fill(Color.Black, new RectangleF(x, y, TileWidth, TileHeight));

            var opts = new RichTextOptions(font)
            {
                Origin = new PointF(x + TileWidth / 2f, y + TileHeight / 2f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            ctx.DrawText(opts, text, Color.White);
        });
    }
}
