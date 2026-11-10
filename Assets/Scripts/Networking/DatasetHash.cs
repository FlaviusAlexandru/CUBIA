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

        /// <summary>
        /// Edit-mode tests only: when set, Of returns this function's answer instead of hashing a
        /// file (the self-tests' datasets are registered in memory and have no file). Leave null.
        /// </summary>
        public static Func<string, string> LocalHashOverride;

        /// <summary>Hex SHA-256 of the local file for datasetName, or "" if there is no such file.</summary>
        public static string Of(string datasetName)
        {
            if (LocalHashOverride != null)
                return LocalHashOverride(datasetName) ?? "";

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

        /// <summary>
        /// LFR-30 / LNFR-04: compares every layer of a published snapshot that names a dataset
        /// (dataset and subset layers, and computed layers frozen on a removed source) with the
        /// local files. Pure apart from localHashOf, which the caller supplies (DatasetHash.Of in
        /// the game, a table in the tests).
        /// Status (DECISIONS.md E7): Unknown when nothing is published yet; Missing when some layer's
        /// file is not here; else Mismatch when some layer's file differs; else Match (an empty
        /// stack too, Q13). So a client missing any layer's data is view-only for the whole plot.
        /// A layer whose file the host itself lacks (published hash "") is hidden here as well but
        /// does not block editing. A computed layer on a hidden source is hidden too.
        /// </summary>
        public static StackDatasetCheck CheckStack(PlotSnapshot snapshot, Func<string, string> localHashOf)
        {
            LayerSnapshot[] layers = snapshot?.Layers ?? Array.Empty<LayerSnapshot>();
            var check = new StackDatasetCheck(layers.Length);
            if (snapshot == null || !snapshot.Published)
                return check; // Unknown

            var localHashes = new Dictionary<string, string>();
            for (int i = 0; i < layers.Length; i++)
            {
                LayerSnapshot layer = layers[i];
                if (layer == null || string.IsNullOrEmpty(layer.DatasetName))
                    continue;

                LayerDatasetState state;
                if (string.IsNullOrEmpty(layer.DatasetHash))
                {
                    state = LayerDatasetState.HostMissing;
                }
                else
                {
                    if (!localHashes.TryGetValue(layer.DatasetName, out string local))
                        localHashes[layer.DatasetName] = local = localHashOf?.Invoke(layer.DatasetName) ?? "";
                    state = local.Length == 0 ? LayerDatasetState.Missing
                        : string.Equals(local, layer.DatasetHash, StringComparison.OrdinalIgnoreCase) ? LayerDatasetState.Match
                        : LayerDatasetState.Mismatch;
                }

                check.Layers[i] = state;
                check.Unavailable[i] = state != LayerDatasetState.Match;
                if (state == LayerDatasetState.Missing && !check.Missing.Contains(layer.DatasetName))
                    check.Missing.Add(layer.DatasetName);
                else if (state == LayerDatasetState.Mismatch && !check.Mismatched.Contains(layer.DatasetName))
                    check.Mismatched.Add(layer.DatasetName);
            }

            // A computed layer whose source is still in the stack draws from that source's rows.
            for (int i = 0; i < layers.Length; i++)
            {
                LayerSnapshot layer = layers[i];
                if (layer == null || layer.Kind != (byte)LayerKind.Computed || check.Layers[i] != LayerDatasetState.NotChecked)
                    continue;
                int source = Array.FindIndex(layers, l => l != null && l.Id == layer.SourceLayerId);
                if (source >= 0 && check.Unavailable[source])
                    check.Unavailable[i] = true;
            }

            check.Status = check.Missing.Count > 0 ? DatasetMatch.Missing
                : check.Mismatched.Count > 0 ? DatasetMatch.Mismatch
                : DatasetMatch.Match;
            return check;
        }

        /// <summary>
        /// False when the host published a hash for datasetName and the local file is absent or
        /// differs (PlotSnapshot.WriteTo then leaves that layer without data). True when hostHash is "".
        /// </summary>
        public static bool MatchesLocal(string datasetName, string hostHash) =>
            string.IsNullOrEmpty(hostHash) || Compare(datasetName, hostHash) == DatasetMatch.Match;

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

    /// <summary>How one layer of a published snapshot compares with the local files.</summary>
    public enum LayerDatasetState : byte
    {
        NotChecked,   // names no dataset (a computed layer on a live source, an empty seed layer)
        Match,
        Missing,      // no such file here
        Mismatch,     // the file here differs from the host's
        HostMissing,  // the host has no such file either (published hash "")
    }

    /// <summary>Result of DatasetHash.CheckStack. Arrays run parallel to the snapshot's Layers.</summary>
    public sealed class StackDatasetCheck
    {
        public DatasetMatch Status = DatasetMatch.Unknown;
        public readonly LayerDatasetState[] Layers;
        /// <summary>True where this peer must not show the layer (LocalUnavailable).</summary>
        public readonly bool[] Unavailable;
        /// <summary>Distinct dataset names not present here, in stack order.</summary>
        public readonly List<string> Missing = new();
        /// <summary>Distinct dataset names present here in another version, in stack order.</summary>
        public readonly List<string> Mismatched = new();

        public StackDatasetCheck(int layerCount)
        {
            Layers = new LayerDatasetState[layerCount];
            Unavailable = new bool[layerCount];
        }
    }
}
