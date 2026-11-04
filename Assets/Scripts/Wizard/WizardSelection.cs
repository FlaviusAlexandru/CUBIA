using System.Collections.Generic;
using DataViz.Flow;

namespace DataViz.Wizard
{
    /// <summary>
    /// The wizard's working copy of one dataset's bookmarks (ordered) and blacklist, loaded from
    /// ColumnPrefsStore and written back only by Save(). Entries for columns the file no longer has
    /// are kept untouched, as the data scene does.
    /// </summary>
    public sealed class WizardSelection
    {
        /// <summary>Above this many bookmarks the wizard suggests keeping the list short.</summary>
        public const int ShortListLimit = 8;

        public readonly DatasetSummary Summary;
        readonly List<string> m_Bookmarks;
        readonly HashSet<string> m_Blacklist;

        public WizardSelection(DatasetSummary summary)
        {
            Summary = summary;
            m_Bookmarks = ColumnPrefsStore.GetBookmarks(summary.Name);
            m_Blacklist = ColumnPrefsStore.GetBlacklist(summary.Name);
        }

        public IReadOnlyList<string> Bookmarks => m_Bookmarks;
        public ICollection<string> Blacklist => m_Blacklist;

        /// <summary>1-based palette position among this file's columns, or 0 when not marked.</summary>
        public int BookmarkPosition(string column)
        {
            int position = 0;
            foreach (string name in m_Bookmarks)
            {
                if (!HasColumn(name))
                    continue;
                position++;
                if (name == column)
                    return position;
            }
            return 0;
        }

        public bool IsBookmarked(string column) => m_Bookmarks.Contains(column);
        public bool IsLeftOut(string column) => m_Blacklist.Contains(column);

        /// <summary>Tap on a column: marks it (appended: tap order = palette order) or unmarks it.
        /// A marked column matters, so it is also taken off the leave-out list.</summary>
        public void ToggleBookmark(string column)
        {
            if (!m_Bookmarks.Remove(column))
            {
                m_Bookmarks.Add(column);
                m_Blacklist.Remove(column);
            }
        }

        public void SetLeftOut(string column, bool leftOut)
        {
            if (leftOut)
                m_Blacklist.Add(column);
            else
                m_Blacklist.Remove(column);
        }

        public void LeaveOutAllUnmarked()
        {
            foreach (ColumnSummary column in Summary.Columns)
                if (!IsBookmarked(column.Name))
                    m_Blacklist.Add(column.Name);
        }

        /// <summary>Back to what is saved (Skip).</summary>
        public void RevertBookmarks()
        {
            m_Bookmarks.Clear();
            m_Bookmarks.AddRange(ColumnPrefsStore.GetBookmarks(Summary.Name));
        }

        public void RevertBlacklist()
        {
            m_Blacklist.Clear();
            m_Blacklist.UnionWith(ColumnPrefsStore.GetBlacklist(Summary.Name));
        }

        /// <summary>Counts over this file's columns: important, left out, neither.</summary>
        public (int important, int leftOut, int neither) Counts()
        {
            int important = 0, leftOut = 0;
            foreach (ColumnSummary column in Summary.Columns)
            {
                if (IsBookmarked(column.Name))
                    important++;
                else if (IsLeftOut(column.Name))
                    leftOut++;
            }
            return (important, leftOut, Summary.Columns.Count - important - leftOut);
        }

        /// <summary>The X/Y/Z the data scene will open with (see FirstAxisMapping).</summary>
        public int[] FirstAxes()
        {
            var names = new List<string>(Summary.Columns.Count);
            foreach (ColumnSummary column in Summary.Columns)
                names.Add(column.Name);
            return FirstAxisMapping.Pick(names, i => Summary.Columns[i].IsAxisCandidate, m_Bookmarks, m_Blacklist);
        }

        public void Save()
        {
            ColumnPrefsStore.SetBookmarks(Summary.Name, m_Bookmarks);
            ColumnPrefsStore.SetBlacklist(Summary.Name, m_Blacklist);
        }

        bool HasColumn(string name)
        {
            foreach (ColumnSummary column in Summary.Columns)
                if (column.Name == name)
                    return true;
            return false;
        }
    }
}
