using System.Collections.Generic;
using IATK;
using UnityEngine;

namespace DataViz
{
    /// <summary>
    /// IATK DataSource backed by a CUBIA .cdataset (DatasetColumnar), so IATK can be
    /// benchmarked on exactly the same files as the Instanced/Particle/VFX pipelines -
    /// including the 1e7 tier, which IATK's own CSVDataSource cannot parse (1.5 GB CSV).
    ///
    /// Only the data path is replaced: IATK's ViewBuilder, BigMesh, shaders and axes are
    /// used unchanged. IATK expects every dimension's Data array normalised to [0,1]
    /// (as CSVDataSource does); that normalisation is done lazily per dimension on first
    /// access, using the same min-max normalisation as DatasetColumnar.GetNormalizedColumnBuffer
    /// - so a scatterplot only pays for the three dimensions it actually maps.
    /// </summary>
    public class IATKCDatasetSource : DataSource
    {
        private DatasetColumnar m_Dataset;
        private readonly List<DimensionData> m_Dimensions = new();

        public void SetDataset(DatasetColumnar dataset)
        {
            m_Dataset = dataset;
            m_Dimensions.Clear();
            if (dataset == null) return;

            for (int i = 0; i < dataset.ColumnCount; i++)
            {
                DatasetColumn col = dataset.Columns[i];
                var meta = new DimensionData.Metadata
                {
                    type = col.IsCategorical ? DataType.String : DataType.Float,
                    minValue = col.IsCategorical ? 0f : col.MinValue,
                    maxValue = col.IsCategorical ? Mathf.Max(0, col.UniqueCount - 1) : col.MaxValue,
                    binCount = col.IsCategorical ? Mathf.Max(1, col.UniqueCount) : 10,
                    categoryCount = col.IsCategorical ? col.UniqueCount : 0
                };
                m_Dimensions.Add(new DimensionData(col.Name, i, meta));
            }

            if (!isOnLoadNull()) raiseOnLoad();
        }

        private DimensionData Materialise(DimensionData d)
        {
            if (d != null && d.Data == null)
                d.setData(m_Dataset.GetNormalizedColumnBuffer(d.Index), null);
            return d;
        }

        public override bool IsLoaded => m_Dataset != null;
        public override int DimensionCount => m_Dimensions.Count;
        public override int DataCount => m_Dataset != null ? m_Dataset.RowCount : 0;

        public override DimensionData this[int index] =>
            index >= 0 && index < m_Dimensions.Count ? Materialise(m_Dimensions[index]) : null;

        public override DimensionData this[string identifier]
        {
            get
            {
                foreach (DimensionData d in m_Dimensions)
                    if (d.Identifier == identifier) return Materialise(d);
                return null;
            }
        }

        private object Denormalise(float v, DimensionData d) =>
            d == null ? (object)v : d.MetaData.minValue + v * (d.MetaData.maxValue - d.MetaData.minValue);

        public override object getOriginalValue(float normalisedValue, string identifier) => Denormalise(normalisedValue, this[identifier]);
        public override object getOriginalValue(float normalisedValue, int identifier) => Denormalise(normalisedValue, this[identifier]);
        public override object getOriginalValuePrecise(float normalisedValue, int identifier) => Denormalise(normalisedValue, this[identifier]);
        public override object getOriginalValuePrecise(float normalisedValue, string identifier) => Denormalise(normalisedValue, this[identifier]);

        public override int getNumberOfCategories(int identifier) => this[identifier]?.MetaData.categoryCount ?? 0;
        public override int getNumberOfCategories(string identifier) => this[identifier]?.MetaData.categoryCount ?? 0;

        // Data is pushed in via SetDataset(); nothing to load from disk here.
        public override void loadHeader() { }
        public override void load() { }
    }
}
