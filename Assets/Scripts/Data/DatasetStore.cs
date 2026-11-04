using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Where the .cdataset files live and how to reach them on every platform.
///
/// Desktop and editor: StreamingAssets/DataCSV/ProcessedData is a normal folder, used as is.
/// Android (Quest): StreamingAssets is packed inside the APK, so plain file IO can't see it. There
/// the list of datasets, their header summaries and hashes come from an index written at build
/// time (<see cref="IndexFileName"/>), and a dataset's file is only copied out of the APK into
/// persistentDataPath/ProcessedData when it is actually opened (<see cref="EnsureLocal"/>).
/// </summary>
public static class DatasetStore
{
    public const string Extension = ".cdataset";
    public const string IndexFileName = "cubia_dataset_index.json";

    /// <summary>The project folder the datasets ship from (inside the APK on Android).</summary>
    public static string PackedFolder => Path.Combine(Application.streamingAssetsPath, "DataCSV", "ProcessedData");

    /// <summary>True when StreamingAssets is not a plain folder (Android: jar:file://…).</summary>
    public static bool IsPacked => Application.streamingAssetsPath.Contains("://");

    /// <summary>Folder the importers read files from.</summary>
    public static string LocalFolder => IsPacked ? Path.Combine(Application.persistentDataPath, "ProcessedData") : PackedFolder;

    static DatasetIndex s_Index;

    /// <summary>File names (e.g. "clusters.cdataset"), sorted by name.</summary>
    public static List<string> Files()
    {
        var files = new List<string>();
        if (IsPacked)
        {
            foreach (DatasetIndex.Entry entry in Index().Files)
                files.Add(entry.FileName);
        }
        else if (Directory.Exists(PackedFolder))
        {
            foreach (string path in Directory.GetFiles(PackedFolder, "*" + Extension))
                files.Add(Path.GetFileName(path));
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static bool Exists(string file) =>
        IsPacked ? Find(file) != null : File.Exists(Path.Combine(PackedFolder, file));

    /// <summary>True when the file can be opened with plain file IO right now (without extracting).</summary>
    public static bool IsLocal(string file)
    {
        string path = Path.Combine(LocalFolder, file);
        if (!IsPacked) return File.Exists(path);
        DatasetIndex.Entry entry = Find(file);
        return entry != null && File.Exists(path) && new FileInfo(path).Length == entry.Length;
    }

    /// <summary>
    /// Makes sure the file is readable under <see cref="LocalFolder"/>: on Android copies it out of
    /// the APK the first time (and again if the shipped file changed). Blocks while copying; a 4 MB
    /// dataset takes well under a second. False when there is no such dataset.
    /// </summary>
    public static bool EnsureLocal(string file)
    {
        if (!IsPacked) return File.Exists(Path.Combine(PackedFolder, file));
        if (IsLocal(file)) return true;
        if (Find(file) == null) return false;

        byte[] bytes = ReadPacked(file);
        if (bytes == null) return false;

        Directory.CreateDirectory(LocalFolder);
        string path = Path.Combine(LocalFolder, file);
        string temp = path + ".part";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        Debug.Log($"[DatasetStore] Extracted {file} ({bytes.Length / 1048576f:F1} MB) to {LocalFolder}");
        return true;
    }

    /// <summary>Build-time SHA-256 of a packed dataset, or null (desktop: always null, hash the file).</summary>
    public static string IndexedHash(string file) => IsPacked ? Find(file)?.Sha256 : null;

    /// <summary>Header summary from the build-time index (no samples or missing counts), or null.</summary>
    public static DatasetSummary IndexedSummary(string file)
    {
        DatasetIndex.Entry entry = IsPacked ? Find(file) : null;
        if (entry == null) return null;
        var summary = new DatasetSummary
        {
            FileName = entry.FileName,
            Name = Path.GetFileNameWithoutExtension(entry.FileName),
            RowCount = entry.RowCount,
        };
        for (int i = 0; i < entry.ColumnNames.Length; i++)
            summary.Columns.Add(new ColumnSummary { Name = entry.ColumnNames[i], Type = (DataValueType)entry.ColumnTypes[i] });
        return summary;
    }

    // ------------------------------------------------------------------ packed access

    static DatasetIndex.Entry Find(string file)
    {
        foreach (DatasetIndex.Entry entry in Index().Files)
            if (string.Equals(entry.FileName, file, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    static DatasetIndex Index()
    {
        if (s_Index != null) return s_Index;
        byte[] json = IsPacked ? ReadPacked(IndexFileName) : null;
        s_Index = json != null ? JsonUtility.FromJson<DatasetIndex>(System.Text.Encoding.UTF8.GetString(json)) : null;
        s_Index ??= new DatasetIndex();
        s_Index.Files ??= new List<DatasetIndex.Entry>();
        if (IsPacked && json == null)
            Debug.LogError($"[DatasetStore] {IndexFileName} is missing from the build: no datasets can be listed.");
        return s_Index;
    }

    // Reads a file from StreamingAssets inside the APK. UnityWebRequest is the only way in; it
    // completes on a worker thread, so waiting for it here is safe.
    static byte[] ReadPacked(string file)
    {
        string url = PackedFolder.Replace('\\', '/') + "/" + file;
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.SendWebRequest();
        while (!request.isDone)
            Thread.Sleep(1);
        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[DatasetStore] Could not read {url}: {request.error}");
            return null;
        }
        return request.downloadHandler.data;
    }
}

/// <summary>Build-time description of the shipped datasets (see DatasetIndexBuilder).</summary>
[Serializable]
public sealed class DatasetIndex
{
    public List<Entry> Files = new();

    [Serializable]
    public sealed class Entry
    {
        public string FileName;
        public long Length;
        public string Sha256;
        public int RowCount;
        public string[] ColumnNames;
        public int[] ColumnTypes; // DataValueType
    }
}
