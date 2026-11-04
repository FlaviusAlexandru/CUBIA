using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Writes StreamingAssets/DataCSV/ProcessedData/cubia_dataset_index.json before every build: the
/// file name, size, SHA-256 and header (rows, column names and types) of each .cdataset. Android
/// builds can't list or read StreamingAssets with file IO, so DatasetStore uses this index for the
/// wizard's dataset cards and the lobby's dataset check, and only extracts the dataset that is opened.
/// The file is generated, so it is git-ignored.
/// </summary>
public sealed class DatasetIndexBuilder : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report) => Write();

    [MenuItem("Tools/CUBIA/Build/Write Dataset Index")]
    public static void Write()
    {
        var index = new DatasetIndex();
        foreach (string file in DatasetStore.Files())
        {
            string path = Path.Combine(DatasetStore.PackedFolder, file);
            DatasetSummary summary = ColumnarBinaryImporter.ReadSummary(file, includeCellStats: false);
            if (summary == null)
            {
                Debug.LogWarning($"[DatasetIndexBuilder] Skipping unreadable {file}");
                continue;
            }

            var entry = new DatasetIndex.Entry
            {
                FileName = file,
                Length = new FileInfo(path).Length,
                Sha256 = Sha256(path),
                RowCount = summary.RowCount,
                ColumnNames = new string[summary.Columns.Count],
                ColumnTypes = new int[summary.Columns.Count],
            };
            for (int i = 0; i < summary.Columns.Count; i++)
            {
                entry.ColumnNames[i] = summary.Columns[i].Name;
                entry.ColumnTypes[i] = (int)summary.Columns[i].Type;
            }
            index.Files.Add(entry);
        }

        string indexPath = Path.Combine(DatasetStore.PackedFolder, DatasetStore.IndexFileName);
        File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
        AssetDatabase.ImportAsset(ToAssetPath(indexPath));
        Debug.Log($"[DatasetIndexBuilder] Indexed {index.Files.Count} datasets → {indexPath}");
    }

    static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    static string ToAssetPath(string fullPath)
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/') + "/";
        return Path.GetFullPath(fullPath).Replace('\\', '/').Replace(project, "");
    }
}
