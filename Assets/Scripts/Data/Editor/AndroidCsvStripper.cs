using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Keeps the raw CSVs in StreamingAssets/DataCSV (~230 MB) out of Android (Quest) builds: the app
/// only loads the processed .cdataset files. Before an Android build the CSVs are moved to
/// &lt;project&gt;/BuildStash/StreamingCsv (outside Assets, git-ignored) with their .meta files,
/// and moved back as soon as the build ends, whether it succeeded or not.
/// If the editor stopped mid-build, they are put back the next time scripts load.
/// </summary>
[InitializeOnLoad]
public sealed class AndroidCsvStripper : IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 10;

    static string SourceFolder => Path.Combine(Application.streamingAssetsPath, "DataCSV");
    static string StashFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BuildStash", "StreamingCsv"));

    static AndroidCsvStripper()
    {
        // Leftovers from an interrupted build; also covers builds that failed before post-process.
        if (!BuildPipeline.isBuildingPlayer) Restore();
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android) return;
        Restore(); // never stash on top of an earlier stash

        Directory.CreateDirectory(StashFolder);
        int moved = 0;
        long bytes = 0;
        foreach (string csv in Directory.GetFiles(SourceFolder, "*.csv"))
        {
            bytes += new FileInfo(csv).Length;
            File.Move(csv, Path.Combine(StashFolder, Path.GetFileName(csv)));
            // Move the .meta too: Unity deletes a .meta whose asset vanished, which would give the CSV a new GUID.
            if (File.Exists(csv + ".meta")) File.Move(csv + ".meta", Path.Combine(StashFolder, Path.GetFileName(csv) + ".meta"));
            moved++;
        }
        Debug.Log($"[AndroidCsvStripper] Left {moved} CSVs ({bytes / 1048576f:F0} MB) out of the Android build.");

        // Post-process does not run when a build fails, so also restore once the build is over.
        EditorApplication.update += RestoreWhenBuildEnds;
    }

    public void OnPostprocessBuild(BuildReport report) => Restore();

    static void RestoreWhenBuildEnds()
    {
        if (BuildPipeline.isBuildingPlayer) return;
        EditorApplication.update -= RestoreWhenBuildEnds;
        Restore();
    }

    static void Restore()
    {
        if (!Directory.Exists(StashFolder)) return;
        int restored = 0;
        foreach (string csv in Directory.GetFiles(StashFolder, "*.csv"))
        {
            string target = Path.Combine(SourceFolder, Path.GetFileName(csv));
            if (File.Exists(target)) continue; // a newer copy was added meanwhile: keep both, nothing is lost
            File.Move(csv, target);
            if (File.Exists(csv + ".meta") && !File.Exists(target + ".meta")) File.Move(csv + ".meta", target + ".meta");
            restored++;
        }
        if (Directory.GetFileSystemEntries(StashFolder).Length == 0) Directory.Delete(StashFolder);
        if (restored > 0) Debug.Log($"[AndroidCsvStripper] Put {restored} CSVs back in StreamingAssets/DataCSV.");
    }
}
