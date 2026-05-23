using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

static class LlmPlacer
{
    private static readonly HttpClient _http = new();

    private const string Model = "claude-opus-4-7";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private const string SystemPrompt =
        "You are an architectural drafting assistant. Your job is to decide the final pixel " +
        "coordinates for a set of labels on a floorplan image, following the conventions a " +
        "human drafter would use.\n\n" +
        "Inputs you will receive:\n\n" +
        "A rendered floorplan image (PNG).\n" +
        "Its dimensions: floorplan_width, floorplan_height (pixels, origin top-left).\n" +
        "A list of labels to place. Each label has:\n\n" +
        "  id     — unique integer index\n" +
        "  name   — identifier\n" +
        "  anchor_x, anchor_y — the desired centre point of the label on the floorplan\n" +
        "  width, height      — the rendered size of the label graphic in pixels\n" +
        "  priority           — higher must be placed first if conflicts arise\n\n" +
        "already_placed — an array of labels already positioned on the floorplan. " +
        "Each entry has id, name, centre_x, centre_y, width, height. " +
        "Their bounding boxes are locked and must be treated as solid obstacles.\n\n" +
        "Placement rules, in order of precedence:\n\n" +
        "1. No overlap. A label's bounding box must not intersect (a) any label in already_placed, " +
        "(b) any other label in this batch, (c) walls or dark linework, (d) furniture, fixtures, " +
        "dimension lines, or existing annotations. Leave at least a 5 px buffer on all sides.\n" +
        "2. Stay inside the canvas. The full label rectangle must fit within " +
        "(0, 0, floorplan_width, floorplan_height).\n" +
        "3. Place inside the relevant space when possible. A room label goes inside that " +
        "room's walls. A fixture label sits next to the fixture, not across a wall.\n" +
        "4. Prefer clear whitespace. Choose the nearest open area to anchor_x, anchor_y — " +
        "ideally the visual centroid of the room or the widest empty pocket adjacent to the fixture.\n" +
        "5. Minimize distance from the anchor. Among valid spots, pick the one where the label " +
        "centre is closest to (anchor_x, anchor_y).\n" +
        "6. Keep labels axis-aligned and horizontally readable. Do not rotate.\n" +
        "7. Distribute, don't cluster. If multiple labels compete for the same pocket of " +
        "whitespace, spread them so each has breathing room rather than stacking them tightly.\n" +
        "8. Respect hierarchy. Room names get the prime central spot; sub-labels (fixtures, " +
        "dimensions) take secondary positions around them.\n\n" +
        "Output format — strict JSON, no prose, no markdown fences:\n" +
        "{\n" +
        "  \"placements\": [\n" +
        "    {\n" +
        "      \"id\": 0,\n" +
        "      \"name\": \"string\",\n" +
        "      \"x\": 0,\n" +
        "      \"y\": 0,\n" +
        "      \"confidence\": 0.0,\n" +
        "      \"reason\": \"explanation\"\n" +
        "    }\n" +
        "  ],\n" +
        "  \"unplaced\": [\n" +
        "    { \"id\": 0, \"name\": \"string\", \"reason\": \"why no valid spot was found\" }\n" +
        "  ]\n" +
        "}\n\n" +
        "x, y are the centre pixel coordinates of the label rectangle. " +
        "The ideal placement is x = anchor_x, y = anchor_y. " +
        "Adjust from there to avoid overlaps and walls while staying as close to the anchor as possible.\n" +
        "confidence is 0-1.\n" +
        "Every input label must appear in either placements or unplaced.\n" +
        "Every placement and unplaced entry must include the original id.\n\n" +
        "Process to follow internally before answering:\n\n" +
        "1. Scan the floorplan for walls, rooms, fixtures, and existing text.\n" +
        "2. Mark all bounding boxes from already_placed as occupied.\n" +
        "3. For each label (in priority order), identify the room or feature it belongs to.\n" +
        "4. Find the largest empty region within or adjacent to that feature.\n" +
        "5. Verify the proposed rectangle against rules 1-2, all already_placed labels, " +
        "and all labels placed so far in this response.\n" +
        "6. If no valid spot exists within a reasonable search radius, add it to unplaced.";

    // Priority by tag type — higher = placed first.
    private static readonly Dictionary<string, int> TagPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RoomTag"]   = 3,
        ["WindowTag"] = 2,
        ["DoorTag"]   = 2,
        ["WallTag"]   = 1,
    };

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

    public static async Task<(int x, int y)[]> SuggestAllPlacementsAsync(
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
            Console.WriteLine("[LLM] ANTHROPIC_API_KEY not set - skipping LLM placement.");
            return results;
        }

        try
        {
            // Resize the image once if either dimension exceeds the API limit (8000 px).
            const int MaxImageDim = 7800;
            double scale = 1.0;
            string imageBase64;
            int scaledW = imgW, scaledH = imgH;

            using (var srcImg = Image.Load<Rgba32>(floorplanPath))
            {
                if (srcImg.Width > MaxImageDim || srcImg.Height > MaxImageDim)
                {
                    scale   = Math.Min((double)MaxImageDim / srcImg.Width, (double)MaxImageDim / srcImg.Height);
                    scaledW = (int)(srcImg.Width  * scale);
                    scaledH = (int)(srcImg.Height * scale);
                    srcImg.Mutate(ctx => ctx.Resize(scaledW, scaledH));
                    Console.WriteLine($"[LLM] Resized image {imgW}x{imgH} → {scaledW}x{scaledH} (scale={scale:F3})");
                }
                using var ms = new MemoryStream();
                srcImg.SaveAsPng(ms);
                imageBase64 = Convert.ToBase64String(ms.ToArray());
            }

            // Process labels in descending priority groups: RoomTag → WindowTag/DoorTag → WallTag.
            // Each group is a separate API call; previously placed labels are passed as locked obstacles.
            var groups = entries
                .Select((e, i) => (entry: e, idx: i))
                .GroupBy(x => TagPriority.TryGetValue(x.entry.Name, out var p) ? p : 1)
                .OrderByDescending(g => g.Key);

            // Accumulates placed labels in scaled coords to send as obstacles in subsequent calls.
            var alreadyPlaced = new List<object>();

            foreach (var group in groups)
            {
                var groupItems    = group.ToList();
                int groupPriority = group.Key;
                Console.WriteLine($"[LLM] Placing priority-{groupPriority} group ({groupItems.Count} labels)…");

                var labelData = groupItems.Select(x => new
                {
                    id       = x.idx,
                    name     = x.entry.Name,
                    anchor_x = (int)(x.entry.AnchorX * scale),
                    anchor_y = (int)(x.entry.AnchorY * scale),
                    width    = (int)(x.entry.W * scale),
                    height   = (int)(x.entry.H * scale),
                    priority = groupPriority,
                }).ToArray();

                // Collect placement rules for the tag types present in this group.
                string groupRules = string.Join("\n", groupItems
                    .Select(x => x.entry.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(Rules.ContainsKey)
                    .Select(n => $"- {Rules[n]}"));

                string userText =
                    $"Floorplan dimensions: {scaledW}x{scaledH} pixels.\n\n" +
                    (groupRules.Length > 0 ? $"Tag-specific placement rules:\n{groupRules}\n\n" : "") +
                    $"Already placed (treat as solid obstacles):\n{JsonSerializer.Serialize(alreadyPlaced, IndentedJson)}\n\n" +
                    $"Labels to place:\n{JsonSerializer.Serialize(labelData, IndentedJson)}";

                var placements = await CallApiAsync(apiKey, imageBase64, userText);
                if (placements == null) continue;

                foreach (var placement in placements)
                {
                    int id = placement?["id"]?.GetValue<int>() ?? -1;
                    if (id < 0 || id >= results.Length) continue;

                    // LLM returns centre coordinates — convert to top-left in scaled space.
                    int labelScaledW = (int)(entries[id].W * scale);
                    int labelScaledH = (int)(entries[id].H * scale);

                    int defaultCx = results[id].x + entries[id].W / 2;
                    int defaultCy = results[id].y + entries[id].H / 2;
                    int px = placement?["x"]?.GetValue<int>() ?? (int)(defaultCx * scale);
                    int py = placement?["y"]?.GetValue<int>() ?? (int)(defaultCy * scale);

                    // Convert centre → top-left in scaled space, then scale up to full resolution.
                    int scaledTopLeftX = px - labelScaledW / 2;
                    int scaledTopLeftY = py - labelScaledH / 2;
                    int fullX = scale < 1.0 ? (int)(scaledTopLeftX / scale) : scaledTopLeftX;
                    int fullY = scale < 1.0 ? (int)(scaledTopLeftY / scale) : scaledTopLeftY;

                    results[id] = (
                        x: Math.Clamp(fullX, 0, imgW - entries[id].W),
                        y: Math.Clamp(fullY, 0, imgH - entries[id].H));

                    double conf   = placement?["confidence"]?.GetValue<double>() ?? 1.0;
                    string reason = placement?["reason"]?.GetValue<string>() ?? "";
                    entries[id].Confidence = conf;
                    entries[id].Reason = reason;
                    Console.WriteLine(
                        $"[LLM] [{id}] {entries[id].Name} " +
                        $"anchor ({entries[id].AnchorX},{entries[id].AnchorY}) " +
                        $"-> ({results[id].x},{results[id].y})  conf={conf:F2}  {reason}");

                    // Add to obstacle list for subsequent groups using centre coords in scaled space.
                    alreadyPlaced.Add(new
                    {
                        id       = id,
                        name     = entries[id].Name,
                        centre_x = px,
                        centre_y = py,
                        width    = labelScaledW,
                        height   = labelScaledH,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LLM] Exception: {ex.Message}");
        }

        return results;
    }

    // Sends one group of labels to the API and returns the placements array, or null on failure.
    private static async Task<JsonArray?> CallApiAsync(
        string apiKey, string imageBase64, string userText)
    {
        var requestBody = new
        {
            model      = Model,
            max_tokens = 8192,
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

        // Send with retry on 429.
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
                Console.WriteLine($"[LLM] 429 - waiting {delay / 1000}s before retry {attempt + 1}...");
                resp.Dispose();
                await Task.Delay(delay);
            }
        }

        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[LLM] API error {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
            return null;
        }

        string body  = await resp.Content.ReadAsStringAsync();
        var    doc   = JsonNode.Parse(body);
        string? text = doc?["content"]?[0]?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Strip any markdown fences and extract the JSON object.
        int start = text.IndexOf('{');
        int end   = text.LastIndexOf('}');
        if (start < 0 || end < 0) return null;

        var responseDoc = JsonNode.Parse(text[start..(end + 1)]);

        // Log unplaced labels.
        var unplaced = responseDoc?["unplaced"]?.AsArray();
        if (unplaced != null)
            foreach (var u in unplaced)
                Console.WriteLine($"[LLM] UNPLACED [{u?["id"]}] {u?["name"]}: {u?["reason"]}");

        return responseDoc?["placements"]?.AsArray();
    }
}
