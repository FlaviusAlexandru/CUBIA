using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;
using SysProcess = System.Diagnostics.Process;
using SysStartInfo = System.Diagnostics.ProcessStartInfo;
using SysStopwatch = System.Diagnostics.Stopwatch;

namespace DataViz.Study
{
    /// <summary>One converted session (Tools/WamIA/AUTO_IMPORT_CONTRACT.md, "converted").</summary>
    [Serializable]
    public sealed class WamIaConverted
    {
        public string participant;
        public int part;
        public string session;
        public string[] files;
    }

    /// <summary>One part whose outputs were replaced by a newer session (contract, "replaced").</summary>
    [Serializable]
    public sealed class WamIaReplaced
    {
        public string participant;
        public int part;
        public string oldSession;
        public string newSession;
        public string[] archived;
    }

    /// <summary>One session the converter did not use (contract, "skipped").</summary>
    [Serializable]
    public sealed class WamIaSkipped
    {
        public string session;
        public string reason;
    }

    /// <summary>
    /// The converter's WAMIA_RESULT line (contract, "Machine-readable result"), parsed with
    /// JsonUtility. Arrays are never null after <see cref="WamIaAutoImporter.TryParseResultLine"/> or
    /// a run; C#-side errors (converter missing, timeout, no result line) go into <see cref="errors"/>.
    /// </summary>
    [Serializable]
    public sealed class WamIaImportResult
    {
        public WamIaConverted[] converted;
        public WamIaReplaced[] replaced;
        public WamIaSkipped[] skipped;
        public string[] warnings;
        public string[] errors;
        public string[] participants;

        /// <summary>Wall-clock duration of the run (not part of the JSON).</summary>
        [NonSerialized] public double Seconds;

        /// <summary>True when the run converted or replaced anything, i.e. new dataset files exist.</summary>
        public bool HasChanges => (converted != null && converted.Length > 0) || (replaced != null && replaced.Length > 0);

        /// <summary>Replaces null arrays (missing JSON keys) with empty ones.</summary>
        public WamIaImportResult Normalise()
        {
            converted ??= Array.Empty<WamIaConverted>();
            replaced ??= Array.Empty<WamIaReplaced>();
            skipped ??= Array.Empty<WamIaSkipped>();
            warnings ??= Array.Empty<string>();
            errors ??= Array.Empty<string>();
            participants ??= Array.Empty<string>();
            foreach (WamIaConverted c in converted)
                if (c != null) c.files ??= Array.Empty<string>();
            foreach (WamIaReplaced r in replaced)
                if (r != null) r.archived ??= Array.Empty<string>();
            return this;
        }

        /// <summary>An empty result carrying one C#-side error.</summary>
        public static WamIaImportResult Error(string message)
        {
            var r = new WamIaImportResult().Normalise();
            r.errors = new[] { message };
            return r;
        }
    }

    /// <summary>
    /// Runs the WAM → CUBIA converter (Tools/WamIA/wam_to_cubia.py --auto … --json) in the
    /// background and reports its result on the main thread. Started by StudyDashboardBootstrap on
    /// every data-scene start and by the dashboard's "Import new logs" button. Desktop only; the
    /// Python side follows Tools/WamIA/AUTO_IMPORT_CONTRACT.md.
    /// Settings (PlayerPrefs): CUBIA.Study.PythonExe, CUBIA.Study.ConverterScript,
    /// CUBIA.Study.WamLogFolder, CUBIA.Study.WamProgram (optional).
    /// </summary>
    public static class WamIaAutoImporter
    {
        public const string PythonExeKey = "CUBIA.Study.PythonExe";
        public const string ConverterScriptKey = "CUBIA.Study.ConverterScript";
        public const string WamLogFolderKey = "CUBIA.Study.WamLogFolder";
        public const string WamProgramKey = "CUBIA.Study.WamProgram";
        public const string ResultPrefix = "WAMIA_RESULT ";
        public const int TimeoutMilliseconds = 180000;

        const string k_Tag = "[WAM+IA import]";
        const int k_TailLines = 8;
        const int k_TailChars = 800;
        static readonly CultureInfo k_Inv = CultureInfo.InvariantCulture;
        // Contract "Participant label": missing-value spellings the converter ignores.
        static readonly HashSet<string> k_Missing = new HashSet<string>(StringComparer.Ordinal) { "", "NULL", "null", "NaN", "nan" };
        static readonly Regex k_ArchiveStamp = new Regex(@" (R \d{2}-\d{2}-\d{4} \d{2}\.\d{2})", RegexOptions.CultureInvariant);

        /// <summary>True from <see cref="Run"/> until its result has been delivered on the main thread.</summary>
        public static bool IsRunning { get; private set; }

        /// <summary>The most recent result (null before the first run finishes).</summary>
        public static WamIaImportResult Last { get; private set; }

        /// <summary>Raised on the main thread after every run (after the Run callbacks).</summary>
        public static event Action<WamIaImportResult> Completed;

        static readonly List<Action<WamIaImportResult>> s_Waiting = new List<Action<WamIaImportResult>>();
        static readonly ConcurrentQueue<Action> s_MainThread = new ConcurrentQueue<Action>();
        static int s_Generation; // bumped per play session: results of a run from an earlier session are dropped
        static Runner s_Runner;

        /// <summary>Drains <see cref="s_MainThread"/> every frame (hidden, DontDestroyOnLoad).</summary>
        sealed class Runner : MonoBehaviour
        {
            void Update()
            {
                while (s_MainThread.TryDequeue(out Action action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }

        // Statics survive play mode with domain reload disabled; start clean each time.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Interlocked.Increment(ref s_Generation);
            IsRunning = false;
            Last = null;
            Completed = null;
            s_Waiting.Clear();
            while (s_MainThread.TryDequeue(out _)) { }
            s_Runner = null;
        }

        // ================================================================== settings

        /// <summary>Python executable (default "python", found on PATH).</summary>
        public static string PythonExe => Pref(PythonExeKey, "python");

        /// <summary>Full path of wam_to_cubia.py (default: &lt;project&gt;/Tools/WamIA/wam_to_cubia.py).</summary>
        public static string ConverterScript =>
            Pref(ConverterScriptKey, Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools", "WamIA", "wam_to_cubia.py")));

        /// <summary>Folder WAM writes its logs to (default: the user's Documents folder).</summary>
        public static string LogFolder => Pref(WamLogFolderKey, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        /// <summary>Only sessions with this SessionProgram are converted; empty = all.</summary>
        public static string Program => Pref(WamProgramKey, "");

        /// <summary>CUBIA's StreamingAssets/DataCSV (the converter puts .cdataset files in its ProcessedData).</summary>
        public static string OutFolder => Path.Combine(Application.streamingAssetsPath, "DataCSV");

        static string Pref(string key, string fallback)
        {
            string value = PlayerPrefs.GetString(key, "");
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        // ================================================================== run

        /// <summary>
        /// Starts a background conversion. A call while one is running starts nothing, but
        /// <paramref name="onDone"/> is still invoked when the current run finishes. Callbacks and
        /// <see cref="Completed"/> run on the main thread. On Android this does nothing (the callback
        /// gets an empty result; Completed is not raised).
        /// </summary>
        public static void Run(Action<WamIaImportResult> onDone)
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                onDone?.Invoke(new WamIaImportResult().Normalise());
                return;
            }
            if (!Application.isPlaying)
            {
                // The runner that delivers results only updates in play mode.
                onDone?.Invoke(WamIaImportResult.Error("The WAM import only runs in Play mode"));
                return;
            }

            EnsureRunner();
            if (onDone != null) s_Waiting.Add(onDone);
            if (IsRunning) return;
            IsRunning = true;

            int generation = s_Generation;
            // PlayerPrefs and Application paths are main-thread only: read them here.
            string python = PythonExe;
            string script = ConverterScript;
            string logs = LogFolder;
            string program = Program;
            string output = OutFolder;

            if (!File.Exists(script))
            {
                string message = $"WAM converter not found at {script}. Set PlayerPrefs \"{ConverterScriptKey}\" to the full path of wam_to_cubia.py.";
                Debug.LogWarning($"{k_Tag} {message}");
                // Through the queue like a real run, so callers never get the callback re-entrantly.
                s_MainThread.Enqueue(() => Deliver(generation, WamIaImportResult.Error(message), alreadyLogged: true));
                return;
            }
            if (!Directory.Exists(logs))
            {
                string message = $"WAM log folder not found: {logs}. Set PlayerPrefs \"{WamLogFolderKey}\" to the folder WAM writes its logs to.";
                Debug.LogWarning($"{k_Tag} {message}");
                s_MainThread.Enqueue(() => Deliver(generation, WamIaImportResult.Error(message), alreadyLogged: true));
                return;
            }

            var args = new List<string> { script, "--auto", "--logs", logs, "--out", output, "--json" };
            if (!string.IsNullOrEmpty(program))
            {
                args.Add("--program");
                args.Add(program);
            }
            string arguments = BuildArguments(args);
            string workingDirectory = Path.GetDirectoryName(script) ?? "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                WamIaImportResult result;
                try
                {
                    result = Execute(python, arguments, workingDirectory);
                }
                catch (Exception e)
                {
                    result = WamIaImportResult.Error($"WAM import failed: {e.Message}");
                }
                s_MainThread.Enqueue(() => Deliver(generation, result, alreadyLogged: false));
            });
        }

        static void EnsureRunner()
        {
            if (s_Runner != null) return;
            var host = new GameObject("[WAM+IA Auto Importer]") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(host);
            s_Runner = host.AddComponent<Runner>();
        }

        /// <summary>Worker thread: runs the converter and turns its output into a result. No Unity API calls except JsonUtility (thread-safe).</summary>
        static WamIaImportResult Execute(string python, string arguments, string workingDirectory)
        {
            var stdout = new List<string>();
            var stderr = new List<string>();
            var clock = SysStopwatch.StartNew();

            var info = new SysStartInfo
            {
                FileName = python,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; // participant ids / paths are not ASCII-only

            int exitCode;
            using (var process = new SysProcess { StartInfo = info })
            {
                // Both streams read asynchronously: ReadToEnd on one while the other fills its pipe deadlocks.
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (stdout) stdout.Add(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (stderr) stderr.Add(e.Data);
                };

                try
                {
                    if (!process.Start())
                        return Timed(WamIaImportResult.Error($"Could not start Python ({python})"), clock);
                }
                catch (Exception e)
                {
                    return Timed(WamIaImportResult.Error(
                        $"Could not start Python \"{python}\": {e.Message}. Set PlayerPrefs \"{PythonExeKey}\" to the full path of python.exe."), clock);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // Already exited between the timeout and Kill.
                    }
                    string tail;
                    lock (stderr) tail = Tail(stderr);
                    string message = $"WAM converter timed out after {TimeoutMilliseconds / 1000} s and was stopped";
                    return Timed(WamIaImportResult.Error(tail.Length > 0 ? $"{message}: {tail}" : message), clock);
                }
                process.WaitForExit(); // the parameterless overload waits for the async readers to finish
                exitCode = process.ExitCode;
            }

            string resultLine = null;
            string stderrTail;
            lock (stdout)
            {
                for (int i = stdout.Count - 1; i >= 0; i--)
                {
                    if (stdout[i] != null && stdout[i].StartsWith(ResultPrefix, StringComparison.Ordinal))
                    {
                        resultLine = stdout[i];
                        break;
                    }
                }
            }
            lock (stderr) stderrTail = Tail(stderr);

            if (resultLine == null || !TryParseResultLine(resultLine, out WamIaImportResult result))
            {
                string what = resultLine == null ? "gave no WAMIA_RESULT line" : "gave an unreadable WAMIA_RESULT line";
                string message = $"WAM converter {what} (exit code {exitCode.ToString(k_Inv)})";
                return Timed(WamIaImportResult.Error(stderrTail.Length > 0 ? $"{message}: {stderrTail}" : message), clock);
            }

            if (exitCode != 0 && result.errors.Length == 0)
            {
                string message = $"WAM converter exited with code {exitCode.ToString(k_Inv)}";
                result.errors = new[] { stderrTail.Length > 0 ? $"{message}: {stderrTail}" : message };
            }
            return Timed(result, clock);
        }

        static WamIaImportResult Timed(WamIaImportResult result, SysStopwatch clock)
        {
            result.Seconds = clock.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>Last few stderr lines, one line of text (for error messages).</summary>
        static string Tail(List<string> lines)
        {
            var parts = new List<string>();
            for (int i = Math.Max(0, lines.Count - k_TailLines); i < lines.Count; i++)
            {
                string line = lines[i]?.Trim();
                if (!string.IsNullOrEmpty(line)) parts.Add(line);
            }
            string text = string.Join(" | ", parts);
            return text.Length > k_TailChars ? "…" + text.Substring(text.Length - k_TailChars) : text;
        }

        /// <summary>Main thread: stores the result, logs it, then calls the waiting callbacks and Completed.</summary>
        static void Deliver(int generation, WamIaImportResult result, bool alreadyLogged)
        {
            if (generation != s_Generation) return; // a run from an earlier play session
            result = (result ?? WamIaImportResult.Error("WAM import returned nothing")).Normalise();

            IsRunning = false;
            Last = result;
            Log(result, alreadyLogged);

            var waiting = new List<Action<WamIaImportResult>>(s_Waiting);
            s_Waiting.Clear();
            foreach (Action<WamIaImportResult> callback in waiting)
            {
                try
                {
                    callback(result);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            Action<WamIaImportResult> handlers = Completed;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<WamIaImportResult>)handler)(result);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        static void Log(WamIaImportResult r, bool errorsAlreadyLogged)
        {
            string seconds = r.Seconds.ToString("0.0", k_Inv) + " s";
            if (r.converted.Length > 0)
            {
                var parts = new List<string>();
                foreach (WamIaConverted c in r.converted)
                {
                    if (c == null) continue;
                    int files = c.files?.Length ?? 0;
                    parts.Add($"{PartName(c.participant, c.part)} ({files.ToString(k_Inv)} file{(files == 1 ? "" : "s")})");
                }
                Debug.Log($"{k_Tag} converted {string.Join(", ", parts)} in {seconds}");
            }
            else if (r.errors.Length == 0)
            {
                Debug.Log($"{k_Tag} nothing new in {seconds}");
            }

            if (r.skipped.Length > 0)
            {
                var parts = new List<string>();
                foreach (WamIaSkipped s in r.skipped)
                    if (s != null) parts.Add($"{s.session} ({s.reason})");
                Debug.Log($"{k_Tag} skipped {r.skipped.Length.ToString(k_Inv)} session(s): {string.Join(", ", parts)}");
            }
            foreach (WamIaReplaced rep in r.replaced)
                if (rep != null) Debug.LogWarning($"{k_Tag} {ReplacedMessage(rep)}");
            foreach (string warning in r.warnings)
                if (!string.IsNullOrEmpty(warning)) Debug.LogWarning($"{k_Tag} {warning}");
            if (!errorsAlreadyLogged)
                foreach (string error in r.errors)
                    if (!string.IsNullOrEmpty(error)) Debug.LogError($"{k_Tag} {error}");
        }

        // ================================================================== pure helpers (StudySelfTests)

        /// <summary>"P07", 1 → "P07 part 1".</summary>
        public static string PartName(string participant, int part) =>
            $"{(string.IsNullOrEmpty(participant) ? "?" : participant)} part {part.ToString(k_Inv)}";

        /// <summary>
        /// Toast / log text for a replaced part: "P07 part 2 replaced by a newer session; old files
        /// archived (R 09-11-2026 14.35)". The stamp comes from the first archived file name.
        /// </summary>
        public static string ReplacedMessage(WamIaReplaced replaced)
        {
            if (replaced == null) return "";
            string text = $"{PartName(replaced.participant, replaced.part)} replaced by a newer session; old files archived";
            if (replaced.archived != null)
            {
                foreach (string file in replaced.archived)
                {
                    Match match = k_ArchiveStamp.Match(file ?? "");
                    if (match.Success) return $"{text} ({match.Groups[1].Value})";
                }
            }
            return text;
        }

        /// <summary>
        /// Parses "WAMIA_RESULT {json}" into a result with no null arrays. False for any other line
        /// or JSON JsonUtility cannot read.
        /// </summary>
        public static bool TryParseResultLine(string line, out WamIaImportResult result)
        {
            result = null;
            if (line == null || !line.StartsWith(ResultPrefix, StringComparison.Ordinal)) return false;
            string json = line.Substring(ResultPrefix.Length).Trim();
            if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal)) return false;
            try
            {
                result = JsonUtility.FromJson<WamIaImportResult>(json);
            }
            catch (Exception)
            {
                result = null;
            }
            if (result == null) return false;
            result.Normalise();
            return true;
        }

        /// <summary>
        /// Contract "Participant label": an integer ID n → "P" + n with at least 2 digits ("7" →
        /// "P07", "0" → "P00", "12" → "P12"); an ID that is already "P"/"p" + digits keeps its number
        /// ("P07" → "P07"); anything else → "P" + the ID without characters outside [A-Za-z0-9_-]
        /// ("abc!" → "Pabc"). Missing values ("", "NULL", "NaN", …) and IDs with nothing left → "".
        /// </summary>
        public static string ParticipantLabel(string rawId)
        {
            string id = (rawId ?? "").Trim();
            if (k_Missing.Contains(id)) return "";

            string digits = id;
            if (id.Length > 1 && (id[0] == 'P' || id[0] == 'p') && AllAsciiDigits(id, 1))
                digits = id.Substring(1);
            if (AllAsciiDigits(digits, 0))
            {
                // Strip leading zeros by hand so arbitrarily long digit strings never overflow.
                string number = digits.TrimStart('0');
                if (number.Length == 0) number = "0";
                return "P" + (number.Length < 2 ? number.PadLeft(2, '0') : number);
            }

            var sb = new StringBuilder("P", id.Length + 1);
            foreach (char c in id)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            }
            return sb.Length > 1 ? sb.ToString() : "";
        }

        static bool AllAsciiDigits(string text, int start)
        {
            if (text == null || start >= text.Length) return false;
            for (int i = start; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }

        /// <summary>The number of a "P" + digits label ("P07" → 7); false for other labels.</summary>
        public static bool TryLabelNumber(string label, out long number)
        {
            number = 0;
            if (label == null || label.Length < 2 || label[0] != 'P' || !AllAsciiDigits(label, 1)) return false;
            return long.TryParse(label.Substring(1), NumberStyles.None, k_Inv, out number);
        }

        /// <summary>Distinct non-empty labels: numbered ones by number, then the rest by ordinal text.</summary>
        public static List<string> SortParticipants(IEnumerable<string> labels)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (labels != null)
            {
                foreach (string label in labels)
                {
                    string l = label?.Trim();
                    if (!string.IsNullOrEmpty(l) && seen.Add(l)) distinct.Add(l);
                }
            }
            distinct.Sort(CompareLabels);
            return distinct;
        }

        static int CompareLabels(string a, string b)
        {
            bool numA = TryLabelNumber(a, out long na);
            bool numB = TryLabelNumber(b, out long nb);
            if (numA && numB)
            {
                int byNumber = na.CompareTo(nb);
                return byNumber != 0 ? byNumber : string.CompareOrdinal(a, b);
            }
            if (numA != numB) return numA ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }

        /// <summary>The label with the highest number (the newest participant), else the last label, else null.</summary>
        public static string DefaultParticipant(IList<string> sortedLabels)
        {
            if (sortedLabels == null || sortedLabels.Count == 0) return null;
            string best = null;
            long bestNumber = long.MinValue;
            foreach (string label in sortedLabels)
            {
                if (TryLabelNumber(label, out long n) && n >= bestNumber)
                {
                    bestNumber = n;
                    best = label;
                }
            }
            return best ?? sortedLabels[sortedLabels.Count - 1];
        }

        /// <summary>
        /// Participant labels from every *_Meta.csv in <paramref name="logFolder"/> (non-recursive;
        /// ';'-separated with a header row; ParticipantID column), distinct and sorted like
        /// <see cref="SortParticipants"/>. Only that column is read (Meta files hold personal data).
        /// Never throws: missing folders and locked or half-written files are skipped.
        /// </summary>
        public static List<string> ScanParticipants(string logFolder)
        {
            var labels = new List<string>();
            if (string.IsNullOrWhiteSpace(logFolder)) return labels;

            string[] files;
            try
            {
                if (!Directory.Exists(logFolder)) return labels;
                files = Directory.GetFiles(logFolder, "*_Meta.csv", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                return labels;
            }

            foreach (string file in files)
            {
                // Windows matches "*.csv" patterns against longer extensions too; check the real name.
                if (!file.EndsWith("_Meta.csv", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    ReadParticipantIds(file, labels);
                }
                catch (Exception)
                {
                    // Locked by WAM, deleted meanwhile, or not text: try again on the next scan.
                }
            }
            return SortParticipants(labels);
        }

        static void ReadParticipantIds(string path, List<string> labels)
        {
            // ReadWrite|Delete sharing: WAM may still be writing the file, and we must not block it.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
            {
                string header = reader.ReadLine();
                if (header == null) return;
                string[] columns = header.Split(';');
                int index = -1;
                for (int i = 0; i < columns.Length; i++)
                {
                    if (string.Equals(Unquote(columns[i]), "ParticipantID", StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0) return;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string[] cells = line.Split(';');
                    // A row with fewer cells than the header is still being written: its ID may be cut short.
                    if (cells.Length < columns.Length) continue;
                    string label = ParticipantLabel(Unquote(cells[index]));
                    if (label.Length > 0) labels.Add(label);
                }
            }
        }

        static string Unquote(string cell)
        {
            string c = (cell ?? "").Trim();
            if (c.Length > 0 && c[0] == '﻿') c = c.Substring(1);
            if (c.Length >= 2 && c[0] == '"' && c[c.Length - 1] == '"') c = c.Substring(1, c.Length - 2);
            return c.Trim();
        }

        /// <summary>
        /// One command line from separate arguments, quoted by the Windows (CommandLineToArgvW)
        /// rules, which Mono also follows: quotes when needed, backslashes doubled only before a quote.
        /// </summary>
        public static string BuildArguments(IEnumerable<string> arguments)
        {
            var sb = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (sb.Length > 0) sb.Append(' ');
                AppendQuoted(sb, argument ?? "");
            }
            return sb.ToString();
        }

        static void AppendQuoted(StringBuilder sb, string argument)
        {
            bool needsQuotes = argument.Length == 0;
            foreach (char c in argument)
            {
                if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }
            if (!needsQuotes)
            {
                sb.Append(argument);
                return;
            }

            sb.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2); // before the closing quote
            sb.Append('"');
        }
    }
}
