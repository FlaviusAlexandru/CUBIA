using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// Which rows a layer draws, and the filter rules (moved here from ScatterplotVisualizer, which
    /// keeps its public methods as wrappers). One place, so points, computed layers and labels
    /// agree on membership. No manager reference.
    /// </summary>
    public static class LayerRows
    {
        /// <summary>One filter entry resolved against a dataset once per build (not per row).</summary>
        public class ResolvedFilter
        {
            public int ColumnIndex;
            public DatasetColumn Column;
            public List<string> CategoryTable;
            public string Label;
            // Value ranges (x = min, y = max, inclusive); when any, they replace Label.
            public List<Vector2> Ranges;
        }

        /// <summary>
        /// Resolves filter entries against ds, skipping out-of-range columns. With warnUntyped, a
        /// column that is neither categorical nor numeric logs a warning (it matches every row).
        /// T lets the visualizer keep returning its own ActiveFilter type.
        /// </summary>
        public static List<T> ResolveFilters<T>(IReadOnlyList<MultiplayerScatterplotManager.FilterEntry> entries, DatasetColumnar ds, bool warnUntyped = false)
            where T : ResolvedFilter, new()
        {
            var result = new List<T>();
            if (entries == null || ds == null)
                return result;

            foreach (MultiplayerScatterplotManager.FilterEntry entry in entries)
            {
                if (entry == null || entry.ColumnIndex < 0 || entry.ColumnIndex >= ds.ColumnCount)
                    continue;

                DatasetColumn column = ds.Columns[entry.ColumnIndex];
                if (warnUntyped && !column.IsCategorical && !column.IsNumeric)
                {
                    Debug.LogWarning($"[ScatterplotVisualizer] Filter column '{column.Name}' is neither " +
                                     $"categorical nor numeric - it matches every row.");
                }

                result.Add(new T
                {
                    ColumnIndex = entry.ColumnIndex,
                    Column = column,
                    CategoryTable = column.IsCategorical ? new List<string>(column.UniqueValues) : null,
                    Label = entry.Label ?? "",
                    Ranges = entry.Ranges != null ? entry.Ranges.ConvertAll(r => new Vector2(r.min, r.max)) : new List<Vector2>(),
                });
            }

            return result;
        }

        public static List<ResolvedFilter> ResolveFilters(PlotLayer layer, DatasetColumnar ds) =>
            ResolveFilters<ResolvedFilter>(layer?.Filters, ds ?? layer?.Dataset);

        /// <summary>
        /// The row's value on a column as the label the UI uses (categorical: its category;
        /// numeric: float.ToString()). Null when the cell has no value.
        /// </summary>
        public static string RowLabel(DatasetColumnar ds, int row, int columnIndex, DatasetColumn column, List<string> categoryTable)
        {
            if (!ds.HasValue(row, columnIndex))
                return null;

            if (column.IsCategorical)
            {
                int catIdx = ds.GetCategoryIndex(row, columnIndex);
                if (catIdx < 0 || categoryTable == null || catIdx >= categoryTable.Count)
                    return null;
                return categoryTable[catIdx];
            }

            if (column.IsNumeric)
                return ds.GetNumericValue(row, columnIndex).ToString();

            return null;
        }

        /// <summary>OR over the filters: true when the row passes at least one, or there are none.</summary>
        public static bool PassesFilters(DatasetColumnar ds, int row, IReadOnlyList<ResolvedFilter> filters)
        {
            if (filters == null || filters.Count == 0)
                return true;

            for (int i = 0; i < filters.Count; i++)
            {
                if (MatchesFilter(ds, row, filters[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Convenience: the row against the layer's own filters (resolves them on every call; in a
        /// loop, resolve once with ResolveFilters and use the overload above).
        /// </summary>
        public static bool PassesFilters(PlotLayer layer, DatasetColumnar ds, int row) =>
            PassesFilters(ds, row, ResolveFilters(layer, ds));

        /// <summary>
        /// One filter entry: with ranges on a numeric column, the value must lie in at least one (a
        /// row with no value never does); otherwise the label rule of the other overload.
        /// </summary>
        public static bool MatchesFilter(DatasetColumnar ds, int row, ResolvedFilter filter)
        {
            if (filter.Ranges != null && filter.Ranges.Count > 0 && filter.Column.IsNumeric)
            {
                if (!ds.HasValue(row, filter.ColumnIndex))
                    return false;
                float value = ds.GetNumericValue(row, filter.ColumnIndex);
                foreach (Vector2 range in filter.Ranges)
                {
                    if (value >= range.x && value <= range.y)
                        return true;
                }
                return false;
            }

            return MatchesFilter(ds, row, filter.ColumnIndex, filter.Column, filter.CategoryTable, filter.Label);
        }

        /// <summary>
        /// Label rule: an untyped column matches every row; no label ("Any value") matches a row
        /// with a value in the column; a label must equal the row's label.
        /// </summary>
        public static bool MatchesFilter(DatasetColumnar ds, int row, int filterCol, DatasetColumn filterColumn,
                                         List<string> filterCategoryTable, string filterLabel)
        {
            if (!filterColumn.IsCategorical && !filterColumn.IsNumeric)
                return true;

            if (string.IsNullOrEmpty(filterLabel))
                return ds.HasValue(row, filterCol);

            string rowLabel = RowLabel(ds, row, filterCol, filterColumn, filterCategoryTable);
            return rowLabel != null && rowLabel == filterLabel;
        }

        /// <summary>
        /// The rows the layer draws, ascending: a value in every mapped axis column; for a Subset,
        /// passing its filters; not taken by a subset of this layer that has HiddenInSource (same
        /// dataset only). A computed layer is read as its own row source (its frozen fields after
        /// the source is gone) with its filters applied. Dataset layers keep rows their filters
        /// reject: those are dimmed, not removed (see FilterPassing).
        /// </summary>
        public static void Drawable(PlotLayer layer, LayerStack stack, List<int> rows)
        {
            Collect(layer, stack, rows, applyOwnFilters: layer != null && (layer.FiltersExclude || layer.Kind == LayerKind.Computed));
        }

        /// <summary>Drawable rows that also pass the layer's filters (for a Dataset layer: the rows that are not dimmed).</summary>
        public static void FilterPassing(PlotLayer layer, LayerStack stack, List<int> rows)
        {
            Collect(layer, stack, rows, applyOwnFilters: true);
        }

        static void Collect(PlotLayer layer, LayerStack stack, List<int> rows, bool applyOwnFilters)
        {
            rows.Clear();
            DatasetColumnar ds = layer?.Dataset;
            if (ds == null)
                return;

            int[] axes = MappedAxes(layer, ds);
            List<ResolvedFilter> own = applyOwnFilters ? ResolveFilters(layer, ds) : null;
            List<List<ResolvedFilter>> hiders = HidingSubsets(layer, stack, ds);

            for (int row = 0; row < ds.RowCount; row++)
            {
                if (!HasAxisValues(ds, row, axes))
                    continue;
                if (own != null && !PassesFilters(ds, row, own))
                    continue;
                if (hiders != null && TakenByHider(ds, row, hiders))
                    continue;
                rows.Add(row);
            }
        }

        /// <summary>True when the row has a value in every given column.</summary>
        public static bool HasAxisValues(DatasetColumnar ds, int row, int[] columns)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                if (!ds.HasValue(row, columns[i]))
                    return false;
            }
            return true;
        }

        /// <summary>The layer's X/Y/Z columns that exist in ds (an unset axis needs no value).</summary>
        public static int[] MappedAxes(PlotLayer layer, DatasetColumnar ds)
        {
            var list = new List<int>(3);
            foreach (int c in new[] { layer.XColumnIndex, layer.YColumnIndex, layer.ZColumnIndex })
            {
                if (c >= 0 && c < ds.ColumnCount)
                    list.Add(c);
            }
            return list.ToArray();
        }

        static List<List<ResolvedFilter>> HidingSubsets(PlotLayer layer, LayerStack stack, DatasetColumnar ds)
        {
            if (stack == null || layer == null || !layer.IsPointLayer)
                return null;

            List<List<ResolvedFilter>> result = null;
            foreach (PlotLayer other in stack.Layers)
            {
                if (other == layer || other.Kind != LayerKind.Subset || !other.HiddenInSource ||
                    other.SourceLayerId != layer.Id || !ReferenceEquals(other.Dataset, ds))
                    continue;
                result ??= new List<List<ResolvedFilter>>();
                result.Add(ResolveFilters(other, ds));
            }
            return result;
        }

        static bool TakenByHider(DatasetColumnar ds, int row, List<List<ResolvedFilter>> hiders)
        {
            foreach (List<ResolvedFilter> filters in hiders)
            {
                if (PassesFilters(ds, row, filters))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Hash of everything FilterPassing(layer, stack) depends on: dataset instance, X/Y/Z, the
        /// layer's filters, and the filters of subsets hiding rows from it. Equal signature ⇒ same rows.
        /// </summary>
        public static int InputSignature(PlotLayer layer, LayerStack stack)
        {
            if (layer == null)
                return 0;

            var h = new HashCode();
            h.Add(layer.Dataset != null ? RuntimeHelpers.GetHashCode(layer.Dataset) : 0);
            h.Add(layer.Kind);
            h.Add(layer.XColumnIndex);
            h.Add(layer.YColumnIndex);
            h.Add(layer.ZColumnIndex);
            AddFilters(ref h, layer.Filters);

            if (stack != null && layer.IsPointLayer)
            {
                foreach (PlotLayer other in stack.Layers)
                {
                    if (other == layer || other.Kind != LayerKind.Subset || !other.HiddenInSource || other.SourceLayerId != layer.Id)
                        continue;
                    h.Add(other.Id);
                    h.Add(other.Dataset != null ? RuntimeHelpers.GetHashCode(other.Dataset) : 0);
                    AddFilters(ref h, other.Filters);
                }
            }
            return h.ToHashCode();
        }

        static void AddFilters(ref HashCode h, List<MultiplayerScatterplotManager.FilterEntry> filters)
        {
            if (filters == null)
                return;
            h.Add(filters.Count);
            foreach (MultiplayerScatterplotManager.FilterEntry f in filters)
            {
                if (f == null)
                    continue;
                h.Add(f.ColumnIndex);
                h.Add(f.Label ?? "");
                if (f.Ranges == null)
                    continue;
                h.Add(f.Ranges.Count);
                foreach (MultiplayerScatterplotManager.FilterRange r in f.Ranges)
                {
                    h.Add(r.min);
                    h.Add(r.max);
                }
            }
        }
    }
}
