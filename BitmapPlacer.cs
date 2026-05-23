using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

static class BitmapPlacer
{
    private const int MaxSearchDistance = 100;
    private const int Buffer            = 5;
    private const int PixelThreshold    = 100;

    public static void Run(List<LabelEntry> entries, Image<Rgba32> floorplan)
    {
        var placedRects = new List<Rectangle>();

        foreach (var e in entries)
        {
            // Search outward from the ideal top-left position (already set in LabelEntry).
            Point? spot = FindAvailableSpot(floorplan, e.X, e.Y, e.W, e.H, placedRects);

            if (spot.HasValue)
            {
                e.X = spot.Value.X;
                e.Y = spot.Value.Y;
                placedRects.Add(new Rectangle(e.X, e.Y, e.W, e.H));
                Console.WriteLine($"[Bitmap] Placed {e.Name} at ({e.X + e.W / 2},{e.Y + e.H / 2})  (anchor {e.AnchorX},{e.AnchorY})");
            }
            else
            {
                Console.WriteLine($"[Bitmap] Could not fit {e.Name} near ({e.AnchorX},{e.AnchorY}) — keeping default position");
                placedRects.Add(new Rectangle(e.X, e.Y, e.W, e.H));
            }
        }
    }

    private static Point? FindAvailableSpot(
        Image<Rgba32> img, int startX, int startY, int w, int h, List<Rectangle> placed)
    {
        for (int distance = 0; distance < MaxSearchDistance; distance++)
        {
            for (int dy = -distance; dy <= distance; dy++)
            {
                for (int dx = -distance; dx <= distance; dx++)
                {
                    int testX = startX + dx;
                    int testY = startY + dy;

                    if (testX < 0 || testY < 0 || testX + w > img.Width || testY + h > img.Height)
                        continue;

                    var candidate = new Rectangle(testX - Buffer, testY - Buffer,
                                                  w + Buffer * 2, h + Buffer * 2);

                    if (IsAreaClear(img, candidate) && !placed.Any(r => r.IntersectsWith(candidate)))
                        return new Point(testX, testY);
                }
            }
        }
        return null;
    }

    private static bool IsAreaClear(Image<Rgba32> img, Rectangle area)
    {
        int x1 = Math.Max(0, area.X);
        int y1 = Math.Max(0, area.Y);
        int x2 = Math.Min(img.Width,  area.X + area.Width);
        int y2 = Math.Min(img.Height, area.Y + area.Height);

        for (int y = y1; y < y2; y++)
            for (int x = x1; x < x2; x++)
            {
                var p = img[x, y];
                if (p.R < PixelThreshold && p.G < PixelThreshold && p.B < PixelThreshold)
                    return false;
            }

        return true;
    }
}
