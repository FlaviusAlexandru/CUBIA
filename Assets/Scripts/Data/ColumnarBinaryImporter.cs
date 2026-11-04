using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Reads the .cdataset binary format written by preprocess_csv_columnar.py.
///
/// Byte layout (must stay in lockstep with the Python writer):
///   4 bytes  magic "DVCB"
///   2 bytes  version (uint16, little-endian)
///   4 bytes  rowCount (uint32, little-endian)
///   4 bytes  columnCount (uint32, little-endian)
///   per column:
///     1 byte   name length (byte)
///     N bytes  name (UTF8)
///     1 byte   type (1 = Numeric, 2 = Categorical, 3 = DateTime)
///     Numeric:
///       4 bytes            minValue (float32)
///       4 bytes            maxValue (float32)
///       rowCount * 4 bytes float32[] data (raw, little-endian)
///     DateTime:
///       Same layout as Numeric - minValue/maxValue/data are elapsed
///       seconds relative to the column's own earliest timestamp (NOT
///       absolute Unix epoch - see preprocess_csv_columnar.py for why).
///     Categorical:
///       4 bytes  categoryCount (uint32)
///       per category:
///         2 bytes  name length (uint16)
///         N bytes  name (UTF8)
///       rowCount * 4 bytes int32[] category indices (raw, little-endian)
///     Version 3+ only, at the END of every column block (after the data above):
///       ceil(rowCount / 8) bytes validity mask - 1 bit per row, LSB-first
///       (row r is bit r % 8 of byte r / 8), 1 = the CSV cell had a value.
///       Missing cells still hold 0 / category index 0 in the data arrays;
///       use DatasetColumnar.HasValue to tell them apart. Version 2 files
///       have no mask and every cell counts as present.
///
/// All bulk arrays are read via Buffer.BlockCopy - a single memcpy per
/// column, not a loop of per-element parses. On any little-endian platform
/// (x86/x64/ARM - i.e. effectively every desktop, mobile, and VR headset
/// Unity targets) this is a direct reinterpretation of the file bytes.
/// </summary>
public static class ColumnarBinaryImporter
{
    private const string ExpectedMagic = "DVCB";
    private const byte TypeNumeric = 1;
    private const byte TypeCategorical = 2;
    private const byte TypeDateTime = 3;
    private const ushort FirstVersionWithValidityMask = 3;
    private const ushort LatestKnownVersion = 3;

    /// <summary>Folder the .cdataset files are loaded from.</summary>
    public static string DataFolder => DatasetStore.LocalFolder;

    public static DatasetColumnar Load(string fileName)
    {
        DatasetStore.EnsureLocal(fileName); // Android: copy the chosen dataset out of the APK
        string path = Path.Combine(DataFolder, fileName);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[ColumnarBinaryImporter] File not found: {path}");
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColumnarBinaryImporter] Failed to read {path}: {e.Message}");
            return null;
        }

        try
        {
            return Parse(bytes, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColumnarBinaryImporter] Failed to parse {path}: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Header-only read for the import wizard: column names and types and the row count and, when
    /// includeCellStats is set, each column's empty-cell count (from the v3 validity masks) and up to
    /// sampleCount formatted values from the first non-empty cells. Bulk data is skipped with seeks,
    /// so a 10M-row file costs its masks (rowCount / 8 bytes per column), not its data.
    /// Returns null when the file is missing or invalid.
    /// </summary>
    public static DatasetSummary ReadSummary(string fileName, bool includeCellStats = true, int sampleCount = 3)
    {
        // Android: header-only summaries come from the build index, so listing datasets extracts nothing.
        if (!includeCellStats && DatasetStore.IsPacked && !DatasetStore.IsLocal(fileName))
            return DatasetStore.IndexedSummary(fileName);
        DatasetStore.EnsureLocal(fileName);

        string path = Path.Combine(DataFolder, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
            return ReadSummaryFrom(reader, Path.GetFileName(path), includeCellStats, sampleCount);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ColumnarBinaryImporter] Failed to read summary of {path}: {e.Message}");
            return null;
        }
    }

    private static DatasetSummary ReadSummaryFrom(BinaryReader reader, string fileName, bool includeCellStats, int sampleCount)
    {
        Stream stream = reader.BaseStream;

        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != ExpectedMagic)
            return null;

        ushort version = reader.ReadUInt16();
        int rowCount = (int)reader.ReadUInt32();
        int columnCount = (int)reader.ReadUInt32();
        bool hasValidityMasks = version >= FirstVersionWithValidityMask;
        int maskByteCount = (rowCount + 7) / 8;

        DatasetSummary summary = new DatasetSummary
        {
            FileName = fileName,
            Name = Path.GetFileNameWithoutExtension(fileName),
            RowCount = rowCount
        };
        List<int> sampleRows = new List<int>(sampleCount);

        for (int col = 0; col < columnCount; col++)
        {
            ColumnSummary column = new ColumnSummary { Name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())) };
            byte typeByte = reader.ReadByte();
            float minValue = 0f;
            long categoryTableStart = -1;

            if (typeByte == TypeNumeric || typeByte == TypeDateTime)
            {
                column.Type = typeByte == TypeDateTime ? DataValueType.DateTime : DataValueType.Numeric;
                minValue = reader.ReadSingle();
                reader.ReadSingle(); // maxValue
            }
            else if (typeByte == TypeCategorical)
            {
                column.Type = DataValueType.Categorical;
                uint categoryCount = reader.ReadUInt32();
                categoryTableStart = stream.Position;
                for (uint c = 0; c < categoryCount; c++)
                    stream.Seek(reader.ReadUInt16(), SeekOrigin.Current);
            }
            else
            {
                Debug.LogWarning($"[ColumnarBinaryImporter] Unknown column type byte {typeByte} for column '{column.Name}' in {fileName}.");
                return null;
            }

            // Numeric (float32) and categorical (int32) cells are both 4 bytes.
            long dataStart = stream.Position;
            long maskStart = dataStart + (long)rowCount * 4;
            long columnEnd = maskStart + (hasValidityMasks ? maskByteCount : 0);

            if (includeCellStats)
            {
                sampleRows.Clear();
                if (hasValidityMasks)
                {
                    stream.Position = maskStart;
                    int present = CountPresentRows(reader.ReadBytes(maskByteCount), rowCount, sampleRows, sampleCount);
                    column.MissingCount = rowCount - present;
                }
                else
                {
                    for (int r = 0; r < Math.Min(sampleCount, rowCount); r++)
                        sampleRows.Add(r);
                }

                ReadSamples(reader, column, dataStart, categoryTableStart, minValue, sampleRows);
            }

            stream.Position = columnEnd;
            summary.Columns.Add(column);
        }

        return summary;
    }

    /// <summary>Counts set mask bits below rowCount and collects the first few set rows.</summary>
    private static int CountPresentRows(byte[] mask, int rowCount, List<int> firstRows, int maxFirstRows)
    {
        int present = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            int bits = mask[i];
            int rowsInByte = Math.Min(8, rowCount - i * 8);
            if (rowsInByte < 8)
                bits &= (1 << rowsInByte) - 1; // ignore padding bits in the last byte
            if (bits == 0)
                continue;

            for (int b = bits; b != 0; b &= b - 1)
                present++;

            for (int bit = 0; bit < rowsInByte && firstRows.Count < maxFirstRows; bit++)
                if ((bits & (1 << bit)) != 0)
                    firstRows.Add(i * 8 + bit);
        }
        return present;
    }

    private static void ReadSamples(BinaryReader reader, ColumnSummary column, long dataStart, long categoryTableStart, float minValue, List<int> rows)
    {
        Stream stream = reader.BaseStream;

        if (column.Type != DataValueType.Categorical)
        {
            foreach (int row in rows)
            {
                stream.Position = dataStart + (long)row * 4;
                column.Samples.Add(FormatSampleValue(reader.ReadSingle(), column.Type, minValue));
            }
            return;
        }

        int[] indices = new int[rows.Count];
        int maxIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            stream.Position = dataStart + (long)rows[i] * 4;
            indices[i] = reader.ReadInt32();
            maxIndex = Math.Max(maxIndex, indices[i]);
        }

        // Walk the category table only as far as the largest index needed.
        string[] names = new string[maxIndex + 1];
        stream.Position = categoryTableStart;
        for (int c = 0; c <= maxIndex; c++)
            names[c] = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));

        foreach (int index in indices)
            column.Samples.Add(index >= 0 ? names[index] : "");
    }

    private static string FormatSampleValue(float value, DataValueType type, float columnMin)
    {
        if (type != DataValueType.DateTime)
        {
            float abs = Math.Abs(value);
            return abs != 0f && (abs >= 1e6f || abs < 1e-3f)
                ? value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Same rule as GlyphRangeEditor: values past 1e8 are absolute Unix seconds; otherwise they
        // are seconds after the column's earliest timestamp (not in the file), shown as an offset.
        if (columnMin > 1e8f)
            return DateTimeOffset.FromUnixTimeSeconds((long)value).UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        TimeSpan elapsed = TimeSpan.FromSeconds(Math.Round(value));
        return elapsed.TotalDays >= 1
            ? $"+{(int)elapsed.TotalDays}d {elapsed.Hours}h"
            : "+" + elapsed.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DatasetColumnar Parse(byte[] bytes, string datasetName)
    {
        int offset = 0;

        string magic = Encoding.ASCII.GetString(bytes, offset, 4);
        offset += 4;

        if (magic != ExpectedMagic)
        {
            Debug.LogError($"[ColumnarBinaryImporter] Bad magic bytes '{magic}', expected '{ExpectedMagic}'.");
            return null;
        }

        ushort version = BitConverter.ToUInt16(bytes, offset); offset += 2;
        uint rowCountU = BitConverter.ToUInt32(bytes, offset); offset += 4;
        uint columnCountU = BitConverter.ToUInt32(bytes, offset); offset += 4;

        int rowCount = (int)rowCountU;
        int columnCount = (int)columnCountU;

        if (version > LatestKnownVersion)
        {
            Debug.LogWarning($"[ColumnarBinaryImporter] '{datasetName}' is format v{version}; this build knows up to v{LatestKnownVersion}. Reading it as v{LatestKnownVersion}.");
        }

        bool hasValidityMasks = version >= FirstVersionWithValidityMask;
        int maskByteCount = (rowCount + 7) / 8;

        DatasetColumnar dataset = new DatasetColumnar(datasetName, rowCount, columnCount);

        for (int col = 0; col < columnCount; col++)
        {
            byte nameLen = bytes[offset]; offset += 1;
            string name = Encoding.UTF8.GetString(bytes, offset, nameLen); offset += nameLen;

            byte typeByte = bytes[offset]; offset += 1;

            DatasetColumn column = new DatasetColumn(name);

            if (typeByte == TypeNumeric || typeByte == TypeDateTime)
            {
                column.Type = typeByte == TypeDateTime ? DataValueType.DateTime : DataValueType.Numeric;

                float minValue = BitConverter.ToSingle(bytes, offset); offset += 4;
                float maxValue = BitConverter.ToSingle(bytes, offset); offset += 4;
                column.MinValue = minValue;
                column.MaxValue = maxValue;

                dataset.Columns.Add(column);
                dataset.ColumnMapping[name] = col;

                float[] values = new float[rowCount];
                int byteCount = rowCount * sizeof(float);
                Buffer.BlockCopy(bytes, offset, values, 0, byteCount);
                offset += byteCount;

                dataset.SetNumericColumn(col, values);
            }
            else if (typeByte == TypeCategorical)
            {
                column.Type = DataValueType.Categorical;

                uint categoryCount = BitConverter.ToUInt32(bytes, offset); offset += 4;
                string[] categoryTable = new string[categoryCount];

                for (int c = 0; c < categoryCount; c++)
                {
                    ushort catLen = BitConverter.ToUInt16(bytes, offset); offset += 2;
                    categoryTable[c] = Encoding.UTF8.GetString(bytes, offset, catLen); offset += catLen;

                    column.UniqueValues.Add(categoryTable[c]);
                }

                dataset.Columns.Add(column);
                dataset.ColumnMapping[name] = col;

                int[] indices = new int[rowCount];
                int byteCount = rowCount * sizeof(int);
                Buffer.BlockCopy(bytes, offset, indices, 0, byteCount);
                offset += byteCount;

                dataset.SetCategoricalColumn(col, indices, categoryTable);
            }
            else
            {
                Debug.LogError($"[ColumnarBinaryImporter] Unknown column type byte {typeByte} for column '{name}'.");
                return null;
            }

            if (hasValidityMasks)
            {
                byte[] mask = new byte[maskByteCount];
                Buffer.BlockCopy(bytes, offset, mask, 0, maskByteCount);
                offset += maskByteCount;

                dataset.SetValidityMask(col, mask);
            }
        }

        dataset.FinalizeDatasetMetadata();

        Debug.Log(
            $"[ColumnarBinaryImporter] Loaded '{dataset.Name}' (format v{version}): " +
            $"{dataset.RowCount} rows, {dataset.ColumnCount} columns, from raw bytes with zero per-cell parsing."
        );

        return dataset;
    }
}