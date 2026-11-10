using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DataViz
{
    /// <summary>
    /// Value range of a numeric (or DateTime) column, used for shared axis bounds, the playback
    /// clock and normalised layers.
    ///
    /// Two modes, switched by ValidRowsOnly:
    /// - true (default, LFR-11): min and max over the rows where HasValue is true, scanned once and
    ///   cached per (dataset instance, column). A column without empty cells gives exactly its
    ///   MinValue/MaxValue; a sparse one no longer starts at the 0 stored for an empty cell
    ///   (accepted change, DECISIONS.md E5).
    /// - false (WP1 legacy pinning): DatasetColumn.MinValue/MaxValue, what positions and the time
    ///   window used before layers, including that placeholder 0. Kept for comparison tests.
    /// </summary>
    public static class ColumnRange
    {
        /// <summary>See the class comment.</summary>
        public static bool ValidRowsOnly = true;

        struct Range { public bool Valid; public float Min, Max; }

        static readonly ConditionalWeakTable<DatasetColumnar, Dictionary<int, Range>> s_Cache = new();

        /// <summary>
        /// False when the column does not exist, is not numeric, or has no value at all; min and max
        /// are then 0.
        /// </summary>
        public static bool Get(DatasetColumnar ds, int column, out float min, out float max)
        {
            min = 0f;
            max = 0f;
            DatasetColumn col = ds?.GetColumn(column);
            if (col == null || !col.IsNumeric)
                return false;

            if (!ValidRowsOnly)
            {
                // A column the loader never filled keeps the float.MaxValue / MinValue initialisers.
                if (col.MinValue > col.MaxValue)
                    return false;
                min = col.MinValue;
                max = col.MaxValue;
                return true;
            }

            Dictionary<int, Range> perColumn = s_Cache.GetOrCreateValue(ds);
            if (!perColumn.TryGetValue(column, out Range range))
            {
                range = Scan(ds, column);
                perColumn[column] = range;
            }

            if (!range.Valid)
                return false;
            min = range.Min;
            max = range.Max;
            return true;
        }

        /// <summary>Min and max over the given rows that have a value (no cache). False when none has one.</summary>
        public static bool GetOverRows(DatasetColumnar ds, int column, IReadOnlyList<int> rows, out float min, out float max)
        {
            min = 0f;
            max = 0f;
            DatasetColumn col = ds?.GetColumn(column);
            if (col == null || !col.IsNumeric || rows == null)
                return false;

            bool any = false;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (int row in rows)
            {
                if (!ds.HasValue(row, column))
                    continue;
                float v = ds.GetNumericValue(row, column);
                if (float.IsNaN(v))
                    continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
                any = true;
            }

            if (!any)
                return false;
            min = lo;
            max = hi;
            return true;
        }

        /// <summary>Forgets the cached scans of one dataset (call if a dataset's values were changed in place).</summary>
        public static void Forget(DatasetColumnar ds)
        {
            if (ds != null)
                s_Cache.Remove(ds);
        }

        static Range Scan(DatasetColumnar ds, int column)
        {
            bool any = false;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int row = 0; row < ds.RowCount; row++)
            {
                if (!ds.HasValue(row, column))
                    continue;
                float v = ds.GetNumericValue(row, column);
                if (float.IsNaN(v))
                    continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
                any = true;
            }

            return any ? new Range { Valid = true, Min = lo, Max = hi } : new Range();
        }
    }
}
