using System;
using System.Collections.Generic;

namespace DataViz
{
    /// <summary>
    /// The plot's ordered layer stack. Plain C# (no Unity objects) so it can be tested in edit
    /// mode; the manager owns one and raises the events. Index 0 is the base layer. Ids ("L" + n)
    /// are never reused within a session: NextLayerNumber only grows. Every mutator bumps Version;
    /// call Touch() after changing a layer's fields directly.
    /// </summary>
    public sealed class LayerStack
    {
        public readonly List<PlotLayer> Layers = new();
        public string ActiveLayerId { get; private set; } = "";
        public int NextLayerNumber = 1;
        public int Version { get; private set; }

        /// <summary>The active layer, or null when the stack is empty.</summary>
        public PlotLayer Active => Find(ActiveLayerId);
        public int ActiveIndex => IndexOf(ActiveLayerId);
        public PlotLayer Base => Layers.Count > 0 ? Layers[0] : null;
        public int Count => Layers.Count;

        public PlotLayer Find(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (PlotLayer layer in Layers)
            {
                if (layer.Id == id)
                    return layer;
            }
            return null;
        }

        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id))
                return -1;
            for (int i = 0; i < Layers.Count; i++)
            {
                if (Layers[i].Id == id)
                    return i;
            }
            return -1;
        }

        /// <summary>"L" + NextLayerNumber++, skipping any id already in the stack.</summary>
        public string NewId()
        {
            string id;
            do
            {
                id = "L" + NextLayerNumber;
                NextLayerNumber++;
            } while (Find(id) != null);
            return id;
        }

        public void Touch() => Version++;

        /// <summary>Makes id active. False (and nothing changes) when no layer has that id.</summary>
        public bool SetActive(string id)
        {
            if (Find(id) == null)
                return false;
            if (ActiveLayerId != id)
            {
                ActiveLayerId = id;
                Version++;
            }
            return true;
        }

        /// <summary>
        /// Appends a dataset layer and makes it active. Its mapping follows LayerRules.ApplyDefaultMapping
        /// against the base layer (same-name columns, LFR-07); its name is the file name without
        /// extension, made unique.
        /// </summary>
        public PlotLayer AddDataset(string datasetName, DatasetColumnar dataset)
        {
            var layer = new PlotLayer
            {
                Id = NewId(),
                Kind = LayerKind.Dataset,
                DatasetName = datasetName ?? "",
                Dataset = dataset,
            };
            LayerRules.ApplyDefaultMapping(layer, Base);
            layer.Name = LayerRules.DefaultName(this, layer);
            Append(layer);
            return layer;
        }

        /// <summary>
        /// Appends a subset of source and makes it active: same dataset, mapping and encodings. Its
        /// filters are one entry (filterColumn, filterLabel), or a copy of the source's filters when
        /// filterColumn &lt; 0. Returns null when source is null or computed.
        /// </summary>
        public PlotLayer AddSubset(PlotLayer source, int filterColumn, string filterLabel)
        {
            if (source == null || !source.IsPointLayer)
                return null;

            PlotLayer layer = source.Clone(NewId());
            layer.Kind = LayerKind.Subset;
            layer.SourceLayerId = source.Id;
            layer.Visible = true;
            layer.HiddenInSource = false;
            layer.LocalUnavailable = source.LocalUnavailable;
            if (filterColumn >= 0)
            {
                layer.Filters = new List<MultiplayerScatterplotManager.FilterEntry>
                {
                    new MultiplayerScatterplotManager.FilterEntry { ColumnIndex = filterColumn, Label = filterLabel ?? "" },
                };
            }
            layer.Name = LayerRules.DefaultName(this, layer);
            Append(layer);
            return layer;
        }

        /// <summary>Appends a computed layer of the given kind on source and makes it active. Null when source is null or computed, or kind is None.</summary>
        public PlotLayer AddComputed(PlotLayer source, ComputedLayerKind kind)
        {
            if (source == null || !source.IsPointLayer || kind == ComputedLayerKind.None)
                return null;

            var layer = new PlotLayer
            {
                Id = NewId(),
                Kind = LayerKind.Computed,
                SourceLayerId = source.Id,
                ComputedKind = kind,
                DriverAxis = -1,
                FixedColour = new UnityEngine.Color32(255, 200, 0, 255), // amber: stands out from the cyan points
            };
            layer.Name = LayerRules.DefaultName(this, layer);
            Append(layer);
            return layer;
        }

        /// <summary>Inserts a clone named "&lt;name&gt; copy" right after the original and makes it active. Null when id is unknown.</summary>
        public PlotLayer Duplicate(string id)
        {
            int index = IndexOf(id);
            if (index < 0)
                return null;

            PlotLayer original = Layers[index];
            PlotLayer copy = original.Clone(NewId());
            copy.Name = LayerRules.UniqueName(this, (original.Name ?? "") + " copy");
            Layers.Insert(index + 1, copy);
            ActiveLayerId = copy.Id;
            Version++;
            return copy;
        }

        /// <summary>
        /// Removes a layer. Any layer may go, the last one too (Q13). When it was active, the
        /// previous layer becomes active (else the next, else none). Computed layers that used it as
        /// their source first get a frozen copy of its dataset, mapping, alignment, filters and time
        /// column, which is what they keep drawing from (LFR-22).
        /// </summary>
        public bool Remove(string id)
        {
            int index = IndexOf(id);
            if (index < 0)
                return false;

            PlotLayer removed = Layers[index];
            foreach (PlotLayer layer in Layers)
            {
                if (layer.Kind == LayerKind.Computed && layer.SourceLayerId == removed.Id)
                    FreezeSource(layer, removed);
            }

            Layers.RemoveAt(index);
            if (ActiveLayerId == removed.Id)
            {
                if (Layers.Count == 0)
                    ActiveLayerId = "";
                else
                    ActiveLayerId = Layers[Math.Max(0, index - 1)].Id;
            }
            Version++;
            return true;
        }

        /// <summary>Moves a layer to newIndex (clamped). False when id is unknown or the position does not change.</summary>
        public bool Move(string id, int newIndex)
        {
            int index = IndexOf(id);
            if (index < 0)
                return false;

            newIndex = Math.Max(0, Math.Min(Layers.Count - 1, newIndex));
            if (newIndex == index)
                return false;

            PlotLayer layer = Layers[index];
            Layers.RemoveAt(index);
            Layers.Insert(newIndex, layer);
            Version++;
            return true;
        }

        /// <summary>The stack becomes onlyLayer alone, active. Ids already handed out stay used.</summary>
        public void ResetTo(PlotLayer onlyLayer)
        {
            Layers.Clear();
            ActiveLayerId = "";
            if (onlyLayer != null)
            {
                Layers.Add(onlyLayer);
                ActiveLayerId = onlyLayer.Id;
                BumpNumberPast(onlyLayer.Id);
            }
            Version++;
        }

        /// <summary>
        /// Replaces the whole stack (snapshot apply, undo). activeId falls back to the first layer
        /// when unknown. NextLayerNumber becomes the larger of the current value, nextLayerNumber and
        /// one past every id in layers, so an id is never handed out twice.
        /// </summary>
        public void Restore(IEnumerable<PlotLayer> layers, string activeId, int nextLayerNumber)
        {
            Layers.Clear();
            if (layers != null)
            {
                foreach (PlotLayer layer in layers)
                {
                    if (layer != null)
                    {
                        Layers.Add(layer);
                        BumpNumberPast(layer.Id);
                    }
                }
            }

            NextLayerNumber = Math.Max(NextLayerNumber, nextLayerNumber);
            ActiveLayerId = Find(activeId) != null ? activeId : (Layers.Count > 0 ? Layers[0].Id : "");
            Version++;
        }

        void Append(PlotLayer layer)
        {
            Layers.Add(layer);
            ActiveLayerId = layer.Id;
            Version++;
        }

        void BumpNumberPast(string id)
        {
            if (id != null && id.Length > 1 && id[0] == 'L' && int.TryParse(id.Substring(1), out int n) && n >= NextLayerNumber)
                NextLayerNumber = n + 1;
        }

        static void FreezeSource(PlotLayer computed, PlotLayer source)
        {
            computed.DatasetName = source.DatasetName;
            computed.Dataset = source.Dataset;
            computed.XColumnIndex = source.XColumnIndex;
            computed.YColumnIndex = source.YColumnIndex;
            computed.ZColumnIndex = source.ZColumnIndex;
            computed.Normalised = source.Normalised;
            computed.Filters = PlotLayer.CopyFilters(source.Filters);
            computed.TimeColumnIndex = source.TimeColumnIndex;
            computed.LocalUnavailable = source.LocalUnavailable;
        }
    }
}
