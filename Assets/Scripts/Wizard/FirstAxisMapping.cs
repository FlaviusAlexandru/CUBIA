using System;
using System.Collections.Generic;
using DataViz.Flow;

namespace DataViz.Wizard
{
    /// <summary>
    /// The X/Y/Z a dataset opens with after the import wizard: the first three bookmarked numeric or
    /// DateTime columns (bookmark order), topped up with the first non-blacklisted numeric/DateTime
    /// columns. Only when a dataset has fewer than three such columns do other columns fill in, as
    /// the plain 0/1/2 default would.
    /// </summary>
    public static class FirstAxisMapping
    {
        /// <summary>X/Y/Z column indices, or null when the dataset has no columns.</summary>
        public static int[] Pick(IReadOnlyList<string> columnNames, Func<int, bool> isAxisColumn,
                                 IEnumerable<string> bookmarks, ICollection<string> blacklist)
        {
            if (columnNames == null || columnNames.Count == 0)
                return null;

            var picked = new List<int>(3);
            void Add(int index)
            {
                if (index >= 0 && picked.Count < 3 && !picked.Contains(index))
                    picked.Add(index);
            }

            foreach (string name in bookmarks)
            {
                int index = IndexOf(columnNames, name);
                if (index >= 0 && isAxisColumn(index))
                    Add(index);
            }

            for (int i = 0; i < columnNames.Count; i++)
                if (isAxisColumn(i) && !blacklist.Contains(columnNames[i]))
                    Add(i);
            for (int i = 0; i < columnNames.Count; i++)
                if (isAxisColumn(i))
                    Add(i);
            for (int i = 0; i < columnNames.Count; i++)
                Add(i);

            while (picked.Count < 3) // fewer than three columns: repeat the last, like the 0/1/2 clamp
                picked.Add(picked[picked.Count - 1]);

            return picked.ToArray();
        }

        /// <summary>
        /// Pick for a .cdataset file using its saved prefs. Reads only the file header, so the data
        /// scene can set X/Y/Z before the dataset loads (one plot build instead of two).
        /// </summary>
        public static int[] ForDatasetFile(string fileName)
        {
            DatasetSummary summary = ColumnarBinaryImporter.ReadSummary(fileName, includeCellStats: false);
            if (summary == null)
                return null;

            var names = new List<string>(summary.Columns.Count);
            foreach (ColumnSummary column in summary.Columns)
                names.Add(column.Name);

            return Pick(names, i => summary.Columns[i].IsAxisCandidate,
                        ColumnPrefsStore.GetBookmarks(summary.Name), ColumnPrefsStore.GetBlacklist(summary.Name));
        }

        static int IndexOf(IReadOnlyList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
                if (names[i] == name)
                    return i;
            return -1;
        }
    }
}
