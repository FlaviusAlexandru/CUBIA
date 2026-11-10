using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using DataViz.Networking;
using UnityEngine;

namespace DataViz
{
    // Layers: the stack, the edit-layer scope, layer requests, events, shared bounds and the clock.
    public partial class MultiplayerScatterplotManager
    {
        private LayerStack m_Stack;

        // Stands in for the edit layer on an empty plot (or inside a scope whose layer is gone), so
        // the forwarders never return null. Writes to it are meaningless; requests check HasEditLayer.
        private readonly PlotLayer m_NoLayer = new PlotLayer { Id = "", Name = "" };

        // Open EditLayerScope ids, innermost last. "" = the active layer.
        private readonly List<string> m_EditScopes = new();

        // The dataset OnDatasetLoaded last announced (RaiseChanged compares against it).
        private DatasetColumnar m_AnnouncedDataset;

        private int m_BoundsSignature;
        private bool m_BoundsCached;
        private AxisBounds m_Bounds;

        /// <summary>LFR-06: the most points all layers together may put in the buffer.</summary>
        [Header("Layers")]
        [Tooltip("Shared point budget of all layers (LFR-06); the renderer's capacity may lower it.")]
        public int PointBudget = 1_000_000;

        /// <summary>The plot's layer stack. Created from the scene seeds on first use (Awake, or lazily in edit mode).</summary>
        public LayerStack Stack => EnsureStack();

        public IReadOnlyList<PlotLayer> Layers => Stack.Layers;
        public PlotLayer ActiveLayer => Stack.Active;
        public string ActiveLayerId => Stack.ActiveLayerId;
        public int ActiveLayerIndex => Stack.ActiveIndex;

        /// <summary>Layers[0], or null on an empty plot.</summary>
        public PlotLayer BaseLayer => Stack.Base;

        /// <summary>True once Start has tried its first dataset load (false in edit mode).</summary>
        public bool HasInitialised { get; private set; }

        /// <summary>
        /// Raised after a change to the stack's structure (add, remove, reorder, reset), the active
        /// layer, or a layer-only setting the existing menus do not show: name, visibility,
        /// Normalised, opacity, fixed colour, default glyph, time behaviour, end-time and label
        /// columns, hide-in-source, computed parameters, line width, availability. Mapping, filter,
        /// glyph and size changes raise only OnPlotSettingsChanged, as before layers. See RaiseChanged
        /// for the order.
        /// </summary>
        public event Action OnLayersChanged;

        /// <summary>A short text for the user (refusals of LFR-12, missing datasets, …).</summary>
        public event Action<string, PlotMessageKind> OnPlotMessage;

        public void PostPlotMessage(string text, PlotMessageKind kind)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (kind == PlotMessageKind.Info)
                Debug.Log("[MultiplayerScatterplotManager] " + text);
            else
                Debug.LogWarning("[MultiplayerScatterplotManager] " + text);
            OnPlotMessage?.Invoke(text, kind);
        }

        // ---- edit layer --------------------------------------------------------------------

        /// <summary>
        /// The layer the forwarders and legacy requests act on: the innermost EditLayerScope's
        /// layer, else the active layer. Never null: on an empty plot, or in a scope whose layer
        /// does not exist, it is a private placeholder and HasEditLayer is false.
        /// </summary>
        public PlotLayer EditLayer
        {
            get
            {
                for (int i = m_EditScopes.Count - 1; i >= 0; i--)
                {
                    string id = m_EditScopes[i];
                    if (!string.IsNullOrEmpty(id))
                        return Stack.Find(id) ?? m_NoLayer;
                }
                return ActiveLayer ?? m_NoLayer;
            }
        }

        public bool HasEditLayer => !ReferenceEquals(EditLayer, m_NoLayer);

        /// <summary>
        /// Points the forwarders and legacy requests at layerId until disposed. Nestable; null or ""
        /// means "the active layer" (no override). An unknown id makes HasEditLayer false inside,
        /// so every mutating request is a no-op there.
        ///   using (manager.EditLayerScope("L2")) { manager.RequestColorColumnRpc(3); }
        /// </summary>
        public IDisposable EditLayerScope(string layerId)
        {
            m_EditScopes.Add(layerId ?? "");
            return new EditScope(this, m_EditScopes.Count);
        }

        private sealed class EditScope : IDisposable
        {
            private MultiplayerScatterplotManager m_Owner;
            private readonly int m_Depth;

            public EditScope(MultiplayerScatterplotManager owner, int depth)
            {
                m_Owner = owner;
                m_Depth = depth;
            }

            public void Dispose()
            {
                if (m_Owner == null)
                    return;
                List<string> scopes = m_Owner.m_EditScopes;
                if (scopes.Count >= m_Depth)
                    scopes.RemoveRange(m_Depth - 1, scopes.Count - m_Depth + 1);
                m_Owner = null;
            }
        }

        // What the read forwarders show: a computed edit layer shows its source (or itself, frozen).
        private PlotLayer MappingLayer
        {
            get
            {
                PlotLayer edit = EditLayer;
                if (edit.Kind != LayerKind.Computed)
                    return edit;
                return Stack.Find(edit.SourceLayerId) ?? edit;
            }
        }

        // The id a forwarded layer-scoped request carries: the edit layer's, or the scope's unknown id.
        private string EditLayerRequestId
        {
            get
            {
                if (HasEditLayer)
                    return EditLayer.Id;
                return m_EditScopes.Count > 0 ? m_EditScopes[m_EditScopes.Count - 1] : "";
            }
        }

        // ---- bounds and clock ----------------------------------------------------------------

        /// <summary>
        /// The plot's shared axis bounds (LayerBounds.Compute over Layers), recomputed when a layer's
        /// kind, dataset, X/Y/Z, Normalised or availability changed. SharedBoundsOverride wins when set.
        /// </summary>
        public AxisBounds SharedBounds
        {
            get
            {
                if (SharedBoundsOverride is AxisBounds forced)
                    return forced;

                int signature = BoundsSignature();
                if (!m_BoundsCached || signature != m_BoundsSignature)
                {
                    m_Bounds = LayerBounds.Compute(Layers);
                    m_BoundsSignature = signature;
                    m_BoundsCached = true;
                }
                return m_Bounds;
            }
        }

        /// <summary>
        /// When set, SharedBounds returns this instead of computing (a client taking the host's
        /// bounds from the snapshot, B3). Null by default. Changing it raises nothing.
        /// </summary>
        public AxisBounds? SharedBoundsOverride { get; set; }

        /// <summary>The playback clock's range: PlotClock.Range over Layers. False when no layer has a numeric time column.</summary>
        public bool GetClockRange(out float min, out float max) => PlotClock.Range(Layers, out min, out max);

        private bool AnyLayerHasTimeColumn()
        {
            foreach (PlotLayer layer in Layers)
            {
                if (layer.IsPointLayer && layer.TimeColumnIndex >= 0)
                    return true;
            }
            return false;
        }

        private int BoundsSignature()
        {
            var h = new HashCode();
            h.Add(ColumnRange.ValidRowsOnly);
            foreach (PlotLayer layer in Layers)
            {
                h.Add(layer.Kind);
                h.Add(layer.Normalised);
                h.Add(layer.LocalUnavailable);
                h.Add(layer.Dataset != null ? RuntimeHelpers.GetHashCode(layer.Dataset) : 0);
                h.Add(layer.XColumnIndex);
                h.Add(layer.YColumnIndex);
                h.Add(layer.ZColumnIndex);
            }
            return h.ToHashCode();
        }

        // ---- events ---------------------------------------------------------------------------

        /// <summary>
        /// The one event order after any change made through the layer requests or a snapshot:
        /// 1. if the dataset LoadedDataset shows (outside any scope) differs from the one last
        ///    announced: OnDatasetLoaded, then OnBookmarksChanged (menus repopulate for the new
        ///    active layer); 2. if layersChanged: OnLayersChanged; 3. OnPlotSettingsChanged.
        /// </summary>
        private void RaiseChanged(bool layersChanged)
        {
            DatasetColumnar shown = ShownDataset();
            if (!ReferenceEquals(shown, m_AnnouncedDataset))
            {
                m_AnnouncedDataset = shown;
                OnDatasetLoaded?.Invoke();
                OnBookmarksChanged?.Invoke();
            }

            if (layersChanged)
                OnLayersChanged?.Invoke();

            OnPlotSettingsChanged?.Invoke();
        }

        /// <summary>
        /// For code that changed PlotLayer fields directly (e.g. marking a layer LocalUnavailable):
        /// bumps Stack.Version and raises the RaiseChanged sequence with layersChanged = true.
        /// </summary>
        public void NotifyLayersChanged()
        {
            Stack.Touch();
            RaiseChanged(true);
        }

        // The dataset the menus show with no scope open: the active layer's (a computed one's source's).
        private DatasetColumnar ShownDataset()
        {
            PlotLayer active = ActiveLayer;
            if (active == null)
                return null;
            if (active.Kind == LayerKind.Computed)
                return (Stack.Find(active.SourceLayerId) ?? active).Dataset;
            return active.Dataset;
        }

        private int LayerLevelSignature()
        {
            var h = new HashCode();
            h.Add(Stack.ActiveLayerId);
            foreach (PlotLayer l in Layers)
            {
                h.Add(l.Id);
                h.Add(l.Name);
                h.Add(l.Kind);
                h.Add(l.Visible);
                h.Add(l.DatasetName);
                h.Add(l.SourceLayerId);
                h.Add(l.Normalised);
                h.Add(l.Opacity);
                h.Add(LayerSnapshot.PackColour(l.FixedColour));
                h.Add(l.DefaultGlyph);
                h.Add(l.TimeBehaviour);
                h.Add(l.EndTimeColumnIndex);
                h.Add(l.LabelColumnIndex);
                h.Add(l.HiddenInSource);
                h.Add(l.ComputedKind);
                h.Add(l.DriverAxis);
                h.Add(l.OrderColumnIndex);
                h.Add(l.Smoothed);
                h.Add(l.RefValue);
                h.Add(l.SmoothSpan);
                h.Add(l.LineWidth);
                h.Add(l.LocalUnavailable);
            }
            return h.ToHashCode();
        }

        // ---- stack plumbing --------------------------------------------------------------------

        private LayerStack EnsureStack()
        {
            if (m_Stack == null)
            {
                m_Stack = new LayerStack();
                m_Stack.ResetTo(SeedLayer(m_Stack.NewId()));
            }
            return m_Stack;
        }

        // A fresh dataset layer for an empty plot, from the scene seeds.
        private PlotLayer NewBaseLayer() => SeedLayer(Stack.NewId());

        // RequestLoadDatasetRpc: keeps Layers[0] (a fresh layer if there is none or it is computed),
        // drops the rest, and resets the layer-only settings. True when the structure or active layer changed.
        private bool ResetStackForNewPlot(out PlotLayer baseLayer)
        {
            baseLayer = Stack.Base;
            bool changed = Stack.Count != 1 || baseLayer == null || Stack.ActiveLayerId != baseLayer.Id;
            if (baseLayer == null || baseLayer.Kind == LayerKind.Computed)
            {
                float pointSize = baseLayer != null ? baseLayer.PointSize : m_InitialPointSize;
                bool sizeMapped = baseLayer != null && baseLayer.SizeMapped;
                baseLayer = NewBaseLayer();
                baseLayer.PointSize = pointSize;
                baseLayer.SizeMapped = sizeMapped;
                changed = true;
            }

            var defaults = new PlotLayer();
            if (baseLayer.Kind != LayerKind.Dataset || !baseLayer.Visible || baseLayer.Normalised)
                changed = true;
            baseLayer.Kind = LayerKind.Dataset;
            baseLayer.SourceLayerId = "";
            baseLayer.Visible = true;
            baseLayer.Normalised = false;
            baseLayer.Opacity = defaults.Opacity;
            baseLayer.FixedColour = defaults.FixedColour;
            baseLayer.DefaultGlyph = defaults.DefaultGlyph;
            baseLayer.TimeBehaviour = defaults.TimeBehaviour;
            baseLayer.EndTimeColumnIndex = -1;
            baseLayer.LabelColumnIndex = -1;
            baseLayer.HiddenInSource = false;
            baseLayer.ComputedKind = ComputedLayerKind.None;
            baseLayer.LocalUnavailable = false;

            Stack.ResetTo(baseLayer);
            return changed;
        }

        // Loads fileName into layer (fresh read), names it after the file when it still has its
        // default name, shares the new instance with the other layers on that file, drops unused
        // datasets and raises OnDatasetLoaded, OnBookmarksChanged, OnPlotSettingsChanged.
        private bool LoadDatasetInto(PlotLayer layer, string fileName)
        {
            string oldName = layer.DatasetName ?? "";
            DatasetColumnar dataset = DatasetRegistry.Load(fileName, out string loadedName);
            layer.Dataset = dataset;
            if (dataset == null)
            {
                Stack.Touch();
                return false;
            }

            if (string.IsNullOrEmpty(layer.Name) || layer.Name == Path.GetFileNameWithoutExtension(oldName))
            {
                layer.Name = "";
                layer.Name = LayerRules.UniqueName(Stack, Path.GetFileNameWithoutExtension(loadedName));
            }
            layer.DatasetName = loadedName;
            layer.LocalUnavailable = false;

            string key = DatasetRegistry.Normalise(loadedName);
            foreach (PlotLayer other in Layers)
            {
                if (other != layer && DatasetRegistry.Normalise(other.DatasetName) == key)
                    other.Dataset = dataset;
            }

            Stack.Touch();
            TrimDatasets();

            m_AnnouncedDataset = ShownDataset();
            OnDatasetLoaded?.Invoke();
            OnBookmarksChanged?.Invoke();
            OnPlotSettingsChanged?.Invoke();
            return true;
        }

        // Lets the registry drop datasets no layer uses any more.
        private void TrimDatasets()
        {
            var keep = new List<string>(Layers.Count);
            foreach (PlotLayer layer in Layers)
            {
                if (!string.IsNullOrEmpty(layer.DatasetName))
                    keep.Add(layer.DatasetName);
            }
            DatasetRegistry.Trim(keep);
        }

        // Legacy requests: true (and nothing changes) when there is no layer to edit, or it is
        // computed (refused with a message; OnPlotSettingsChanged lets the menus snap back).
        private bool RefuseLayerEdit()
        {
            if (!HasEditLayer)
            {
                Debug.Log("[MultiplayerScatterplotManager] No layer to edit; request ignored.");
                return true;
            }

            if (EditLayer.Kind == LayerKind.Computed)
            {
                PostPlotMessage("A computed layer takes its mapping from its source", PlotMessageKind.Refused);
                OnPlotSettingsChanged?.Invoke();
                return true;
            }

            return false;
        }

        // LFR-12 for the X/Y/Z requests: false (after a message and a resync event) when refused.
        private bool CheckAxisMapping(int axis, int columnIndex)
        {
            if (LayerRules.CanMapAxis(Stack, EditLayer, axis, columnIndex, out string message))
                return true;

            PostPlotMessage(message, PlotMessageKind.Refused);
            OnPlotSettingsChanged?.Invoke();
            return false;
        }

        // ---- layer requests ----------------------------------------------------------------------
        // Each starts with a forward guard (the layer id travels in PlotRequest.Layer), resolves the
        // layer ("" = the active layer), changes it through Stack, then raises RaiseChanged(true).
        // A request for a layer that does not exist is dropped with a log line. Refusals post a
        // message and still raise RaiseChanged(true), so a UI that changed optimistically resyncs.

        private static bool ForwardLayerOp(PlotOp op, string layerId, int intArg = 0, float floatArg = 0f, bool boolArg = false, string text = null) =>
            NetworkScatterplotState.TryForwardToHost(new PlotRequest(op, intArg, floatArg, boolArg, text, layerId ?? ""));

        private PlotLayer ResolveLayer(string layerId) =>
            string.IsNullOrEmpty(layerId) ? ActiveLayer : Stack.Find(layerId);

        private bool TryResolve(string layerId, string request, out PlotLayer layer)
        {
            layer = ResolveLayer(layerId);
            if (layer != null)
                return true;
            Debug.Log($"[MultiplayerScatterplotManager] {request}: no layer '{layerId}', request dropped.");
            return false;
        }

        private void Refuse(string message)
        {
            PostPlotMessage(message, PlotMessageKind.Refused);
            RaiseChanged(true);
        }

        private void LayerSettingChanged()
        {
            Stack.Touch();
            RaiseChanged(true);
        }

        /// <summary>Adds fileName as a new dataset layer on top (LFR-02), mapped by LayerRules.ApplyDefaultMapping, and makes it active.</summary>
        public void RequestAddDatasetLayerRpc(string fileName)
        {
            if (ForwardLayerOp(PlotOp.AddDatasetLayer, "", text: fileName)) return;

            DatasetColumnar dataset = DatasetRegistry.Get(fileName, out string loadedName);
            if (dataset == null)
            {
                Refuse($"Could not load {fileName}");
                return;
            }

            Stack.AddDataset(loadedName, dataset);
            RaiseChanged(true);
        }

        /// <summary>Adds a subset of a point layer (LFR-03): one filter entry (filterColumn, filterLabel), or a copy of the source's filters when filterColumn &lt; 0.</summary>
        public void RequestAddSubsetLayerRpc(string sourceLayerId, int filterColumn = -1, string filterLabel = "")
        {
            if (ForwardLayerOp(PlotOp.AddSubsetLayer, sourceLayerId, filterColumn, text: filterLabel)) return;
            if (!TryResolve(sourceLayerId, "AddSubsetLayer", out PlotLayer source)) return;

            if (!source.IsPointLayer)
            {
                Refuse("A subset needs a dataset or subset layer as its source");
                return;
            }
            if (filterColumn >= 0 && (source.Dataset == null || filterColumn >= source.Dataset.ColumnCount))
            {
                Refuse("That filter column does not exist in " + source.Name);
                return;
            }

            Stack.AddSubset(source, filterColumn, filterLabel ?? "");
            RaiseChanged(true);
        }

        /// <summary>Adds a computed layer of kind on a point layer (LFR-04).</summary>
        public void RequestAddComputedLayerRpc(string sourceLayerId, ComputedLayerKind kind)
        {
            if (ForwardLayerOp(PlotOp.AddComputedLayer, sourceLayerId, (int)kind)) return;
            if (!TryResolve(sourceLayerId, "AddComputedLayer", out PlotLayer source)) return;

            if (!source.IsPointLayer || kind == ComputedLayerKind.None || !Enum.IsDefined(typeof(ComputedLayerKind), kind))
            {
                Refuse("A computed layer needs a dataset or subset layer as its source");
                return;
            }

            Stack.AddComputed(source, kind);
            RaiseChanged(true);
        }

        /// <summary>Removes a layer (any layer, the last one too: Q13). Computed layers on it keep a frozen copy (LFR-22).</summary>
        public void RequestRemoveLayerRpc(string layerId)
        {
            if (ForwardLayerOp(PlotOp.RemoveLayer, layerId)) return;
            if (!TryResolve(layerId, "RemoveLayer", out PlotLayer layer)) return;

            Stack.Remove(layer.Id);
            m_ShuffleUnlockedLayers.Remove(layer.Id);
            TrimDatasets();
            RaiseChanged(true);
        }

        public void RequestDuplicateLayerRpc(string layerId)
        {
            if (ForwardLayerOp(PlotOp.DuplicateLayer, layerId)) return;
            if (!TryResolve(layerId, "DuplicateLayer", out PlotLayer layer)) return;

            Stack.Duplicate(layer.Id);
            RaiseChanged(true);
        }

        /// <summary>Trimmed, at most 40 characters; an empty name is refused.</summary>
        public void RequestRenameLayerRpc(string layerId, string name)
        {
            if (ForwardLayerOp(PlotOp.RenameLayer, layerId, text: name)) return;
            if (!TryResolve(layerId, "RenameLayer", out PlotLayer layer)) return;

            string clean = (name ?? "").Trim();
            if (clean.Length > 40)
                clean = clean.Substring(0, 40);
            if (clean.Length == 0)
            {
                Refuse("A layer needs a name");
                return;
            }
            if (clean == layer.Name)
                return;

            layer.Name = clean;
            LayerSettingChanged();
        }

        /// <summary>Moves a layer to newIndex (0 = base, clamped).</summary>
        public void RequestReorderLayerRpc(string layerId, int newIndex)
        {
            if (ForwardLayerOp(PlotOp.ReorderLayer, layerId, newIndex)) return;
            if (!TryResolve(layerId, "ReorderLayer", out PlotLayer layer)) return;

            if (Stack.Move(layer.Id, newIndex))
                RaiseChanged(true);
        }

        /// <summary>The active layer is shared by all players (Q15).</summary>
        public void RequestSetActiveLayerRpc(string layerId)
        {
            if (ForwardLayerOp(PlotOp.SetActiveLayer, layerId)) return;
            if (!TryResolve(layerId, "SetActiveLayer", out PlotLayer layer)) return;

            if (layer.Id == Stack.ActiveLayerId)
                return;
            Stack.SetActive(layer.Id);
            RaiseChanged(true);
        }

        public void RequestSetLayerVisibleRpc(string layerId, bool visible)
        {
            if (ForwardLayerOp(PlotOp.SetLayerVisible, layerId, boolArg: visible)) return;
            if (!TryResolve(layerId, "SetLayerVisible", out PlotLayer layer)) return;

            if (layer.Visible == visible)
                return;
            layer.Visible = visible;
            LayerSettingChanged();
        }

        /// <summary>LFR-10. Switching back to aligned is refused when LayerRules.CanSetAligned says the column types clash (LFR-12).</summary>
        public void RequestSetLayerNormalisedRpc(string layerId, bool normalised)
        {
            if (ForwardLayerOp(PlotOp.SetLayerNormalised, layerId, boolArg: normalised)) return;
            if (!TryResolve(layerId, "SetLayerNormalised", out PlotLayer layer)) return;

            if (layer.Normalised == normalised)
                return;
            if (!layer.IsPointLayer)
            {
                Refuse("A computed layer follows its source's alignment");
                return;
            }
            if (!normalised)
            {
                layer.Normalised = false;
                bool ok = LayerRules.CanSetAligned(Stack, layer, out string message);
                if (!ok)
                {
                    layer.Normalised = true;
                    Refuse(message);
                    return;
                }
            }

            layer.Normalised = normalised;
            LayerSettingChanged();
        }

        /// <summary>Clamped to 0.05..1.</summary>
        public void RequestSetLayerOpacityRpc(string layerId, float opacity)
        {
            if (ForwardLayerOp(PlotOp.SetLayerOpacity, layerId, floatArg: opacity)) return;
            if (!TryResolve(layerId, "SetLayerOpacity", out PlotLayer layer)) return;
            if (!float.IsFinite(opacity)) return;

            float clamped = Mathf.Clamp(opacity, 0.05f, 1f);
            if (layer.Opacity == clamped)
                return;
            layer.Opacity = clamped;
            LayerSettingChanged();
        }

        /// <summary>Fixed colour (LFR-14); also clears the colour column (ColorColumnIndex = -1).</summary>
        public void RequestSetLayerColourRpc(string layerId, Color32 colour)
        {
            if (ForwardLayerOp(PlotOp.SetLayerColour, layerId, unchecked((int)LayerSnapshot.PackColour(colour)))) return;
            if (!TryResolve(layerId, "SetLayerColour", out PlotLayer layer)) return;

            layer.FixedColour = colour;
            layer.ColorColumnIndex = -1;
            LayerSettingChanged();
        }

        /// <summary>The layer-wide glyph (DefaultGlyph), an index into ScatterplotUI.k_GlyphShapeNames.</summary>
        public void RequestSetLayerGlyphRpc(string layerId, int glyphIndex)
        {
            if (ForwardLayerOp(PlotOp.SetLayerGlyph, layerId, glyphIndex)) return;
            if (!TryResolve(layerId, "SetLayerGlyph", out PlotLayer layer)) return;

            if (glyphIndex < 0 || glyphIndex >= ScatterplotUI.k_GlyphShapeNames.Length)
            {
                Refuse($"Unknown glyph {glyphIndex}");
                return;
            }
            if (layer.DefaultGlyph == glyphIndex)
                return;
            layer.DefaultGlyph = glyphIndex;
            LayerSettingChanged();
        }

        public void RequestSetLayerTimeBehaviourRpc(string layerId, LayerTimeBehaviour behaviour)
        {
            if (ForwardLayerOp(PlotOp.SetLayerTimeBehaviour, layerId, (int)behaviour)) return;
            if (!TryResolve(layerId, "SetLayerTimeBehaviour", out PlotLayer layer)) return;

            if (!Enum.IsDefined(typeof(LayerTimeBehaviour), behaviour) || layer.TimeBehaviour == behaviour)
                return;
            layer.TimeBehaviour = behaviour;
            LayerSettingChanged();
        }

        /// <summary>Span mode's end column; -1 = none (visible from its start onward).</summary>
        public void RequestSetLayerEndTimeColumnRpc(string layerId, int columnIndex)
        {
            if (ForwardLayerOp(PlotOp.SetLayerEndTimeColumn, layerId, columnIndex)) return;
            if (!TryResolve(layerId, "SetLayerEndTimeColumn", out PlotLayer layer)) return;
            if (!ValidColumnOrNone(layer, columnIndex)) return;

            layer.EndTimeColumnIndex = columnIndex;
            LayerSettingChanged();
        }

        /// <summary>Text labels next to the points (LFR-17); -1 = none.</summary>
        public void RequestSetLayerLabelColumnRpc(string layerId, int columnIndex)
        {
            if (ForwardLayerOp(PlotOp.SetLayerLabelColumn, layerId, columnIndex)) return;
            if (!TryResolve(layerId, "SetLayerLabelColumn", out PlotLayer layer)) return;
            if (!ValidColumnOrNone(layer, columnIndex)) return;

            layer.LabelColumnIndex = columnIndex;
            LayerSettingChanged();
        }

        /// <summary>Subset only (Q6 option c): the source layer skips the rows this subset takes.</summary>
        public void RequestSetSubsetHiddenInSourceRpc(string layerId, bool hidden)
        {
            if (ForwardLayerOp(PlotOp.SetSubsetHiddenInSource, layerId, boolArg: hidden)) return;
            if (!TryResolve(layerId, "SetSubsetHiddenInSource", out PlotLayer layer)) return;

            if (layer.Kind != LayerKind.Subset)
            {
                Refuse("Only a subset layer can hide its rows in its source");
                return;
            }
            if (layer.HiddenInSource == hidden)
                return;
            layer.HiddenInSource = hidden;
            LayerSettingChanged();
        }

        /// <summary>Computed only: DriverAxis, -1 (auto) or 0..2.</summary>
        public void RequestSetComputedAxisRpc(string layerId, int axis)
        {
            if (ForwardLayerOp(PlotOp.SetComputedAxis, layerId, axis)) return;
            if (!TryResolveComputed(layerId, "SetComputedAxis", out PlotLayer layer)) return;
            if (axis < -1 || axis > 2) return;

            layer.DriverAxis = axis;
            LayerSettingChanged();
        }

        /// <summary>Computed only: OrderColumnIndex (Path, SmoothedPath), a column of the source's dataset or -1.</summary>
        public void RequestSetComputedColumnRpc(string layerId, int columnIndex)
        {
            if (ForwardLayerOp(PlotOp.SetComputedColumn, layerId, columnIndex)) return;
            if (!TryResolveComputed(layerId, "SetComputedColumn", out PlotLayer layer)) return;

            PlotLayer source = Stack.Find(layer.SourceLayerId) ?? layer;
            if (!ValidColumnOrNone(source, columnIndex)) return;
            layer.OrderColumnIndex = columnIndex;
            LayerSettingChanged();
        }

        /// <summary>Computed only: Smoothed (Trendline curve; Reference plane).</summary>
        public void RequestSetComputedFlagRpc(string layerId, bool flag)
        {
            if (ForwardLayerOp(PlotOp.SetComputedFlag, layerId, boolArg: flag)) return;
            if (!TryResolveComputed(layerId, "SetComputedFlag", out PlotLayer layer)) return;

            layer.Smoothed = flag;
            LayerSettingChanged();
        }

        /// <summary>Computed only: RefValue (Reference), data units, finite.</summary>
        public void RequestSetComputedValueRpc(string layerId, float value)
        {
            if (ForwardLayerOp(PlotOp.SetComputedValue, layerId, floatArg: value)) return;
            if (!TryResolveComputed(layerId, "SetComputedValue", out PlotLayer layer)) return;
            if (!float.IsFinite(value)) return;

            layer.RefValue = value;
            LayerSettingChanged();
        }

        /// <summary>Computed only: SmoothSpan, clamped to 0.01..1.</summary>
        public void RequestSetComputedSpanRpc(string layerId, float span)
        {
            if (ForwardLayerOp(PlotOp.SetComputedSpan, layerId, floatArg: span)) return;
            if (!TryResolveComputed(layerId, "SetComputedSpan", out PlotLayer layer)) return;
            if (!float.IsFinite(span)) return;

            layer.SmoothSpan = Mathf.Clamp(span, 0.01f, 1f);
            LayerSettingChanged();
        }

        /// <summary>LineWidth in plot units, clamped to 0.0005..0.05.</summary>
        public void RequestSetLayerLineWidthRpc(string layerId, float width)
        {
            if (ForwardLayerOp(PlotOp.SetLayerLineWidth, layerId, floatArg: width)) return;
            if (!TryResolve(layerId, "SetLayerLineWidth", out PlotLayer layer)) return;
            if (!float.IsFinite(width)) return;

            layer.LineWidth = Mathf.Clamp(width, 0.0005f, 0.05f);
            LayerSettingChanged();
        }

        private bool TryResolveComputed(string layerId, string request, out PlotLayer layer)
        {
            if (!TryResolve(layerId, request, out layer))
                return false;
            if (layer.Kind == LayerKind.Computed)
                return true;
            Refuse(layer.Name + " is not a computed layer");
            return false;
        }

        private bool ValidColumnOrNone(PlotLayer layer, int columnIndex)
        {
            if (columnIndex == -1)
                return true;
            DatasetColumnar ds = layer.Dataset;
            if (ds != null && columnIndex >= 0 && columnIndex < ds.ColumnCount)
                return true;
            Refuse($"Column {columnIndex} does not exist in {layer.Name}");
            return false;
        }
    }
}
