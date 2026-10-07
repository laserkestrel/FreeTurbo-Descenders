#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

// Pure logic (no Unity, no logging), bridge-only: the bridge's Menu runs it (bridge/DescendersBridge.csproj links this file; the mod leaves it out).
namespace DescendersCyclingPOC
{
    internal sealed class WorkoutPickerEntry
    {
        public string File = "";        // path relative to the Workouts folder ("Sub\\x.xml")
        public string Path = "";        // the full path, to start it
        public string Title = "";
        public string Author = "", Description = "";   // from the workout file, for the detail line
        public float TotalS;
        public int Steps;
        public string Error;            // set when the file could not be loaded: shown, not startable
        public bool Selectable { get { return Error == null; } }
    }

    /// <summary>
    /// The workout list of the bridge window's Workouts tab (docs/REQUIREMENTS.md V2.19, docs/PLANS.md "UI"; it replaced the in-game
    /// list). The library's Workouts folder is shown one folder at a time: <see cref="Folders"/> for the folder drop-down, <see cref="Workouts"/>
    /// for the .zwo / .xml workout files of the chosen folder. Nothing is kept: every call reads the disk again, so files dropped in show up.
    /// </summary>
    internal static class WorkoutPicker
    {
        public const int FolderMax = 300;    // a folder with more workout files than this is refused: use sub-folders
        public const int FoldersMax = 500;   // the folder drop-down stops here

        /// <summary>Every folder under <paramref name="dir"/> (the Workouts folder) that holds workout files, as paths relative to it, "" (the
        /// folder itself) first, then sorted by name ignoring case. Empty when the Workouts folder does not exist. Never throws.</summary>
        public static List<string> Folders(string dir)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
                list.Add("");
                AddFolders(dir, "", list);
            }
            catch (Exception) { }
            return list;
        }

        private static void AddFolders(string dir, string sub, List<string> list)
        {
            string[] dirs = Directory.GetDirectories(System.IO.Path.Combine(dir, sub));
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                if (list.Count >= FoldersMax) return;
                string rel = System.IO.Path.Combine(sub, System.IO.Path.GetFileName(d));
                try
                {
                    if (WorkoutFiles(d).Count > 0) list.Add(rel);
                    AddFolders(dir, rel, list);
                }
                catch (Exception) { }   // a folder that cannot be read is left out
            }
        }

        /// <summary>The workouts directly in <paramref name="sub"/> (relative to <paramref name="dir"/>, the Workouts folder), sorted by name
        /// ignoring case; a file that does not load is listed with its <see cref="WorkoutPickerEntry.Error"/>. <paramref name="message"/> says
        /// why the list is empty (no workouts, too many files, cannot read). Never throws.</summary>
        public static List<WorkoutPickerEntry> Workouts(string dir, string sub, float ftpW, out string message)
        {
            message = "";
            var list = new List<WorkoutPickerEntry>();
            string here = System.IO.Path.Combine(dir ?? "", sub ?? "");
            try
            {
                if (!Directory.Exists(here)) { message = "No workouts in " + here; return list; }
                List<string> files = WorkoutFiles(here);
                if (files.Count > FolderMax) { message = files.Count + " workout files in one folder: too many to list, sort them into sub-folders"; return list; }
                foreach (string f in files)
                {
                    Workout w = WorkoutParser.Load(f, ftpW);
                    var e = new WorkoutPickerEntry
                    {
                        File = System.IO.Path.Combine(sub ?? "", System.IO.Path.GetFileName(f)), Path = f, Title = w.Name.Length > 0 ? w.Name : WorkoutParser.Stem(f),
                        TotalS = w.TotalS, Steps = w.Steps.Count, Author = w.Author, Description = w.Description,
                    };
                    if (!w.Ok) e.Error = w.Error ?? "no steps";
                    list.Add(e);
                }
                if (files.Count == 0) message = "No workouts in " + here;
            }
            catch (Exception ex) { message = "Cannot read " + here + ": " + ex.Message; }
            return list;
        }

        /// <summary>How many workout files are directly in <paramref name="sub"/> (relative to <paramref name="dir"/>), for the folder
        /// picker's detail line. 0 when it cannot be read. Never throws.</summary>
        public static int Count(string dir, string sub)
        {
            try { return WorkoutFiles(System.IO.Path.Combine(dir ?? "", sub ?? "")).Count; }
            catch (Exception) { return 0; }
        }

        /// <summary>Workout files (.zwo, .xml) directly in <paramref name="dir"/>, sorted by name ignoring case.</summary>
        private static List<string> WorkoutFiles(string dir)
        {
            var list = new List<string>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = System.IO.Path.GetExtension(f);
                if (string.Equals(ext, ".zwo", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase)) list.Add(f);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
