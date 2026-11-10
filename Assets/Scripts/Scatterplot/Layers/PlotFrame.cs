using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// One axis scale: maps a data value to 0..1 along the cube edge. Numeric: today's formula,
    /// Clamp01((v - Min) / (Max - Min)), and 0 when |Max - Min| &lt; 1e-5. Categorical: a category
    /// at union index i sits at i / (Categories.Length - 1), 0 with one category or none.
    /// Not mapped: every value sits at 0 (what an unset axis column always did).
    /// </summary>
    public struct AxisScale
    {
        public bool Mapped, Categorical;
        public float Min, Max;
        public string[] Categories;   // categorical only: ordered union of the category tables

        public static AxisScale Numeric(float min, float max) => new AxisScale { Mapped = true, Min = min, Max = max };

        public static AxisScale Category(string[] categories) =>
            new AxisScale { Mapped = true, Categorical = true, Categories = categories ?? Array.Empty<string>() };

        /// <summary>Numeric value → 0..1. On a categorical scale value is read as a union category index.</summary>
        public float Normalise(float value)
        {
            if (!Mapped)
                return 0f;
            if (Categorical)
                return NormaliseCategoryIndex((int)value);

            float range = Max - Min;
            if (Mathf.Abs(range) < 0.00001f)
                return 0f;
            return Mathf.Clamp01((value - Min) / range);
        }

        /// <summary>Category label → 0..1; 0 for a label not in Categories.</summary>
        public float NormaliseCategory(string label) => NormaliseCategoryIndex(IndexOfCategory(label));

        /// <summary>Union category index → 0..1 (the same division DatasetColumnar.GetNormalizedValue does).</summary>
        public float NormaliseCategoryIndex(int index)
        {
            if (!Mapped || !Categorical || Categories == null || Categories.Length <= 1 || index < 0)
                return 0f;
            return (float)index / (Categories.Length - 1);
        }

        public int IndexOfCategory(string label) =>
            Categories == null || label == null ? -1 : Array.IndexOf(Categories, label);

        /// <summary>The data value at a 0..1 position (numeric scales; ticks).</summary>
        public float ValueAt(float unit) => Min + unit * (Max - Min);

        public bool SameAs(AxisScale o)
        {
            if (Mapped != o.Mapped || Categorical != o.Categorical)
                return false;
            if (!Mapped)
                return true;
            if (!Categorical)
                return Min.Equals(o.Min) && Max.Equals(o.Max);
            int n = Categories?.Length ?? 0;
            if (n != (o.Categories?.Length ?? 0))
                return false;
            for (int i = 0; i < n; i++)
                if (Categories[i] != o.Categories[i])
                    return false;
            return true;
        }
    }

    /// <summary>The three scales one layer is placed with (shared ones, or its own when Normalised).</summary>
    public struct AxisFrame
    {
        public AxisScale X, Y, Z;

        /// <summary>Data values → unit cube (0..1 each). Categorical axes take union category indices.</summary>
        public Vector3 ToCube(float x, float y, float z) => new Vector3(X.Normalise(x), Y.Normalise(y), Z.Normalise(z));

        /// <summary>0 = X, 1 = Y, 2 = Z.</summary>
        public AxisScale Get(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;

        public void Set(int axis, AxisScale scale)
        {
            if (axis == 0) X = scale;
            else if (axis == 1) Y = scale;
            else Z = scale;
        }
    }

    /// <summary>The plot's shared axis bounds (LFR-08): what aligned layers are placed by. See LayerBounds.Compute.</summary>
    public struct AxisBounds
    {
        public AxisScale X, Y, Z;

        public AxisScale Get(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;

        public void Set(int axis, AxisScale scale)
        {
            if (axis == 0) X = scale;
            else if (axis == 1) Y = scale;
            else Z = scale;
        }

        public bool SameAs(AxisBounds o) => X.SameAs(o.X) && Y.SameAs(o.Y) && Z.SameAs(o.Z);
    }

    /// <summary>What happened to one layer in the last build (budget, skips). Warning is null when there is nothing to say.</summary>
    public struct LayerDrawInfo
    {
        public int Rows;                    // dataset rows
        public int Drawable;                // rows the layer would draw before the budget
        public int Drawn;                   // points put in the buffer
        public int SkippedNoAxisValue;      // LFR-11
        public int SkippedNoEncodingValue;  // LFR-11 (colour or mapped size cell empty)
        public int Stride;                  // 1 = every drawable row; n = 1 of every n (LFR-06)
        public string Warning;
    }

    /// <summary>One layer's part of a PlotFrame, in stack order.</summary>
    public sealed class LayerFrame
    {
        public PlotLayer Layer;
        public DatasetColumnar Dataset;
        public AxisFrame Axes;
        public int BufferStart, BufferCount;   // slice of the concatenated buffers; 0 for hidden, unavailable or computed layers
        public LayerDrawInfo Info;
    }

    /// <summary>
    /// What the visualizer built last (ScatterplotVisualizer.LastFrame, OnFrameBuilt). Overlays
    /// (lines, labels, legend) read this instead of the manager so they agree with the points.
    /// </summary>
    public sealed class PlotFrame
    {
        public int StackVersion;
        public AxisBounds Bounds;
        public bool ClockActive;
        public float ClockMin, ClockMax, ClockTime;
        public int Budget, TotalPoints;
        public readonly List<LayerFrame> Layers = new();   // one per layer, stack order

        /// <summary>The layer whose buffer slice holds bufferIndex, or null.</summary>
        public LayerFrame FrameOfPoint(int bufferIndex)
        {
            foreach (LayerFrame f in Layers)
            {
                if (f.BufferCount > 0 && bufferIndex >= f.BufferStart && bufferIndex < f.BufferStart + f.BufferCount)
                    return f;
            }
            return null;
        }

        public LayerFrame Find(string layerId)
        {
            foreach (LayerFrame f in Layers)
            {
                if (f.Layer != null && f.Layer.Id == layerId)
                    return f;
            }
            return null;
        }
    }

    /// <summary>Visualizer settings LayerPointBuilder needs (it has no visualizer reference).</summary>
    public struct LayerBuildSettings
    {
        public Vector3 Origin;          // visualizer-local position of the cube's (0,0,0) corner
        public float AxisLength;
        public float FilteredOutAlpha, TimeOutsideAlpha, TimeTrailFraction;
        public IReadOnlyList<Vector2> GlyphPreviewHidden;   // glyph range editor overlap preview (x = low, y = high)
        public string GlyphPreviewLayerId;                  // the only layer the preview applies to (the edit layer)
    }

    /// <summary>The concatenated per-point lists handed to the renderer. All lists stay parallel.</summary>
    public sealed class PointBuffers
    {
        public List<Vector3> Positions = new();
        public List<Color> Colors = new();
        public List<float> Sizes = new();
        public List<int> Glyphs = new();
        public List<int> Rows = new();          // dataset row of each point, in its layer's dataset
        public List<bool> InTimeWindow = new(); // true = full alpha and hoverable

        public int Count => Positions.Count;

        public void Clear()
        {
            Positions.Clear();
            Colors.Clear();
            Sizes.Clear();
            Glyphs.Clear();
            Rows.Clear();
            InTimeWindow.Clear();
        }
    }
}
