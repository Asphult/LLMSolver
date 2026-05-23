# Hello Tag — Revit 2026 Plugin

A Revit 2026 addin that automates annotation tag placement for rooms, doors, windows, walls, and stairs in floor plan views using Claude API.

## Requirements

- Autodesk Revit 2026
- Windows 10 or 11

## Installation

1. Copy both files into `%AppData%\Autodesk\Revit\Addins\2026\`:
   - `RevitHelloTag.dll`
   - `RevitHelloTag.addin`
2. Restart Revit.
3. A **Hello Tag** ribbon tab will appear.

If Revit shows a security warning the first time, click **Always Load**.

## Features

All commands operate on the **active floor plan view** only. Commands that place tags require the relevant tag family to be loaded in the project (e.g. Room Tag.rfa, Door Tag.rfa).

### Tagging Rooms

| Button | Description |
|--------|-------------|
| Tag Next Room | Tags the next untagged room (sorted by room number). Click repeatedly to walk through all rooms one at a time. |
| Tag All Rooms | Tags every untagged room in one step. All placements are a single undo step. |

### Tagging Doors

| Button | Description |
|--------|-------------|
| Tag Next Door | Tags the next untagged door (by element ID). |
| Tag All Doors | Tags every untagged door in the view in one undo step. |

### Tagging Windows

| Button | Description |
|--------|-------------|
| Tag Next Window | Tags the next untagged window (by element ID). |
| Tag All Windows | Tags every untagged window in the view in one undo step. |

### Tagging Walls

| Button | Description |
|--------|-------------|
| Tag Next Wall | Tags the next untagged wall (by element ID). |
| Tag All Walls | Tags every untagged wall in the view in one undo step. |
| Tag Walls by Length | Prompts for a minimum wall length in metres, then tags only walls that meet or exceed that threshold. |

### Tagging Stairs

| Button | Description |
|--------|-------------|
| Tag Next Stair | Tags the next untagged stair (by element ID). |
| Tag All Stairs | Tags every untagged stair in the view in one undo step. |

### Export Coords

Exports a CSV of all visible elements in the active view to the Desktop. A selection dialog lets you choose which element categories to include, with a **Quick Select Tags** button that pre-selects the standard taggable categories (Walls, Doors, Windows, Stairs, Rooms, Columns, Railings).

Output file: `RevitCoords_<ViewName>_<timestamp>.csv` on the Desktop.

The CSV includes model coordinates, view-local coordinates, bounding boxes, and a separate section for all existing tags in the view.

### Render CSV

Opens a file picker for a CSV produced by Export Coords and renders a colour-coded PNG and SVG floor plan diagram to the Desktop, showing bounding boxes for every element category, tag positions, room labels, and rotation arrows.

Output files: `BBoxRender_<name>_<timestamp>.png` and `.svg` on the Desktop.

### LLM Solve

Uses the Claude AI API to automatically reposition all tags in the active view into non-overlapping, aligned positions. The solver:

1. Collects all elements and tags from the view
2. Sends them to Claude as a CSV with placement rules
3. Applies Claude's suggested positions inside a single Revit transaction (one undo step)
4. Saves input/output CSVs to `Desktop\REVIT_LLM\` for inspection

**This feature requires an Anthropic API key.** Set the `ANTHROPIC_API_KEY` environment variable before launching Revit:

```powershell
# Run once in PowerShell (persists across reboots):
[Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "User")
```

Then restart Revit completely. If the key is not set, the button will show a diagnostic message explaining how to fix it.

**One-shot examples (optional):** Place `example_input.csv` and `example_output.csv` in `Desktop\REVIT_LLM\examples\` to give the solver a reference example from your own project. This improves output quality.

## Output file locations

All output files go to the current user's Desktop.

| Feature | Output location |
|---------|----------------|
| Export Coords | Desktop |
| Render CSV | Desktop |
| LLM Solve (CSVs) | Desktop\REVIT_LLM\ |

## Known Issues

- Occasionally the plugin will erroneously output the error 'The active view must be a floor plan view'. To mitigate this, simply re-open the floor plan view.

- For certain screen resolutions, the pop-up window for the Render CSV command will have the quick selection boxes cut-off. Please select manually or change resolution.


## Troubleshooting

**The ribbon tab doesn't appear**
- Confirm both `RevitHelloTag.dll` and `RevitHelloTag.addin` are in `%AppData%\Autodesk\Revit\Addins\2026\`.
- Restart Revit after copying the files.

**A button does nothing or shows an error**
- The active view must be a floor plan view. The commands will not run from 3D views, sections, or elevations.
- If a tag family is not loaded (e.g. no Door Tag family in the project), the command will report it and cancel.

**LLM Solve — API key not found**
- The `ANTHROPIC_API_KEY` environment variable must be set as a **User** variable (not just in a terminal), and Revit must be restarted after setting it.
- See the PowerShell command above.

**LLM Solve is slow**
- This is expected. The solver sends the full view data to Claude and may take up to few minutes on large views.
