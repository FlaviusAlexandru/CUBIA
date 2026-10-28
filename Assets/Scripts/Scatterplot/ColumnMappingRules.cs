namespace DataViz
{
    /// <summary>
    /// Visual channels a dataset column (a "label") can be mapped to by dropping a
    /// LabelBlock from the BookmarkPalette onto a MappingDropSlot.
    /// </summary>
    public enum MappingTarget
    {
        X,
        Y,
        Z,
        Colour,
        Size,
        Filter,
        Time,
        Glyph
    }

    /// <summary>
    /// Which column types each MappingTarget accepts, and how an accepted column is
    /// applied through MultiplayerScatterplotManager. Kept separate from the slot so the
    /// rules can be checked (and tested) without any XR objects.
    ///
    ///   X, Y, Z, Time : numeric or DateTime (DatasetColumn.IsNumeric includes DateTime)
    ///   Colour        : numeric, DateTime or categorical
    ///   Size          : numeric or DateTime, and only while the size mode is Mapped
    ///   Filter        : any column (several Filter drops combine with OR)
    ///   Glyph         : categorical
    /// </summary>
    public static class ColumnMappingRules
    {
        public static bool Accepts(MappingTarget target, DatasetColumn column, MultiplayerScatterplotManager manager)
        {
            if (column == null)
                return false;

            switch (target)
            {
                case MappingTarget.X:
                case MappingTarget.Y:
                case MappingTarget.Z:
                case MappingTarget.Time:
                    return column.IsNumeric;
                case MappingTarget.Colour:
                    return column.IsNumeric || column.IsCategorical;
                case MappingTarget.Size:
                    return column.IsNumeric && manager != null && manager.SizeMapped;
                case MappingTarget.Filter:
                    return true;
                case MappingTarget.Glyph:
                    return column.IsCategorical;
                default:
                    return false;
            }
        }

        public static bool Accepts(MappingTarget target, string columnName, MultiplayerScatterplotManager manager)
        {
            if (manager == null || manager.LoadedDataset == null || string.IsNullOrEmpty(columnName))
                return false;

            return Accepts(target, manager.LoadedDataset.GetColumn(columnName), manager);
        }

        /// <summary>
        /// Validates and applies a column to a target. Returns false (and changes nothing)
        /// when the column is unknown or the target does not accept it.
        /// </summary>
        public static bool TryApply(MappingTarget target, string columnName, MultiplayerScatterplotManager manager)
        {
            if (!Accepts(target, columnName, manager))
                return false;

            int columnIndex = manager.LoadedDataset.GetColumnIndex(columnName);
            if (columnIndex < 0)
                return false;

            switch (target)
            {
                case MappingTarget.X: manager.RequestXColumnRpc(columnIndex); return true;
                case MappingTarget.Y: manager.RequestYColumnRpc(columnIndex); return true;
                case MappingTarget.Z: manager.RequestZColumnRpc(columnIndex); return true;
                case MappingTarget.Colour: manager.RequestColorColumnRpc(columnIndex); return true;
                case MappingTarget.Size: manager.RequestSizeColumnRpc(columnIndex); return true;
                case MappingTarget.Filter: manager.RequestAddFilterColumnRpc(columnIndex); return true;
                case MappingTarget.Time: manager.RequestTimeColumnRpc(columnIndex); return true;
                case MappingTarget.Glyph: manager.RequestGlyphColumnRpc(columnIndex); return true;
                default: return false;
            }
        }
    }
}
