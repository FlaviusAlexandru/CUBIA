using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// SHA-256 of the dataset file a name would load, so the host and clients can check they have
    /// the same version. Resolves names exactly like MultiplayerScatterplotManager.LoadLocalDataset:
    /// the processed .cdataset in StreamingAssets/DataCSV/ProcessedData first, else the .csv in
    /// StreamingAssets/DataCSV. Each file is hashed once (re-hashed only if its size or time changes).
    /// </summary>
    public static class DatasetHash
    {
        readonly struct Entry
        {
            public readonly long Length;
            public readonly DateTime WriteTime;
            public readonly string Hash;
            public Entry(long length, DateTime writeTime, string hash) { Length = length; WriteTime = writeTime; Hash = hash; }
        }

        static readonly Dictionary<string, Entry> s_Cache = new();

        /// <summary>Hex SHA-256 of the local file for datasetName, or "" if there is no such file.</summary>
        public static string Of(string datasetName)
        {
            // Android: the files sit inside the APK; their hashes were taken at build time.
            if (DatasetStore.IsPacked)
                return string.IsNullOrEmpty(datasetName) ? "" : DatasetStore.IndexedHash(ToColumnar(datasetName)) ?? "";

            string path = ResolvePath(datasetName);
            if (path == null)
                return "";

            try
            {
                var info = new FileInfo(path);
                if (s_Cache.TryGetValue(path, out Entry cached) && cached.Length == info.Length && cached.WriteTime == info.LastWriteTimeUtc)
                    return cached.Hash;

                string hash;
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");

                s_Cache[path] = new Entry(info.Length, info.LastWriteTimeUtc, hash);
                return hash;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DatasetHash] Could not hash {path}: {e.Message}");
                return "";
            }
        }

        /// <summary>Compares the host's dataset (name + hash) with the local file.</summary>
        public static DatasetMatch Compare(string datasetName, string hostHash)
        {
            if (string.IsNullOrEmpty(datasetName))
                return DatasetMatch.Unknown;

            string localHash = Of(datasetName);
            if (localHash.Length == 0)
                return DatasetMatch.Missing;

            return string.Equals(localHash, hostHash, StringComparison.OrdinalIgnoreCase) ? DatasetMatch.Match : DatasetMatch.Mismatch;
        }

        static string ResolvePath(string datasetName)
        {
            if (string.IsNullOrEmpty(datasetName))
                return null;

            string root = Path.Combine(Application.streamingAssetsPath, "DataCSV");

            string columnar = ToColumnar(datasetName);
            string columnarPath = Path.Combine(root, "ProcessedData", columnar);
            if (File.Exists(columnarPath))
                return columnarPath;

            string csvPath = Path.Combine(root, columnar.Replace(".cdataset", ".csv"));
            return File.Exists(csvPath) ? csvPath : null;
        }

        static string ToColumnar(string datasetName) => datasetName.EndsWith(".cdataset")
            ? datasetName
            : datasetName.Replace(".csv", ".cdataset").Replace(".dataset", ".cdataset");
    }
}
