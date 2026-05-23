using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>
/// Asks Claude to suggest a placement for each label individually (one API call per label).
/// Sends a 400×400 crop of the floorplan centred on the label's anchor for each call.
/// No priority grouping — labels are placed in input order.
/// Already-placed labels are passed as obstacles in crop-local coordinates.
/// Falls back to the anchor position on any error.
/// </summary>
static class LlmOneByOnePlacer
{
    private static readonly HttpClient _http = new();

    private const string Model = "claude-haiku-4-5-20251001";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private const string SystemPrompt =
        "You are an architectural drafting assistant. Your job is to decide the final pixel " +
        "coordinates for a single label on a cropped region of a floorplan image, following " +
        "the conventions a human drafter would use.\n\n" +
        "Inputs you will receive:\n\n" +
        "A cropped PNG region of the floorplan, up to 400×400 pixels.\n" +
        "Its dimensions: crop_width, crop_height (pixels, origin top-left of the crop).\n" +
        "All coordinates below are relative to the top-left corner of this crop.\n\n" +
        "The label to place, with:\n\n" +
        "  name     — identifier\n" +
        "  anchor_x, anchor_y — the desired centre point of the label in crop coordinates\n" +
        "  width, height      — the rendered size of the label graphic in pixels\n\n" +
        "already_placed — labels already positioned on the floorplan whose bounding boxes " +
        "are near this crop. Each entry has name, centre_x, centre_y, width, height, " +
        "all in crop coordinates (may be negative or > crop size if partially outside). " +
        "Their bounding boxes are locked and must be treated as solid obstacles.\n\n" +
        "Placement rules, in order of precedence:\n\n" +
        "1. No overlap. The label's bounding box must not intersect (a) any label in already_placed, " +
        "(b) walls or dark linework, (c) furniture, fixtures, dimension lines, or existing annotations. " +
        "Leave at least a 5 px buffer on all sides.\n" +
        "2. Stay inside the crop. The full label rectangle must fit within " +
        "(0, 0, crop_width, crop_height).\n" +
        "3. Place inside the relevant space when possible. A room label goes inside that " +
        "room's walls. A fixture label sits next to the fixture, not across a wall.\n" +
        "4. Prefer clear whitespace. Choose the nearest open area to anchor_x, anchor_y — " +
        "ideally the visual centroid of the room or the widest empty pocket adjacent to the fixture.\n" +
        "5. Minimise distance from the anchor. Among valid spots, pick the one where the label " +
        "centre is closest to (anchor_x, anchor_y).\n" +
        "6. Keep labels axis-aligned and horizontally readable. Do not rotate.\n\n" +
        "Output format — strict JSON, no prose, no markdown fences:\n" +
        "{ \"x\": 0, \"y\": 0, \"confidence\": 0.0, \"reason\": \"explanation\" }\n\n" +
        "x, y are the centre pixel coordinates of the label rectangle IN CROP COORDINATES. " +
        "The ideal placement is x = anchor_x, y = anchor_y. " +
        "Adjust from there to avoid overlaps and walls while staying as close to the anchor as possible.\n" +
        "confidence is 0-1.\n\n" +
        "Process to follow internally before answering:\n\n" +
        "1. Scan the crop for walls, rooms, fixtures, and existing text.\n" +
        "2. Mark all bounding boxes from already_placed as occupied.\n" +
        "3. Identify the room or feature the label belongs to.\n" +
        "4. Find the largest empty region within or adjacent to that feature.\n" +
        "5. Verify the proposed rectangle does not violate rules 1-2.\n" +
        "6. If no valid spot exists, pick the least-bad position and set confidence low.";

    // Cartographic rules injected into the prompt per tag type.
    private static readonly Dictionary<string, string> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RoomTag"]   = "Room labels should be placed in the centre of the room interior, " +
                        "well away from walls, doors and windows.",
        ["WindowTag"] = "Window labels should be placed just inside the room adjacent to the " +
                        "window opening, not overlapping the window line itself.",
        ["DoorTag"]   = "Door labels should be placed in clear open space beside the door, " +
                        "not blocking the doorway passage.",
        ["WallTag"]   = "Wall labels should be placed along the wall in a clear gap, " +
                        "not overlapping other structural features.",
    };

    public static async Task<(int x, int y)[]> PlaceAllAsync(
        List<LabelEntry> entries, string floorplanPath, int imgW, int imgH)
    {
        // Default: centre each label on its anchor, clamped to canvas bounds.
        var results = entries
            .Select(e => (
                x: Math.Clamp(e.AnchorX - e.W / 2, 0, imgW - e.W),
                y: Math.Clamp(e.AnchorY - e.H / 2, 0, imgH - e.H)))
            .ToArray();

        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("[LLM-OBO] ANTHROPIC_API_KEY not set – skipping.");
            return results;
        }

        try
        {
            const int CropSize = 400;

            // Load the full image once — crops are 400×400 so no global resize needed.
            using var fullImg = Image.Load<Rgba32>(floorplanPath);

            // Accumulated placed labels in full-image centre coords.
            var alreadyPlaced = new List<(int cx, int cy, int w, int h, string name)>();

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                Console.WriteLine($"[LLM-OBO] Placing label {i + 1}/{entries.Count}: {e.Name}…");

                // Compute crop rectangle centred on the anchor, clamped to image bounds.
                int cropX = Math.Clamp(e.AnchorX - CropSize / 2, 0, Math.Max(0, imgW - CropSize));
                int cropY = Math.Clamp(e.AnchorY - CropSize / 2, 0, Math.Max(0, imgH - CropSize));
                int cropW = Math.Min(CropSize, imgW - cropX);
                int cropH = Math.Min(CropSize, imgH - cropY);

                // Crop and encode.
                string cropBase64;
                using (var crop = fullImg.Clone(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH))))
                {
                    using var ms = new MemoryStream();
                    crop.SaveAsPng(ms);
                    cropBase64 = Convert.ToBase64String(ms.ToArray());
                }

                // Anchor in crop-local coords.
                int localAnchorX = e.AnchorX - cropX;
                int localAnchorY = e.AnchorY - cropY;

                string rule = Rules.TryGetValue(e.Name, out var r)
                    ? r
                    : "Place the label at the centre of the feature anchor in clear open space.";

                // Translate all obstacles to crop-local coords.
                var localObstacles = alreadyPlaced.Select(p => new
                {
                    name     = p.name,
                    centre_x = p.cx - cropX,
                    centre_y = p.cy - cropY,
                    width    = p.w,
                    height   = p.h,
                }).ToArray();

                var labelData = new
                {
                    name     = e.Name,
                    anchor_x = localAnchorX,
                    anchor_y = localAnchorY,
                    width    = e.W,
                    height   = e.H,
                };

                string userText =
                    $"Crop dimensions: {cropW}x{cropH} pixels " +
                    $"(top-left of crop is at full-image coords {cropX},{cropY}).\n\n" +
                    $"Tag-specific placement rule: {rule}\n\n" +
                    $"Already placed (crop-local coords, treat as solid obstacles):\n{JsonSerializer.Serialize(localObstacles, IndentedJson)}\n\n" +
                    $"Label to place (crop-local coords):\n{JsonSerializer.Serialize(labelData, IndentedJson)}";

                var placement = await CallApiAsync(apiKey, cropBase64, userText);
                if (placement == null)
                {
                    Console.WriteLine($"[LLM-OBO] [{i}] {e.Name} – API returned null, using fallback.");
                }
                else
                {
                    // API returns centre in crop-local coords — translate back to full image.
                    int localPx = placement["x"]?.GetValue<int>() ?? localAnchorX;
                    int localPy = placement["y"]?.GetValue<int>() ?? localAnchorY;

                    int fullCx = localPx + cropX;
                    int fullCy = localPy + cropY;

                    // Centre → top-left, clamped to canvas.
                    results[i] = (
                        x: Math.Clamp(fullCx - e.W / 2, 0, imgW - e.W),
                        y: Math.Clamp(fullCy - e.H / 2, 0, imgH - e.H));

                    double conf   = placement["confidence"]?.GetValue<double>() ?? 1.0;
                    string reason = placement["reason"]?.GetValue<string>() ?? "";
                    e.Confidence = conf;
                    e.Reason = reason;
                    Console.WriteLine(
                        $"[LLM-OBO] [{i}] {e.Name} " +
                        $"anchor ({e.AnchorX},{e.AnchorY}) " +
                        $"-> ({results[i].x},{results[i].y})  conf={conf:F2}  {reason}");

                    // Record placed centre in full-image coords as obstacle for subsequent calls.
                    alreadyPlaced.Add((fullCx, fullCy, e.W, e.H, e.Name));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LLM-OBO] Exception: {ex.Message}");
        }

        return results;
    }

    // Calls the API for a single label and returns the parsed JSON node, or null on failure.
    private static async Task<JsonNode?> CallApiAsync(
        string apiKey, string imageBase64, string userText)
    {
        var requestBody = new
        {
            model      = Model,
            max_tokens = 256,
            system     = SystemPrompt,
            messages   = new[]
            {
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new
                        {
                            type   = "image",
                            source = new
                            {
                                type       = "base64",
                                media_type = "image/png",
                                data       = imageBase64
                            }
                        },
                        new { type = "text", text = userText }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(requestBody);

        // Retry on 429 with exponential backoff.
        HttpResponseMessage resp = null!;
        int[] retryDelaysMs = [4000, 8000, 16000];
        for (int attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key",         apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            resp = await _http.SendAsync(req);

            if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests) break;

            if (attempt < retryDelaysMs.Length)
            {
                int delay = retryDelaysMs[attempt];
                Console.WriteLine($"[LLM-OBO] 429 – waiting {delay / 1000}s before retry {attempt + 1}…");
                resp.Dispose();
                await Task.Delay(delay);
            }
        }

        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[LLM-OBO] API error {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
            return null;
        }

        string body  = await resp.Content.ReadAsStringAsync();
        var    doc   = JsonNode.Parse(body);
        string? text = doc?["content"]?[0]?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return null;

        int start = text.IndexOf('{');
        int end   = text.LastIndexOf('}');
        if (start < 0 || end < 0) return null;

        return JsonNode.Parse(text[start..(end + 1)]);
    }
}
