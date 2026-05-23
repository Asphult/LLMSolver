using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// ── Mode ─────────────────────────────────────────────────────────────────────
bool useSA         = args.Contains("--sa");
bool useBitmap     = args.Contains("--bitmap");
bool usePerLabel   = args.Contains("--llm-per-label");
bool useOneByOne   = args.Contains("--llm-one-by-one");
string modeName    = useSA        ? "Simulated Annealing"
                   : useBitmap    ? "Bitmap"
                   : usePerLabel  ? "LLM per-label"
                   : useOneByOne  ? "LLM one-by-one"
                   : "LLM batch";
Console.WriteLine($"Mode: {modeName}");

// ── Paths ────────────────────────────────────────────────────────────────────
string baseDir    = AppContext.BaseDirectory.Contains("bin")
    ? @"C:\Users\leome\OneDrive - University College London\Documents\LabelingApp"
    : AppContext.BaseDirectory;
string inputPath  = Path.Combine(baseDir, "floorplan.png");
string coordsPath = Path.Combine(baseDir, "coords.txt");
string tagsDir    = Path.Combine(baseDir, "Tags");
string imagesDir = Path.Combine(baseDir,
    useSA        ? "SAResults"
  : useBitmap    ? "BitmapResults"
  : usePerLabel  ? "LLMPerLabelResults"
  : useOneByOne  ? "LLMOneByOneResults"
  : "LLMResults");
Directory.CreateDirectory(imagesDir);

// Find the next unused run number so previous outputs are never overwritten.
int runNumber = 1;
while (File.Exists(Path.Combine(imagesDir, $"floorplan_optimized_{runNumber}.png")))
    runNumber++;

string outImagePath  = Path.Combine(imagesDir, $"floorplan_optimized_{runNumber}.png");
string outCoordsPath = Path.Combine(imagesDir, $"coords_placed_{runNumber}.csv");

if (!File.Exists(inputPath) || !File.Exists(coordsPath)) return;

using var floorplan = Image.Load<Rgba32>(inputPath);

// ── Load all label entries ────────────────────────────────────────────────────
var entries = new List<LabelEntry>();
foreach (string line in File.ReadAllLines(coordsPath))
{
    var parts = line.Split(',');
    if (parts.Length < 3) continue;

    int ax      = (int)float.Parse(parts[0]);
    int ay      = (int)float.Parse(parts[1]);
    string name = parts[2].Trim();

    string tagPath = Path.Combine(tagsDir, $"{name}.png");
    if (!File.Exists(tagPath)) { Console.WriteLine($"Tag not found: {tagPath}"); continue; }

    using var tmp = Image.Load<Rgba32>(tagPath);
    entries.Add(new LabelEntry(name, ax, ay, tmp.Width, tmp.Height));
}

if (entries.Count == 0) return;

// ── Place labels ──────────────────────────────────────────────────────────────
if (useSA)
{
    SaPlacer.Run(entries, floorplan);
}
else if (useBitmap)
{
    BitmapPlacer.Run(entries, floorplan);
}
else if (usePerLabel)
{
    Console.WriteLine($"Requesting per-label LLM placements for {entries.Count} labels…");
    await LlmPlacerPerLabel.RunAsync(entries, floorplan.Width, floorplan.Height);
}
else if (useOneByOne)
{
    Console.WriteLine($"Requesting one-by-one LLM placements for {entries.Count} labels…");
    var positions = await LlmOneByOnePlacer.PlaceAllAsync(
        entries, inputPath, floorplan.Width, floorplan.Height);

    for (int i = 0; i < entries.Count; i++)
    {
        entries[i].X = positions[i].x;
        entries[i].Y = positions[i].y;
    }
}
else
{
    Console.WriteLine($"Requesting LLM batch placements for {entries.Count} labels…");
    var positions = await LlmPlacer.SuggestAllPlacementsAsync(
        entries, inputPath, floorplan.Width, floorplan.Height);

    for (int i = 0; i < entries.Count; i++)
    {
        entries[i].X = positions[i].x;
        entries[i].Y = positions[i].y;
    }
}

// ── Draw all placed labels and write output ───────────────────────────────────
var outputCoords = new List<string>();

foreach (var e in entries)
{
    string tagPath = Path.Combine(tagsDir, $"{e.Name}.png");
    using var tagImage = Image.Load<Rgba32>(tagPath);
    floorplan.Mutate(ctx => ctx.DrawImage(tagImage, new Point(e.X, e.Y), 1f));
    outputCoords.Add($"{e.AnchorX},{e.AnchorY},{e.X + e.W / 2},{e.Y + e.H / 2},{e.Name},{e.Confidence:F2},{e.Reason}");
    Console.WriteLine($"Placed {e.Name} at ({e.X + e.W / 2},{e.Y + e.H / 2})  (anchor {e.AnchorX},{e.AnchorY})");
}

floorplan.Save(outImagePath);
File.WriteAllLines(outCoordsPath, outputCoords);
Console.WriteLine($"Saved image  → {outImagePath}");
Console.WriteLine($"Saved coords → {outCoordsPath}");
