using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DataViz.Study
{
    /// <summary>
    /// Marks a static method `static void Name(SelfTestTally t)` in this (editor) assembly as a
    /// self-test. StudySelfTests.Run() finds and runs every one after its own checks, ordered by
    /// type name then method name, and adds the results to the same PASS / FAIL report. A test that
    /// throws counts as one failure named after the attribute.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SelfTestAttribute : Attribute
    {
        public string Name { get; }
        public SelfTestAttribute(string name) => Name = name ?? "";
    }

    /// <summary>Pass/fail counter handed to every [SelfTest] method.</summary>
    public sealed class SelfTestTally
    {
        public int Pass;
        public readonly List<string> Failures = new List<string>();

        public void Fail(string name, string detail) => Failures.Add("FAIL " + name + ": " + detail);

        public void True(string name, bool condition)
        {
            if (condition) Pass++;
            else Fail(name, "expected true");
        }

        public void Eq(string name, string expected, string actual)
        {
            if (expected == actual) Pass++;
            else Fail(name, "expected <" + expected + "> got <" + actual + ">");
        }

        public void Eq(string name, int expected, int actual) =>
            Eq(name, expected.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture));

        public void Eq(string name, bool expected, bool actual) =>
            Eq(name, expected ? "true" : "false", actual ? "true" : "false");

        /// <summary>|expected - actual| &lt;= tolerance (NaN never passes).</summary>
        public void Near(string name, float expected, float actual, float tolerance = 1e-5f)
        {
            if (Math.Abs(expected - actual) <= tolerance) Pass++;
            else Fail(name, "expected " + expected.ToString("R", CultureInfo.InvariantCulture) +
                            " got " + actual.ToString("R", CultureInfo.InvariantCulture) +
                            " (tolerance " + tolerance.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }

    /// <summary>Runner and fixtures for [SelfTest] methods.</summary>
    public static class SelfTestKit
    {
        /// <summary>Runs every [SelfTest] method of this assembly into t; returns how many ran.</summary>
        public static int RunAll(SelfTestTally t)
        {
            var tests = new List<(string order, string name, MethodInfo method)>();
            foreach (Type type in typeof(SelfTestKit).Assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    SelfTestAttribute attribute = method.GetCustomAttribute<SelfTestAttribute>();
                    if (attribute == null)
                        continue;
                    tests.Add((type.FullName + "." + method.Name, attribute.Name, method));
                }
            }
            tests.Sort((a, b) => string.CompareOrdinal(a.order, b.order));

            foreach (var test in tests)
            {
                ParameterInfo[] parameters = test.method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(SelfTestTally))
                {
                    t.Fail(test.name, test.order + " must be static void (SelfTestTally t)");
                    continue;
                }

                try
                {
                    test.method.Invoke(null, new object[] { t });
                }
                catch (TargetInvocationException e)
                {
                    Exception inner = e.InnerException ?? e;
                    t.Fail(test.name, "threw " + inner.GetType().Name + ": " + inner.Message);
                }
                catch (Exception e)
                {
                    t.Fail(test.name, "threw " + e.GetType().Name + ": " + e.Message);
                }
            }
            return tests.Count;
        }

        // ---- fixtures ------------------------------------------------------------------------

        /// <summary>
        /// A numeric column for NumericDataset. values has one entry per row; a null entry is an
        /// empty cell (stored as 0, like the preprocessor does). Type defaults to Numeric.
        /// </summary>
        public sealed class Col
        {
            public string Name;
            public float?[] Values;
            public string[] Categories;   // set for a categorical column (Values then hold category indices)
            public DataValueType Type = DataValueType.Numeric;

            public static Col Num(string name, params float?[] values) => new Col { Name = name, Values = values };

            /// <summary>Categorical column: labels per row (null = empty cell); the table is first-seen order.</summary>
            public static Col Cat(string name, params string[] labels)
            {
                var table = new List<string>();
                var values = new float?[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    if (labels[i] == null) continue;
                    int index = table.IndexOf(labels[i]);
                    if (index < 0) { table.Add(labels[i]); index = table.Count - 1; }
                    values[i] = index;
                }
                return new Col { Name = name, Values = values, Categories = table.ToArray(), Type = DataValueType.Categorical };
            }
        }

        /// <summary>
        /// An in-memory dataset shaped like a loaded .cdataset: MinValue/MaxValue over ALL rows,
        /// including the 0 stored for an empty cell (what preprocess_csv_columnar.py writes), and a
        /// validity mask only on columns with an empty cell. All columns need the same row count.
        /// </summary>
        public static DatasetColumnar Dataset(string name, params Col[] columns)
        {
            int rows = columns.Length > 0 ? columns[0].Values.Length : 0;
            var ds = new DatasetColumnar(name, rows, columns.Length);
            for (int c = 0; c < columns.Length; c++)
            {
                Col col = columns[c];
                var column = new DatasetColumn(col.Name) { Type = col.Type };
                ds.Columns.Add(column);
                ds.ColumnMapping[col.Name] = c;

                byte[] mask = null;
                for (int r = 0; r < rows; r++)
                {
                    if (col.Values[r].HasValue) continue;
                    mask ??= FullMask(rows);
                    mask[r >> 3] &= (byte)~(1 << (r & 7));
                }

                if (col.Categories != null)
                {
                    var indices = new int[rows];
                    for (int r = 0; r < rows; r++)
                        indices[r] = (int)(col.Values[r] ?? 0f);
                    ds.SetCategoricalColumn(c, indices, col.Categories);
                    foreach (string label in col.Categories)
                        column.UniqueValues.Add(label);
                }
                else
                {
                    var values = new float[rows];
                    float min = float.MaxValue, max = float.MinValue;
                    for (int r = 0; r < rows; r++)
                    {
                        values[r] = col.Values[r] ?? 0f;
                        min = Math.Min(min, values[r]);
                        max = Math.Max(max, values[r]);
                    }
                    ds.SetNumericColumn(c, values);
                    if (rows > 0)
                    {
                        column.MinValue = min;
                        column.MaxValue = max;
                    }
                }

                ds.SetValidityMask(c, mask);
            }
            ds.FinalizeDatasetMetadata();
            return ds;
        }

        static byte[] FullMask(int rows)
        {
            var mask = new byte[(rows + 7) / 8];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = 0xFF;
            return mask;
        }

        /// <summary>
        /// Runs body with a throwaway MultiplayerScatterplotManager on a hidden, unsaved GameObject
        /// (Awake does not run in edit mode; the stack is created lazily from the default seeds:
        /// one layer "L1"). fixtures are registered in DatasetRegistry under their names for the
        /// duration and unregistered afterwards, so requests can load them without touching disk.
        /// </summary>
        public static void WithManager(Action<MultiplayerScatterplotManager> body, params (string fileName, DatasetColumnar dataset)[] fixtures)
        {
            GameObject go = EditorUtility.CreateGameObjectWithHideFlags("SelfTest Manager", HideFlags.HideAndDontSave);
            foreach (var f in fixtures)
                DatasetRegistry.Register(f.fileName, f.dataset);
            try
            {
                MultiplayerScatterplotManager manager = go.AddComponent<MultiplayerScatterplotManager>();
                body(manager);
            }
            finally
            {
                foreach (var f in fixtures)
                    DatasetRegistry.Unregister(f.fileName);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
