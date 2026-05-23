using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

static class SaPlacer
{
    private const double InitialTemperature = 800.0;
    private const double CoolingRate        = 0.9995;
    private const int    Iterations         = 150_000;
    private const int    MaxPerturbation    = 60;
    private const double WallPenaltyWeight  = 20.0;
    private const double OverlapPenalty     = 2000.0;
    private const double DistancePenalty    = 2.5;
    private const int    PixelThreshold     = 100;

    public static void Run(List<LabelEntry> entries, Image<Rgba32> floorplan)
    {
        long[,] integral = BuildIntegralImage(floorplan, PixelThreshold, dark: true);

        var rng = new Random();
        double temperature   = InitialTemperature;
        double currentEnergy = TotalEnergy(entries, integral, floorplan.Width, floorplan.Height);

        for (int iter = 0; iter < Iterations; iter++)
        {
            var label = entries[rng.Next(entries.Count)];
            int oldX = label.X, oldY = label.Y;

            int newX = Math.Clamp(oldX + rng.Next(-MaxPerturbation, MaxPerturbation + 1), 0, floorplan.Width  - label.W);
            int newY = Math.Clamp(oldY + rng.Next(-MaxPerturbation, MaxPerturbation + 1), 0, floorplan.Height - label.H);

            label.X = newX;
            label.Y = newY;

            double newEnergy = TotalEnergy(entries, integral, floorplan.Width, floorplan.Height);
            double delta     = newEnergy - currentEnergy;

            if (delta < 0 || rng.NextDouble() < Math.Exp(-delta / temperature))
                currentEnergy = newEnergy;
            else
            { label.X = oldX; label.Y = oldY; }

            temperature *= CoolingRate;
        }

        Console.WriteLine($"Final energy: {currentEnergy:F1}");
    }

    private static long[,] BuildIntegralImage(Image<Rgba32> img, int threshold, bool dark)
    {
        int w = img.Width, h = img.Height;
        var ii = new long[h + 1, w + 1];

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p   = img[x, y];
                int hit = dark
                    ? (p.R < threshold && p.G < threshold && p.B < threshold ? 1 : 0)
                    : (p.R > threshold && p.G > threshold && p.B > threshold ? 1 : 0);
                ii[y + 1, x + 1] = hit + ii[y, x + 1] + ii[y + 1, x] - ii[y, x];
            }

        return ii;
    }

    private static long DarkPixelsInRect(long[,] ii, int x, int y, int w, int h, int imgW, int imgH)
    {
        int x1 = Math.Clamp(x,     0, imgW);
        int y1 = Math.Clamp(y,     0, imgH);
        int x2 = Math.Clamp(x + w, 0, imgW);
        int y2 = Math.Clamp(y + h, 0, imgH);
        if (x1 >= x2 || y1 >= y2) return 0;
        return ii[y2, x2] - ii[y1, x2] - ii[y2, x1] + ii[y1, x1];
    }

    private static int IntersectionArea(LabelEntry a, LabelEntry b)
    {
        int ox = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        int oy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        return ox * oy;
    }

    private static double TotalEnergy(List<LabelEntry> labels, long[,] ii, int imgW, int imgH)
    {
        double energy = 0;

        for (int i = 0; i < labels.Count; i++)
        {
            var a = labels[i];

            energy += WallPenaltyWeight * DarkPixelsInRect(ii, a.X, a.Y, a.W, a.H, imgW, imgH);

            double dist = Math.Sqrt(Math.Pow(a.X - a.AnchorX, 2) + Math.Pow(a.Y - a.AnchorY, 2));
            energy += DistancePenalty * dist;

            for (int j = i + 1; j < labels.Count; j++)
                energy += OverlapPenalty * IntersectionArea(a, labels[j]);
        }

        return energy;
    }
}
