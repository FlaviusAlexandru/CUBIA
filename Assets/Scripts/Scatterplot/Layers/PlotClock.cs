using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// The plot's one playback clock (LFR-23). TimeScrub 0..1 maps onto [min, max], the union of the
    /// ranges of every layer's time column; each layer compares its own time column against the
    /// same clock time. With one layer this is that column's range, as before layers.
    /// </summary>
    public static class PlotClock
    {
        /// <summary>
        /// Union of ColumnRange.Get over the numeric time columns of the point layers that have a
        /// dataset and are not LocalUnavailable (hidden layers count, so hiding never moves the
        /// clock). False when no layer contributes.
        /// </summary>
        public static bool Range(IReadOnlyList<PlotLayer> layers, out float min, out float max)
        {
            min = 0f;
            max = 0f;
            if (layers == null)
                return false;

            bool any = false;
            foreach (PlotLayer layer in layers)
            {
                if (!Contributes(layer))
                    continue;
                if (!ColumnRange.Get(layer.Dataset, layer.TimeColumnIndex, out float lo, out float hi))
                    continue;
                if (!any || lo < min) min = lo;
                if (!any || hi > max) max = hi;
                any = true;
            }
            return any;
        }

        /// <summary>True when there is at least one contributing time column and every one is seconds-like (IsSecondsLike).</summary>
        public static bool SecondsLike(IReadOnlyList<PlotLayer> layers)
        {
            if (layers == null)
                return false;

            bool any = false;
            foreach (PlotLayer layer in layers)
            {
                if (!Contributes(layer))
                    continue;
                DatasetColumn column = layer.Dataset.GetColumn(layer.TimeColumnIndex);
                if (!IsSecondsLike(column))
                    return false;
                any = true;
            }
            return any;
        }

        /// <summary>Clock time for a scrub value: Mathf.Lerp(min, max, scrub) (scrub clamped to 0..1).</summary>
        public static float TimeAt(float scrub, float min, float max) => Mathf.Lerp(min, max, scrub);

        /// <summary>
        /// DateTime columns are stored as seconds (preprocess_csv_columnar.py). A numeric column
        /// counts when its name contains "time", unless the name says milliseconds. (Moved from the manager.)
        /// </summary>
        public static bool IsSecondsLike(DatasetColumn column)
        {
            if (column == null)
                return false;
            if (column.Type == DataValueType.DateTime)
                return true;

            string name = column.Name;
            if (string.IsNullOrEmpty(name) || name.IndexOf("time", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            bool milliseconds =
                name.IndexOf("milli", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("msec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("(ms)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("[ms]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.EndsWith("ms", StringComparison.OrdinalIgnoreCase);
            return !milliseconds;
        }

        static bool Contributes(PlotLayer layer)
        {
            if (layer == null || !layer.IsPointLayer || layer.LocalUnavailable || layer.Dataset == null)
                return false;
            DatasetColumn column = layer.Dataset.GetColumn(layer.TimeColumnIndex);
            return column != null && column.IsNumeric;
        }
    }
}
