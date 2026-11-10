using System;
using System.Collections.Generic;
using System.IO;

namespace DataViz
{
    /// <summary>Pure rules about layers: mapping compatibility (LFR-12), default names and mappings (LFR-07).</summary>
    public static class LayerRules
    {
        static readonly string[] k_AxisNames = { "X", "Y", "Z" };

        public static string AxisName(int axis) => axis >= 0 && axis < 3 ? k_AxisNames[axis] : "?";

        /// <summary>The layer's column index on an axis (0 X, 1 Y, 2 Z), -1 for any other axis.</summary>
        public static int AxisColumn(PlotLayer layer, int axis)
        {
            if (layer == null)
                return -1;
            switch (axis)
            {
                case 0: return layer.XColumnIndex;
                case 1: return layer.YColumnIndex;
                case 2: return layer.ZColumnIndex;
                default: return -1;
            }
        }

        public static void SetAxisColumn(PlotLayer layer, int axis, int column)
        {
            if (layer == null)
                return;
            switch (axis)
            {
                case 0: layer.XColumnIndex = column; break;
                case 1: layer.YColumnIndex = column; break;
                case 2: layer.ZColumnIndex = column; break;
            }
        }

        /// <summary>
        /// LFR-12: false, with a message, when columnIndex is categorical and another aligned point
        /// layer maps a numeric column to the same axis, or the reverse. A normalised layer, an
        /// unknown or untyped column, and a single-layer stack are always allowed.
        /// </summary>
        public static bool CanMapAxis(LayerStack stack, PlotLayer layer, int axis, int columnIndex, out string message)
        {
            message = null;
            if (stack == null || layer == null || layer.Normalised || !layer.IsPointLayer)
                return true;

            DatasetColumn column = layer.Dataset?.GetColumn(columnIndex);
            return column == null || CompatibleWithOthers(stack, layer, axis, column, out message);
        }

        /// <summary>LFR-12 for switching Normalise off: every axis of the layer must agree in type with the other aligned layers.</summary>
        public static bool CanSetAligned(LayerStack stack, PlotLayer layer, out string message)
        {
            message = null;
            if (stack == null || layer == null || !layer.IsPointLayer)
                return true;

            for (int axis = 0; axis < 3; axis++)
            {
                DatasetColumn column = layer.Dataset?.GetColumn(AxisColumn(layer, axis));
                if (column != null && !CompatibleWithOthers(stack, layer, axis, column, out message))
                    return false;
            }
            return true;
        }

        static bool CompatibleWithOthers(LayerStack stack, PlotLayer layer, int axis, DatasetColumn column, out string message)
        {
            message = null;
            bool categorical = column.IsCategorical;
            if (!categorical && !column.IsNumeric)
                return true;

            foreach (PlotLayer other in stack.Layers)
            {
                if (other == layer || !other.IsPointLayer || other.Normalised || other.LocalUnavailable)
                    continue;

                DatasetColumn otherColumn = other.Dataset?.GetColumn(AxisColumn(other, axis));
                if (otherColumn == null || (!otherColumn.IsCategorical && !otherColumn.IsNumeric))
                    continue;

                if (otherColumn.IsCategorical != categorical)
                {
                    message = $"{column.Name} is {(categorical ? "categorical" : "numeric")}, but layer \"{other.Name}\" " +
                              $"shows {otherColumn.Name} as {(categorical ? "numbers" : "categories")} on {AxisName(axis)}. " +
                              "Normalise one of the layers to map them both.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Default name of a new layer: Dataset → file name without extension; Subset → "&lt;source&gt;:
        /// &lt;label&gt;" (or "&lt;source&gt; subset"); Computed → "&lt;Kind&gt; of &lt;source&gt;". Made unique in the stack.
        /// </summary>
        public static string DefaultName(LayerStack stack, PlotLayer layer)
        {
            if (layer == null)
                return "";

            PlotLayer source = stack?.Find(layer.SourceLayerId);
            string sourceName = source != null ? source.Name : "source";
            string name;
            switch (layer.Kind)
            {
                case LayerKind.Subset:
                    string label = layer.Filters.Count == 1 ? layer.Filters[0].Label : "";
                    name = string.IsNullOrEmpty(label) ? sourceName + " subset" : sourceName + ": " + label;
                    break;
                case LayerKind.Computed:
                    name = ComputedKindName(layer.ComputedKind) + " of " + sourceName;
                    break;
                default:
                    name = string.IsNullOrEmpty(layer.DatasetName) ? "Layer" : Path.GetFileNameWithoutExtension(layer.DatasetName);
                    break;
            }
            return UniqueName(stack, name);
        }

        /// <summary>name, or "name 2", "name 3", … whichever no layer in the stack uses yet.</summary>
        public static string UniqueName(LayerStack stack, string name)
        {
            name = string.IsNullOrEmpty(name) ? "Layer" : name;
            if (stack == null || !NameUsed(stack, name))
                return name;
            for (int n = 2; ; n++)
            {
                string candidate = name + " " + n;
                if (!NameUsed(stack, candidate))
                    return candidate;
            }
        }

        static bool NameUsed(LayerStack stack, string name)
        {
            foreach (PlotLayer layer in stack.Layers)
            {
                if (string.Equals(layer.Name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static string ComputedKindName(ComputedLayerKind kind)
        {
            switch (kind)
            {
                case ComputedLayerKind.Trendline: return "Trendline";
                case ComputedLayerKind.Path: return "Path";
                case ComputedLayerKind.Mean: return "Mean";
                case ComputedLayerKind.Reference: return "Reference";
                case ComputedLayerKind.SmoothedPath: return "Smoothed path";
                default: return "Computed";
            }
        }

        /// <summary>
        /// LFR-07: each axis of newLayer gets the column whose name equals baseLayer's column on that
        /// axis, where newLayer's dataset has one, else the first numeric column not used on another
        /// axis (else 0). The time column is matched by name too (else none). PointSize and SizeMapped
        /// come from baseLayer. Without a base layer or datasets: X/Y/Z 0/1/2 clamped to the column count.
        /// </summary>
        public static void ApplyDefaultMapping(PlotLayer newLayer, PlotLayer baseLayer)
        {
            if (newLayer == null)
                return;

            DatasetColumnar ds = newLayer.Dataset;
            int columnCount = ds?.ColumnCount ?? 3;
            int maxIndex = Math.Max(0, columnCount - 1);

            if (baseLayer != null)
            {
                newLayer.PointSize = baseLayer.PointSize;
                newLayer.SizeMapped = baseLayer.SizeMapped;
            }

            DatasetColumnar baseDs = baseLayer?.Dataset;
            if (ds == null || baseDs == null || baseLayer == newLayer)
            {
                newLayer.XColumnIndex = Math.Min(0, maxIndex);
                newLayer.YColumnIndex = Math.Min(1, maxIndex);
                newLayer.ZColumnIndex = Math.Min(2, maxIndex);
                newLayer.TimeColumnIndex = -1;
                return;
            }

            var picked = new int[3];
            var used = new HashSet<int>();
            for (int axis = 0; axis < 3; axis++)
            {
                picked[axis] = SameNameColumn(ds, baseDs, AxisColumn(baseLayer, axis));
                if (picked[axis] >= 0)
                    used.Add(picked[axis]);
            }

            for (int axis = 0; axis < 3; axis++)
            {
                if (picked[axis] >= 0)
                    continue;
                int choice = -1;
                for (int c = 0; c < ds.ColumnCount && choice < 0; c++)
                {
                    DatasetColumn column = ds.GetColumn(c);
                    if (column != null && column.IsNumeric && !used.Contains(c))
                        choice = c;
                }
                if (choice < 0)
                    choice = Math.Min(axis, maxIndex);
                picked[axis] = choice;
                used.Add(choice);
            }

            newLayer.XColumnIndex = picked[0];
            newLayer.YColumnIndex = picked[1];
            newLayer.ZColumnIndex = picked[2];
            newLayer.TimeColumnIndex = SameNameColumn(ds, baseDs, baseLayer.TimeColumnIndex);
        }

        static int SameNameColumn(DatasetColumnar ds, DatasetColumnar baseDs, int baseColumn)
        {
            string name = baseDs.GetColumn(baseColumn)?.Name;
            return string.IsNullOrEmpty(name) ? -1 : ds.GetColumnIndex(name);
        }

        /// <summary>The axis holding source's time column (0 X, 1 Y, 2 Z), else 2 (Z).</summary>
        public static int DefaultDriverAxis(PlotLayer source)
        {
            if (source == null || source.TimeColumnIndex < 0)
                return 2;
            for (int axis = 0; axis < 3; axis++)
            {
                if (AxisColumn(source, axis) == source.TimeColumnIndex)
                    return axis;
            }
            return 2;
        }
    }
}
