# CUBIA - Columnar Unity-Based Immersive Analytics

<img width="1299" height="751" alt="CUBIA scatterplot in VR" src="https://github.com/user-attachments/assets/ee5fba77-ed20-4f9c-9763-f90e6bcc3f28" />

CUBIA is a Unity 6 application for exploring large datasets as 3D scatterplots in virtual reality. Data is prepared once on the desktop into a columnar binary format (`.cdataset`). Then one of three interchangeable rendering back-ends draws it in VR.

## Features

### Data
- **Columnar data:** each column is one contiguous array (Structure-of-Arrays), which suits datasets of 1M+ rows.
- **Binary format:** `.cdataset` files load with no per-cell parsing.
- **Column types:** Numeric, DateTime and Categorical, detected automatically.
- **Missing values:** empty cells are stored as missing rather than as 0, so filters and glyphs can tell them apart.
- **CSV input:** files separated by comma, semicolon, tab or `|` all work, and decimal commas (`468,5`) are read correctly.

### Rendering
- **Three back-ends:** VFX Graph (GPU particles, the default), Particle System (CPU particles) and GPU instancing.

### Visual mappings
- **X, Y, Z:** any numeric or DateTime label.
- **Colour:** numeric gradient or categorical palette.
- **Point size:** a fixed slider, or *Mapped* to a label.
- **Time:** scrubbing and playback over a time column.

### Filtering
- **By label:** picking a label fades out every point with no value in it.
- **By value:** picking one value is optional.
- **Combining:** several filters combine with OR, and *Clear all filters* resets them.

### Glyphs
- **Per value:** give one specific value its own shape.
- **Per range:** for numeric and DateTime labels, assign shapes to value ranges.
- **Range editor:** up to five ranges, an optional *Prevent overlap* toggle, and Confirm/Cancel.

### Bookmark palette
- **Blocks:** every bookmarked label appears as a block on a palette.
- **Drag and drop:** drop a block onto the X, Y, Z, Colour, Size, Filter, Time or Glyph slot to assign that label.

### Shuffle
- **Random mappings:** assigns random labels to explore a dataset quickly. Back (`<<`) and forward (`>>`) restore earlier states.
- **Locks:** each field can be locked, and each menu section can be locked or unlocked at once. Locks are saved per dataset.
- **Blacklist:** blacklisted labels are never picked.

### Interaction
- **Tooltips:** hover to see a data point's values.
- **Menus:** a summoned menu, a wrist menu, and grabbing, moving and scaling the plot.
- **Reset position:** puts the plot back where it started.

### Not implemented
- **Multiplayer / collaboration:** not implemented. The Photon and Unity Netcode packages are installed but unused.

## Requirements

Developed and tested on:

| | |
|---|---|
| Unity | 6000.5.2f1 (Unity 6); other versions may upgrade the project and touch scenes |
| OS | Windows 11 |
| CPU / GPU | Intel Core Ultra 9 285K, NVIDIA RTX PRO 4000 (24 GB) |
| RAM | 64 GB |
| Headsets | Vive Focus Vision Pro (used for all benchmarks), Meta Quest 3 |
| Python | 3.14 with pandas 3.0.5 and numpy 2.5.3, for preprocessing |

## Setup

1. **Clone the repository.** The sample CSVs in `Assets/StreamingAssets/DataCSV/` total about 260 MB. Git LFS is not used.
2. **Open the project** in Unity Hub with Unity 6000.5.2f1, and wait for the first import.
3. **Install the Python packages:**
   ```bash
   pip install -r Tests/Preprocessor/requirements.txt
   ```
4. **Set the OpenXR runtime** for your headset (see [XR setup](#xr-setup)).
5. **Open the main scene,** `Assets/Scenes/SampleScene.unity`, put on the headset, and press Play.

## Adding a dataset

1. **Add the CSV.** Put it in `Assets/StreamingAssets/DataCSV/`.
2. **Run the preprocessor** from the repository root. It converts every CSV in that folder:
   ```bash
   python Assets/Scripts/Data/preprocess_csv_columnar.py
   ```
3. **Check the log.** It prints each column's type, and says when a column is read as DateTime, so a wrong guess is easy to spot.
4. **Optional:** list a file's columns, types, ranges and missing-value counts:
   ```bash
   python plans/inspect_cdataset.py Assets/StreamingAssets/DataCSV/ProcessedData/<name>.cdataset
   ```
5. **Commit everything together:** the CSV, the `.cdataset` and both `.meta` files. CI fails if a `.cdataset` doesn't match its CSV.

**Type rules:**
- **Numeric:** a column is numeric when at least 90% of its values parse as numbers.
- **DateTime:** otherwise, a column is DateTime when at least 90% of its values parse as dates. Strings that look like numbers are never read as dates.
- **Categorical:** anything else.

## XR setup

The app uses OpenXR. The headset's runtime must be the active OpenXR runtime, and its controllers need an interaction profile enabled.

| Headset | OpenXR runtime | Controller profiles |
|---|---|---|
| Vive Focus Vision Pro | SteamVR | HTC Vive Controller |
| Meta Quest 3 | Meta Horizon Link | Oculus Touch, Meta Quest Touch Plus |

**Switching the runtime:**
- **For Unity only:** Project Settings → XR Plug-in Management → OpenXR → *Play Mode OpenXR Runtime*. This lasts until the editor restarts.
- **For the whole PC:** in SteamVR, Settings → OpenXR → *Set SteamVR as OpenXR Runtime*. This needs admin rights.

**Controller profiles** are set in Project Settings → XR Plug-in Management → OpenXR → *Enabled Interaction Profiles*. Without a matching profile, the runtime falls back to the Khronos Simple Controller. The trigger then acts as the primary button, which XRI binds to Jump.

## Project structure

```
Assets/
├── Scripts/
│   ├── Data/                  # Importers, DatasetColumnar/DatasetColumn, preprocess_csv_columnar.py
│   ├── Scatterplot/           # MultiplayerScatterplotManager (state), ScatterplotVisualizer,
│   │                          # the three renderers, interactables, ScatterplotUI,
│   │                          # ColumnMappingRules, ShuffleLocks, GlyphRangeEditor/Row
│   ├── UI/                    # Summoned and wrist menus, ShuffleLockPanel, BookmarkPalette/
│   ├── Grid/                  # Grid settings
│   └── Capture/               # Wide-angle screenshot capture
├── Scenes/                    # SampleScene (main app), VisualizerSceneTest (benchmark), BasicScene
├── Prefabs/                   # Menu panels, LabelBlock
└── StreamingAssets/DataCSV/   # Source CSVs and ProcessedData/*.cdataset
Tests/Preprocessor/            # Preprocessor regression tests and fixtures
.github/                       # CI workflows and check scripts
plans/                         # Implementation plans and the .cdataset inspector
```

`Assets/Photon`, `Assets/Samples` and `Assets/VRTemplateAssets` are third-party or Unity sample code; avoid editing them.

**`.cdataset` format (version 3):**
- **Header:** `DVCB` magic, version, row count, column count.
- **Numeric and DateTime columns:** min, max and float32 values. DateTime values are seconds from the column's earliest timestamp.
- **Categorical columns:** the category strings and int32 indices.
- **Every column:** a validity mask, 1 bit per row, saying which cells have a value.
- **Older files:** version 2 files still load, with every cell treated as present.

## Development

### Pull requests
- **Rebase only:** pull requests are merged with **Rebase and merge**, so `main` keeps a straight history with no merge commits.
- **After a merge:** either start the next branch fresh from `main`, or run `git rebase origin/main` on your branch before continuing.

### Checks on every pull request (GitHub Actions)
- **Unity `.meta` files:** every asset and folder has a `.meta`, and no `.meta` is orphaned.
- **Large files:** fails if a file over 50 MB is added or changed.
- **CSV preprocessor:** regression tests on small fixtures, plus a check that every committed `.cdataset` matches its CSV.

Run the preprocessor tests locally:
```bash
python Tests/Preprocessor/test_preprocessor.py fixtures    # parsing rules
python Tests/Preprocessor/test_preprocessor.py committed   # ProcessedData matches the CSVs
```
If you change the parsing on purpose, run `fixtures --update`, review the diff, and commit the new expected files.

### Working rules
- **Don't re-run Tools > DataViz > New UI Setup.** Several menu rows were placed by hand, and the tool would undo them.
- **Never commit participant or study data** to this repository.

## Known issues
- **Second Play run crash:** the Unity editor's second Play-mode run can crash when no headset is connected to SteamVR.
- **Multiplayer:** not implemented.
- **Instanced Mesh back-end:** its material is opaque, so filtered-out points don't fade there. The VFX Graph back-end is the one in use.
- **Sparse numeric columns:** a numeric column with more than 10% empty cells is classified as Categorical.
- **Glyph range dates on Quest builds:** the date labels on glyph range handles need the source CSV. On a Quest build they show elapsed time instead.

## Future work
- [ ] Drop label blocks straight onto the 3D axes
- [ ] Additional visualisation types (heatmaps, parallel coordinates)
- [ ] Data export and sharing
- [ ] Collaborative (multiplayer) exploration and annotation
- [ ] Real-time data streaming

## License

This project is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). Third-party packages and assets keep their own licences.

## Acknowledgments

- Unity Technologies, for Unity 6, the XR Interaction Toolkit and the VFX Graph

## Contact

Please open an issue in this repository for questions and bug reports.  Alternatively you can reach me at my AAU affiliated email: flmi@create.aau.dk

---

**Last updated:** 2026-11-02 · **Unity version:** 6000.5.2f1 · **Status:** active development
