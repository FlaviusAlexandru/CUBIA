using System.Collections.Generic;
using DataViz.Flow;
using DataViz.Networking;
using UnityEngine;
using UnityEngine.UI;
using Col = DataViz.Study.SelfTestKit.Col;
using Model = DataViz.MenuUI.LayersPanelModel;

namespace DataViz.Study
{
    /// <summary>
    /// Pilot 2 follow-ups on the general CUBIA branch. PF-01: a per-layer point size that does not
    /// change the active layer, logged with the resized layer's name and undoable on the dashboard.
    /// PF-02: plot text 1.5x, dark menus (MenuTheme.Dark / RemapToDark) and dark studio colours.
    /// PF-03: grid planes on the cube faces behind the data (GridFaces.FarSides).
    /// PF-04: the Mole glyph in the 4x2 point atlas.
    /// </summary>
    public static class PilotFollowUpSelfTests
    {
        const string k_Moles = "pf_moles.cdataset";
        const string k_Movement = "pf_movement.cdataset";

        static DatasetColumnar Moles() => SelfTestKit.Dataset(k_Moles,
            Col.Num("MoleX", 0.2f, 0.8f, 1.2f),
            Col.Num("MoleY", 1.1f, 1.3f, 1.5f),
            Col.Num("SpawnTime", 0.5f, 1.5f, 2.5f));

        static DatasetColumnar Movement() => SelfTestKit.Dataset(k_Movement,
            Col.Num("LaserX", 0f, 0.5f, 1f, 1.5f),
            Col.Num("LaserY", 1f, 1.2f, 1.4f, 1.6f),
            Col.Num("Time", 0f, 1f, 2f, 3f));

        // ------------------------------------------------------------------ PF-01

        [SelfTest("PF-01: size steps")]
        static void SizeSteps(SelfTestTally t)
        {
            float max = MultiplayerScatterplotManager.MaxPointSize;
            t.Near("up from 20%", 0.25f * max, Model.StepPointSize(0.2f * max, +1));
            t.Near("down from 20%", 0.16f * max, Model.StepPointSize(0.2f * max, -1));
            t.Near("down from between steps", 0.2f * max, Model.StepPointSize(0.21f * max, -1));
            t.Near("up from between steps", 0.25f * max, Model.StepPointSize(0.21f * max, +1));
            t.Near("top stays", max, Model.StepPointSize(max, +1));
            t.Near("bottom stays", 0.01f * max, Model.StepPointSize(0.01f * max, -1));
            t.Near("below bottom: up to first step", 0.01f * max, Model.StepPointSize(0f, +1));
            t.Near("direction 0 stays", 0.123f, Model.StepPointSize(0.123f, 0));
            t.Eq("percent text", "20%", Model.SizePercentText(0.2f * max));
            t.Eq("log text", "0.025", DashboardLayersSection.SizeText(0.025f));
        }

        [SelfTest("PF-01: resize a non-active layer")]
        static void ResizeNonActive(SelfTestTally t)
        {
            DatasetColumnar moles = Moles(), movement = Movement();
            SelfTestKit.WithManager(m =>
            {
                m.RequestLoadDatasetRpc(k_Moles);
                string molesId = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(k_Movement);
                string movementId = m.ActiveLayerId;
                t.True("movement active", movementId != molesId);
                float movementSize = m.Stack.Find(movementId).PointSize;
                float activeMapSize = m.PointSize;

                var events = new List<string>();
                m.OnLayersChanged += () => events.Add("L");
                m.OnPlotSettingsChanged += () => events.Add("P");
                PlotSnapshot before = PlotSnapshot.Capture(m);

                float bigger = Model.StepPointSize(m.Stack.Find(molesId).PointSize, +1);
                if (bigger == m.Stack.Find(molesId).PointSize) bigger = Model.StepPointSize(bigger, -1); // at the top: go down instead
                float oldSize = m.Stack.Find(molesId).PointSize;
                m.RequestSetLayerPointSizeRpc(molesId, bigger);

                t.Near("moles resized", bigger, m.Stack.Find(molesId).PointSize);
                t.Near("movement unchanged", movementSize, m.Stack.Find(movementId).PointSize);
                t.Eq("active layer unchanged", movementId, m.ActiveLayerId);
                t.Near("Map slider value (active layer) unchanged", activeMapSize, m.PointSize);
                t.Eq("events", "L,P", string.Join(",", events));

                List<LayerChange> rows = PlotStateDiff.DescribeStack(before, PlotSnapshot.Capture(m), DatasetRegistry.Get);
                t.Eq("one log row", 1, rows.Count);
                if (rows.Count == 1)
                {
                    t.Eq("row action", StudyActions.SetPointSize, rows[0].Action);
                    t.Eq("row layer", m.Stack.Find(molesId).Name, rows[0].Layer);
                    t.Eq("row old (as the dashboard writes it)", DashboardLayersSection.SizeText(oldSize), rows[0].OldValue);
                    t.Eq("row new (as the dashboard writes it)", DashboardLayersSection.SizeText(bigger), rows[0].NewValue);
                }

                events.Clear();
                m.RequestSetLayerPointSizeRpc(molesId, bigger);
                t.Eq("same size: no events", 0, events.Count);
                m.RequestSetLayerPointSizeRpc(molesId, float.NaN);
                t.Near("NaN ignored", bigger, m.Stack.Find(molesId).PointSize);
                m.RequestSetLayerPointSizeRpc(molesId, 5f);
                t.Near("clamped to MaxPointSize", MultiplayerScatterplotManager.MaxPointSize, m.Stack.Find(molesId).PointSize);
                m.RequestSetLayerPointSizeRpc("L99", 0.05f);
                t.Near("unknown id dropped", MultiplayerScatterplotManager.MaxPointSize, m.Stack.Find(molesId).PointSize);

                // The forwarded op (client -> host) lands on the named layer too.
                t.Eq("network op applied", true,
                    NetworkScatterplotState.ApplyRequest(m, new PlotRequest(PlotOp.SetLayerPointSize, floatArg: 0.03f, layer: molesId)));
                t.Near("network op: moles", 0.03f, m.Stack.Find(molesId).PointSize);
                t.Eq("network op: active unchanged", movementId, m.ActiveLayerId);

                // Computed layers draw lines: refused, nothing changes.
                m.RequestAddComputedLayerRpc(movementId, ComputedLayerKind.Path);
                string computedId = m.ActiveLayerId;
                float computedSize = m.Stack.Find(computedId).PointSize;
                string message = null;
                m.OnPlotMessage += (text, kind) => message = text;
                m.RequestSetLayerPointSizeRpc(computedId, 0.07f);
                t.Near("computed refused", computedSize, m.Stack.Find(computedId).PointSize);
                t.True("computed: refusal message", !string.IsNullOrEmpty(message));
            }, (k_Moles, moles), (k_Movement, movement));
        }

        [SelfTest("PF-01: dashboard undo of a layer size")]
        static void UndoSize(SelfTestTally t)
        {
            DatasetColumnar moles = Moles(), movement = Movement();
            SelfTestKit.WithManager(m =>
            {
                m.RequestLoadDatasetRpc(k_Moles);
                string molesId = m.ActiveLayerId;
                m.RequestAddDatasetLayerRpc(k_Movement);
                float oldSize = m.Stack.Find(molesId).PointSize;

                var history = new DashboardUndoHistory(50);
                bool Restore(PlotSnapshot s)
                {
                    m.ApplyNetworkSnapshot(s, s.TimeScrub);
                    return true;
                }
                history.Push(PlotSnapshot.Capture(m));
                m.RequestSetLayerPointSizeRpc(molesId, 0.05f);
                t.True("undo", history.TryUndo(PlotSnapshot.Capture(m), Restore));
                t.Near("undo restores the size", oldSize, m.Stack.Find(molesId).PointSize);
                t.True("redo", history.TryRedo(PlotSnapshot.Capture(m), Restore));
                t.Near("redo sets it again", 0.05f, m.Stack.Find(molesId).PointSize);
            }, (k_Moles, moles), (k_Movement, movement));
        }

        [SelfTest("PF-01: layer list rows show the size")]
        static void RowSize(SelfTestTally t)
        {
            var stack = new LayerStack();
            DatasetColumnar ds = Moles();
            PlotLayer a = stack.AddDataset(ds.Name, ds);
            a.PointSize = 0.2f * MultiplayerScatterplotManager.MaxPointSize;
            PlotLayer path = stack.AddComputed(a, ComputedLayerKind.Path);

            Model.View v = Model.Build(stack, null, null, 0);
            t.Eq("point row: stepper", true, v.Rows[0].ShowSize);
            t.Eq("point row: text", "20%", v.Rows[0].SizeText);
            t.Eq("point row: can shrink", true, v.Rows[0].CanShrink);
            t.Eq("point row: can grow", true, v.Rows[0].CanGrow);
            t.Eq("computed row: no stepper", false, v.Rows[1].ShowSize);

            a.PointSize = MultiplayerScatterplotManager.MaxPointSize;
            t.Eq("at max: cannot grow", false, Model.Build(stack, null, null, 0).Rows[0].CanGrow);

            int s0 = Model.Signature(stack, null);
            a.PointSize = 0.05f;
            t.True("signature follows the size", Model.Signature(stack, null) != s0);
        }
        // ------------------------------------------------------------------ PF-02

        static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        [SelfTest("PF-02: plot text scale")]
        static void TextScale(SelfTestTally t)
        {
            t.Near("scale", 1.5f, PlotText.Scale);
            t.Near("axis title 7 -> 10.5", 10.5f, PlotText.Size(7f));
            t.Near("tick 14 -> 21", 21f, PlotText.Size(14f));
        }

        [SelfTest("PF-02: dark menu theme")]
        static void DarkTheme(SelfTestTally t)
        {
            MenuTheme light = MenuTheme.LoadLight();
            t.True("edit mode: Load is the light asset", ReferenceEquals(light, MenuTheme.Load()));
            MenuTheme dark = MenuTheme.Dark(light);
            try
            {
                t.True("dark panel", Luma(dark.PanelColor) < 0.2f);
                t.True("light text", Luma(dark.Text) > 0.8f);
                t.True("light muted text", Luma(dark.TextMuted) > 0.5f);
                t.True("dark button", Luma(dark.ButtonColor) < 0.3f);
                t.True("dark active tile", Luma(dark.TileActiveColor) < 0.35f);
                t.True("accent kept", dark.Accent == light.Accent);
                t.True("font kept", dark.Font == light.Font);
                t.Near("body size kept", light.BodyFontSize, dark.BodyFontSize);
                t.True("asset untouched", Luma(light.PanelColor) > 0.8f && Luma(light.Text) < 0.2f);

                ColorBlock lb = light.ButtonColors();
                t.True("light buttons as before", lb.normalColor == Color.white && lb.selectedColor == Color.white &&
                    lb.highlightedColor == new Color(0.90f, 0.93f, 0.98f, 1f) && lb.pressedColor == new Color(0.78f, 0.83f, 0.92f, 1f) &&
                    Mathf.Approximately(lb.colorMultiplier, 1f));
                ColorBlock db = dark.ButtonColors();
                t.Near("dark buttons: normal shows the fill as is", 1f, db.normalColor.r * db.colorMultiplier, 1e-4f);
                t.True("dark buttons: highlight brightens", db.highlightedColor.r * db.colorMultiplier > 1.2f);

                Color32 round(Color c) => c; // the scene stores 8-bit colours
                t.True("panel face", MenuTheme.RemapToDark((Color)round(light.PanelColor), light, dark) == dark.PanelColor);
                t.True("active tile", MenuTheme.RemapToDark((Color)round(light.TileActiveColor), light, dark) == dark.TileActiveColor);
                t.True("button fill", MenuTheme.RemapToDark(light.ButtonColor, light, dark) == dark.ButtonColor);
                Color hint = new Color(light.Text.r, light.Text.g, light.Text.b, 0.8f);
                Color hintDark = MenuTheme.RemapToDark(hint, light, dark);
                t.True("hint text: light, alpha kept", Luma(hintDark) > 0.8f && Mathf.Abs(hintDark.a - 0.8f) < 1e-4f);
                Color divider = MenuTheme.RemapToDark((Color)round(light.Divider), light, dark);
                t.True("divider: light at its alpha", Luma(divider) > 0.8f && Mathf.Abs(divider.a - ((Color)round(light.Divider)).a) < 1e-4f);
                t.True("muted heading", MenuTheme.RemapToDark((Color)round(light.TextMuted), light, dark) == dark.TextMuted);
                t.True("icon tint", MenuTheme.RemapToDark((Color)round(light.IconTint), light, dark) == dark.IconTint);
                t.True("accent stays", MenuTheme.RemapToDark(light.Accent, light, dark) == light.Accent);
                t.True("plain white stays", MenuTheme.RemapToDark(Color.white, light, dark) == Color.white);
                var widget = new Color32(0x2E, 0x2E, 0x2E, 0xFF);
                t.True("dark widget stays", MenuTheme.RemapToDark(widget, light, dark) == (Color)widget);
                // Idempotent: the bootstrap remaps again, and runtime-built menus are dark already.
                foreach (Color c in new[] { dark.PanelColor, dark.PanelOutline, dark.Text, dark.TextMuted, dark.IconTint,
                                            dark.TileActiveColor, dark.Divider, dark.ButtonColor, dark.Accent })
                {
                    Color stored = (Color)round(c);
                    t.True("already dark stays " + ColorUtility.ToHtmlStringRGBA(c), MenuTheme.RemapToDark(stored, light, dark) == stored);
                }
            }
            finally
            {
                Object.DestroyImmediate(dark);
            }
        }

        [SelfTest("PF-02: dark studio colours")]
        static void StudioColours(SelfTestTally t)
        {
            t.True("sky dark", Luma(DarkStudio.SkyZenith) < 0.05f && Luma(DarkStudio.SkyHorizon) < 0.1f);
            t.True("floor dark", Luma(DarkStudio.FloorBase) < 0.1f);
            t.True("table dark", Luma(DarkStudio.TableTop) < 0.2f);
            t.True("grid lighter than floor", Luma(DarkStudio.FloorMinor) > Luma(DarkStudio.FloorBase) + 0.15f &&
                Luma(DarkStudio.FloorMajor) > Luma(DarkStudio.FloorMinor));
            t.True("depth cues light", Luma(DarkStudio.Ring) > 0.8f && Luma(DarkStudio.Post) > 0.8f && Luma(DarkStudio.Footprint) > 0.75f);
        }
        // ------------------------------------------------------------------ PF-03

        static string Sides(Vector3Int v) => $"{v.x},{v.y},{v.z}";

        [SelfTest("PF-03: grid faces behind the data")]
        static void GridFacesBehind(SelfTestTally t)
        {
            // SampleScene's start position in the axes space of the 1 m cube: in front of the z = 0
            // face, centred in x, eye above the cube's middle.
            t.Eq("start position", "1,0,1", Sides(GridFaces.FarSides(new Vector3(0.45f, 0.67f, -1.55f), 1f)));
            t.Eq("standing taller", "1,0,1", Sides(GridFaces.FarSides(new Vector3(0.45f, 0.95f, -1.55f), 1f)));
            t.Eq("from the far corner, below", "0,1,0", Sides(GridFaces.FarSides(new Vector3(1.6f, -0.3f, 2.4f), 1f)));
            t.Eq("halfway keeps the default", Sides(GridFaces.Default), Sides(GridFaces.FarSides(new Vector3(0.5f, 0.5f, 0.5f), 1f)));
            t.Eq("scaled cube", "0,0,1", Sides(GridFaces.FarSides(new Vector3(1.2f, 1.5f, 0.9f), 2f)));
            t.Eq("default: back wall, far side, floor", "1,0,1", Sides(GridFaces.Default));
        }

        // ------------------------------------------------------------------ PF-04

        const string k_AtlasPath = "Assets/Materials/glyph_atlas.png";
        const string k_PointGraphPath = "Assets/Visual Effects/Point Particle VFX Graph/PointParticles.vfx";
        const int k_Cell = 64;

        // SHA-256 of each 64x64 cell of the six glyphs in the old 192x128 (3x2) atlas, RGBA bytes,
        // rows from the top of the image. The 4x2 atlas must hold the same bytes in frames 0..5.
        static readonly string[] k_OldCellHashes =
        {
            "724c0c3d8c245f3d368516c53224bc92cda7353d129a2eca4a385ab25c6d4cfd", // Circle
            "fbfeab091cb32cc0e3fc1cadd16d295bdcf2f53310bce8fbcbbc0fb95e76f60e", // Square
            "3be263e567e5bfe53db2155cd9bac3ede759255af9c7024ed87e8e3a0045cfbe", // Triangle
            "b3ff8ffea5eeeac5dfeae8349406c42f1dc14548a4f0918d24454d772ae12e0a", // Diamond
            "24f35bbce6b3dc8a39d8c5f9b574036ae1f09b525c29e4623567ddbe3067b806", // Cross
            "a373cbb90506e393e82951d449a6438c19022e84f510095a106cce8cc40c2715", // Star
        };

        /// <summary>Frame i of the 4-column atlas as RGBA bytes, rows from the top (the VFX
        /// flipbook puts frame 0 at the top left).</summary>
        static byte[] AtlasCell(Color32[] pixels, int width, int height, int frame)
        {
            int col = frame % 4, row = frame / 4;
            var bytes = new byte[k_Cell * k_Cell * 4];
            int k = 0;
            for (int yTop = 0; yTop < k_Cell; yTop++)
            {
                int y = height - 1 - (row * k_Cell + yTop);   // Unity rows run bottom-up
                for (int x = col * k_Cell; x < (col + 1) * k_Cell; x++)
                {
                    Color32 c = pixels[y * width + x];
                    bytes[k++] = c.r; bytes[k++] = c.g; bytes[k++] = c.b; bytes[k++] = c.a;
                }
            }
            return bytes;
        }

        static int Opaque(byte[] cell)
        {
            int n = 0;
            for (int i = 3; i < cell.Length; i += 4) if (cell[i] > 0) n++;
            return n;
        }

        [SelfTest("PF-04: mole glyph in the point atlas")]
        static void MoleGlyph(SelfTestTally t)
        {
            string[] names = ScatterplotUI.k_GlyphShapeNames;
            t.Eq("seven glyph names", 7, names.Length);
            t.Eq("glyph 6", "Mole", names.Length > 6 ? names[6] : "");
            t.Eq("glyph 5 unchanged", "Star", names.Length > 5 ? names[5] : "");

            string graph = System.IO.File.ReadAllText(k_PointGraphPath);
            t.True("point graph flipbook is 4x2", graph.Contains("m_SerializableObject: '{\"x\":4,\"y\":2}'") &&
                                                 !graph.Contains("{\"x\":3,\"y\":2}"));

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                bool loaded = tex.LoadImage(System.IO.File.ReadAllBytes(k_AtlasPath));
                t.True("atlas png loads", loaded);
                t.Eq("atlas size", "256x128", tex.width + "x" + tex.height);
                if (!loaded || tex.width != 4 * k_Cell || tex.height != 2 * k_Cell) return;
                Color32[] pixels = tex.GetPixels32();
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    for (int i = 0; i < k_OldCellHashes.Length; i++)
                    {
                        byte[] hash = sha.ComputeHash(AtlasCell(pixels, tex.width, tex.height, i));
                        string hex = System.BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                        t.Eq("old glyph cell " + i + " (" + names[i] + ") byte-identical", k_OldCellHashes[i], hex);
                    }
                }
                t.True("mole cell 6 drawn", Opaque(AtlasCell(pixels, tex.width, tex.height, 6)) > 500);
                t.Eq("cell 7 empty", 0, Opaque(AtlasCell(pixels, tex.width, tex.height, 7)));
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }

            var imported = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(k_AtlasPath);
            t.Eq("imported atlas size", "256x128", imported != null ? imported.width + "x" + imported.height : "missing");
        }
    }
}
