using System.Collections.Generic;

/// <summary>
/// What the import wizard shows about a .cdataset without loading its data: produced by
/// ColumnarBinaryImporter.ReadSummary from the header, the validity masks and a few cells.
/// Column order matches the loaded dataset's column indices.
/// </summary>
public sealed class DatasetSummary
{
    public string FileName;   // e.g. "clusters.cdataset"
    public string Name;       // e.g. "clusters" (= DatasetColumnar.Name, the prefs key)
    public int RowCount;
    public readonly List<ColumnSummary> Columns = new();
}

public sealed class ColumnSummary
{
    public string Name;
    public DataValueType Type;

    /// <summary>Rows whose CSV cell was empty (from the v3 validity mask; 0 for v2 files).</summary>
    public int MissingCount;

    /// <summary>Up to three formatted values from the first non-empty cells.</summary>
    public readonly List<string> Samples = new();

    /// <summary>Numeric or DateTime: the column types X/Y/Z accept.</summary>
    public bool IsAxisCandidate => Type == DataValueType.Numeric || Type == DataValueType.DateTime;
}
