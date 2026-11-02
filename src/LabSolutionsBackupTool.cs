// LabSolutions Backup Tool - portable, single .exe (.NET Framework 4, included in Windows 10/11).
//
// A small window for the LabSolutions System Backup function (Shimadzu manual 223-11702D):
//   - finds the LabSolutions folders on this PC (usual places, registry, then a search of all drives)
//   - edits <LabSolutions>\System\config\LSSSystemBackupConfiguration.xml
//     (a dated copy of the old file is kept next to it)
//   - creates the folders and warns about mapped drives / low disk space
//   - runs LSSSystemBackup.exe (full) or LSSSystemBackup_diff.bat (differential)
//   - checks the result files the backup creates
//   - deletes old backups (keeps the newest N)
//   - creates the Task Scheduler job for automatic backups
//   - restores a full or differential backup (LSSSystemRestoreConfiguration.xml,
//     stops LSSService, runs LSSSystemRestore.exe, checks the restore log)
//
// Command line (used by the scheduled task):
//   LabSolutionsBackupTool.exe --unattended [--keep N] [--diff]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("LabSolutions Backup Tool")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

static class Paths
{
    public const string AppTitle = "LabSolutions Backup Tool";

    // Found on this PC by Locator, or chosen by the user. Null until then.
    public static string SystemDir;    // usually C:\LabSolutions (holds System\config)
    public static string ProgramDir;   // usually C:\Program Files (x86)\LabSolutions (holds LSSSystemBackup.exe)

    public const string BackupConfigName = @"System\config\LSSSystemBackupConfiguration.xml";
    public const string RestoreConfigName = @"System\config\LSSSystemRestoreConfiguration.xml";
    public static string Config { get { return In(SystemDir, BackupConfigName); } }
    public static string RestoreConfig { get { return In(SystemDir, RestoreConfigName); } }
    public static string LabSolutionsDir { get { return ProgramDir; } }
    public static string BackupExe { get { return In(ProgramDir, "LSSSystemBackup.exe"); } }
    public static string DiffBat { get { return In(ProgramDir, "LSSSystemBackup_diff.bat"); } }
    public static string RestoreExe { get { return In(ProgramDir, "LSSSystemRestore.exe"); } }
    public const string ServiceName = "LSSService";

    // The tool keeps its log and the found locations here, and copies itself here when a
    // schedule needs it, so the schedule keeps working after the USB stick is removed.
    public static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabSolutionsBackupTool");
    public static readonly string ToolExe = Path.Combine(ToolDir, "LabSolutionsBackupTool.exe");
    public static readonly string Log = Path.Combine(ToolDir, "LabSolutionsBackupTool.log");
    public static readonly string LocationsFile = Path.Combine(ToolDir, "locations.txt");
    public static readonly string UnpackDir = Path.Combine(ToolDir, "Restore");
    public const string TaskName = "LabSolutions System Backup";
    public const double LowSpaceGB = 20;

    static string In(string dir, string name)
    {
        return dir == null ? "" : Path.Combine(dir, name);
    }
}

// Finds the LabSolutions folders on this PC.
static class Locator
{
    public static bool IsSystemDir(string dir)
    {
        return File.Exists(Path.Combine(dir, Paths.BackupConfigName)) || File.Exists(Path.Combine(dir, Paths.RestoreConfigName));
    }

    public static bool IsProgramDir(string dir)
    {
        return File.Exists(Path.Combine(dir, "LSSSystemBackup.exe")) || File.Exists(Path.Combine(dir, "LSSSystemRestore.exe"));
    }

    // Checks a folder and sets whichever of the two it is. Returns true when both are known.
    static bool Try(string dir, ref string system, ref string program)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return system != null && program != null;
            dir = Path.GetFullPath(dir).TrimEnd('\\');
            if (system == null && IsSystemDir(dir)) system = dir;
            if (program == null && IsProgramDir(dir)) program = dir;
        }
        catch { }
        return system != null && program != null;
    }

    // Fast: remembered locations, the LSSService registration, the installer entries, the usual folders.
    public static void FindQuick(out string system, out string program)
    {
        system = program = null;
        foreach (string dir in QuickCandidates())
            if (Try(dir, ref system, ref program)) return;
    }

    static IEnumerable<string> QuickCandidates()
    {
        var list = new List<string>();
        try
        {
            foreach (string line in File.ReadAllLines(Paths.LocationsFile))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) list.Add(line.Substring(eq + 1).Trim());
            }
        }
        catch { }

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                {
                    using (RegistryKey svc = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Paths.ServiceName))
                    {
                        string image = svc == null ? null : svc.GetValue("ImagePath") as string;
                        if (!string.IsNullOrEmpty(image)) list.AddRange(ParentsOf(ExePath(image)));
                    }
                    using (RegistryKey uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                        {
                            using (RegistryKey app = uninstall.OpenSubKey(name))
                            {
                                string display = app == null ? null : app.GetValue("DisplayName") as string;
                                if (display == null || display.IndexOf("LabSolutions", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string location = app.GetValue("InstallLocation") as string;
                                if (!string.IsNullOrEmpty(location)) list.Add(location.Trim('"'));
                                string icon = app.GetValue("DisplayIcon") as string;
                                if (!string.IsNullOrEmpty(icon)) list.AddRange(ParentsOf(ExePath(icon.Split(',')[0])));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        foreach (DriveInfo drive in FixedDrives())
        {
            string root = drive.RootDirectory.FullName;
            list.Add(Path.Combine(root, "LabSolutions"));
            list.Add(Path.Combine(root, @"Program Files (x86)\LabSolutions"));
            list.Add(Path.Combine(root, @"Program Files\LabSolutions"));
            list.Add(Path.Combine(root, @"Shimadzu\LabSolutions"));
        }
        return list;
    }

    static string ExePath(string commandLine)
    {
        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (commandLine.StartsWith("\""))
        {
            int end = commandLine.IndexOf('"', 1);
            return end > 0 ? commandLine.Substring(1, end - 1) : commandLine.Trim('"');
        }
        int exe = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? commandLine.Substring(0, exe + 4) : commandLine;
    }

    static IEnumerable<string> ParentsOf(string file)
    {
        var dirs = new List<string>();
        try
        {
            for (DirectoryInfo d = new FileInfo(file).Directory; d != null && dirs.Count < 3; d = d.Parent)
                dirs.Add(d.FullName);
        }
        catch { }
        return dirs;
    }

    static IEnumerable<DriveInfo> FixedDrives()
    {
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList(); }
        catch { return new List<DriveInfo>(); }
    }

    // Slow: looks through the folders on every local drive, a few levels deep.
    public static void Search(Action<string> progress, ref string system, ref string program)
    {
        string[] skip = { "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "Config.Msi" };
        foreach (DriveInfo drive in FixedDrives())
        {
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(drive.RootDirectory.FullName, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (Try(item.Key, ref system, ref program)) return;
                if (item.Value >= 4) continue;
                progress(item.Key);
                string[] subs;
                try { subs = Directory.GetDirectories(item.Key); }
                catch { continue; }
                foreach (string sub in subs)
                    if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
            }
        }
    }

    public static void Remember()
    {
        try
        {
            Directory.CreateDirectory(Paths.ToolDir);
            File.WriteAllLines(Paths.LocationsFile, new[] { "system=" + Paths.SystemDir, "program=" + Paths.ProgramDir });
        }
        catch { }
    }
}

static class Util
{
    public static event Action<string> Logged;

    public static void Log(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
        try
        {
            Directory.CreateDirectory(Paths.ToolDir);
            File.AppendAllText(Paths.Log, line + Environment.NewLine);
        }
        catch { }
        if (Logged != null) Logged(line);
    }

    public static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    // Returns the \\server\share path behind a mapped drive letter, or null.
    // An elevated program often cannot see the user's mapped drives, so the
    // registry entry of the mapping is checked as well.
    public static string MappedDriveToUnc(string path)
    {
        Match m = Regex.Match(path, @"^([A-Za-z]):");
        if (!m.Success) return null;
        string letter = m.Groups[1].Value.ToUpperInvariant();
        string root = null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            if (WNetGetConnection(letter + ":", sb, ref len) == 0) root = sb.ToString();
        }
        catch { }
        if (string.IsNullOrEmpty(root))
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Network\" + letter))
            {
                if (key != null) root = key.GetValue("RemotePath") as string;
            }
        }
        if (string.IsNullOrEmpty(root)) return null;
        return root.TrimEnd('\\') + path.Substring(2);
    }

    public static bool IsLocalDrivePath(string path)
    {
        return Regex.IsMatch(path, @"^[A-Za-z]:");
    }

    public static double? FreeSpaceGB(string path)
    {
        if (!IsLocalDrivePath(path)) return null;
        try { return Math.Round(new DriveInfo(path.Substring(0, 1)).AvailableFreeSpace / 1073741824.0, 1); }
        catch { return null; }
    }

    public static IEnumerable<string> FilesUpToDepth(string dir, int depth)
    {
        var result = new List<string>();
        try
        {
            result.AddRange(Directory.GetFiles(dir));
            if (depth > 0)
                foreach (string sub in Directory.GetDirectories(dir))
                    result.AddRange(FilesUpToDepth(sub, depth - 1));
        }
        catch { }
        return result;
    }

    public static int RunHidden(string exe, string args, out string output)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process p = Process.Start(psi))
        {
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            output = (stdout + stderr).Trim();
            return p.ExitCode;
        }
    }

    public static string Quote(string s)
    {
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}

static class Config
{
    public static XmlDocument Load(string path = null)
    {
        var xml = new XmlDocument { PreserveWhitespace = true };
        xml.Load(path ?? Paths.Config);
        return xml;
    }

    public static XmlElement Node(XmlDocument xml, string name, string section = "Backup")
    {
        return xml.SelectSingleNode("/CLSSSystemBackupRestoreConfiguration/" + section + "/" + name) as XmlElement;
    }

    public static string Get(XmlDocument xml, string name, string section = "Backup")
    {
        XmlElement node = Node(xml, name, section);
        return node == null ? "" : node.InnerText;
    }

    // Sets the values that exist in the file. Saves (after keeping a dated copy) only if something changed.
    // Returns the names that are missing from the file.
    public static List<string> Save(string path, string section, Dictionary<string, string> values)
    {
        XmlDocument xml = Load(path);
        var missing = new List<string>();
        bool changed = false;
        foreach (var v in values)
        {
            XmlElement node = Node(xml, v.Key, section);
            if (node == null) { missing.Add(v.Key); continue; }
            if (node.InnerText != v.Value)
            {
                node.InnerText = v.Value;
                node.IsEmpty = false;   // keep <WorkFolder></WorkFolder> instead of <WorkFolder />
                changed = true;
            }
        }
        if (changed)
        {
            string copy = path + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak";
            File.Copy(path, copy);
            xml.Save(path);
            Util.Log("Settings saved to " + Path.GetFileName(path) + ". Old settings copied to " + copy);
        }
        return missing;
    }
}

// Packs a backup folder into one .zip file and unpacks it again, for moving a backup to another PC.
static class Packer
{
    // The full backup a folder belongs to (a differential backup needs its full backup).
    public static string FullBackupOf(string folder)
    {
        for (var d = new DirectoryInfo(folder); d != null; d = d.Parent)
            if (Regex.IsMatch(d.Name, @"^FullBackup_\d{14}$")) return d.FullName;
        return folder;
    }

    public static void Pack(string folder, string zipPath, Action<int> progress)
    {
        var root = new DirectoryInfo(folder);
        FileInfo[] files = root.GetFiles("*", SearchOption.AllDirectories);
        long total = Math.Max(1, files.Sum(f => f.Length)), done = 0;
        // Written under a temporary name first, so a half-written file is never mistaken for a backup.
        string partial = zipPath + ".partial";
        if (File.Exists(partial)) File.Delete(partial);
        using (ZipArchive zip = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
            foreach (FileInfo f in files)
            {
                string relative = f.FullName.Substring(root.FullName.Length).TrimStart('\\').Replace('\\', '/');
                zip.CreateEntryFromFile(f.FullName, root.Name + "/" + relative, CompressionLevel.Optimal);
                done += f.Length;
                progress((int)(done * 100 / total));
            }
        }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(partial, zipPath);
    }

    // Where the .zip will be unpacked, and the backup folder it will produce.
    public static void Plan(string zipPath, string targetRoot, out string extractTo, out string backupFolder, out long size)
    {
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            List<string> names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            List<string> tops = names.Select(n => n.Split('/')[0]).Distinct().ToList();
            size = zip.Entries.Sum(e => e.Length);
            if (tops.Count == 1 && names.All(n => n.Contains("/")))
            {
                extractTo = targetRoot;
                backupFolder = Path.Combine(targetRoot, tops[0]);
            }
            else
            {
                extractTo = backupFolder = Path.Combine(targetRoot, Path.GetFileNameWithoutExtension(zipPath));
            }
        }
    }

    public static void Unpack(string zipPath, string extractTo, Action<int> progress)
    {
        string root = Path.GetFullPath(extractTo).TrimEnd('\\') + "\\";
        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        {
            long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('/', '\\');
                string path = Path.GetFullPath(Path.Combine(root, name));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("The file contains an unsafe path: " + entry.FullName);
                if (name.EndsWith("\\"))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                entry.ExtractToFile(path, true);
                done += entry.Length;
                progress((int)(done * 100 / total));
            }
        }
    }
}

class RestorePoint
{
    public string Path;
    public DateTime Date;
    public string Type;
    public bool LooksComplete;
}

static class Restore
{
    public static List<RestorePoint> Find(string backupPath)
    {
        var points = new List<RestorePoint>();
        var candidates = new List<DirectoryInfo>();
        try
        {
            var root = new DirectoryInfo(backupPath);
            candidates.Add(root);   // the chosen folder may itself be one backup
            foreach (DirectoryInfo full in root.GetDirectories("FullBackup_*"))
            {
                candidates.Add(full);
                candidates.AddRange(full.GetDirectories("DiffBackup_*"));
            }
        }
        catch { return points; }

        foreach (DirectoryInfo d in candidates)
        {
            bool complete = false;
            try { complete = d.GetFiles("LSSSystemBackupInfo*").Length > 0; } catch { }
            Match m = Regex.Match(d.Name, @"^(FullBackup|DiffBackup)_(\d{14})$");
            DateTime date;
            if (m.Success)
            {
                DateTime.TryParseExact(m.Groups[2].Value, "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.None, out date);
            }
            else if (d == candidates[0] && complete)
            {
                // A backup folder with another name, for example unpacked from a renamed .zip.
                date = d.LastWriteTime;
            }
            else continue;
            bool diff = m.Success && m.Groups[1].Value == "DiffBackup";
            points.Add(new RestorePoint
            {
                Path = d.FullName,
                Date = date,
                Type = diff ? "Differential (on full of " + FullDate(d.Parent.Name) + ")" : "Full",
                LooksComplete = complete
            });
        }
        return points.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(p => p.Date).ToList();
    }

    static string FullDate(string fullBackupName)
    {
        DateTime date;
        return DateTime.TryParseExact(fullBackupName.Replace("FullBackup_", ""), "yyyyMMddHHmmss", null,
            System.Globalization.DateTimeStyles.None, out date) ? date.ToString("yyyy-MM-dd") : fullBackupName;
    }

    // Returns false if the service does not exist.
    public static bool StopService()
    {
        using (var sc = new System.ServiceProcess.ServiceController(Paths.ServiceName))
        {
            System.ServiceProcess.ServiceControllerStatus status;
            try { status = sc.Status; }
            catch (InvalidOperationException) { return false; }
            if (status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                Util.Log("Stopping " + Paths.ServiceName + "...");
                if (status != System.ServiceProcess.ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
            }
            Util.Log(Paths.ServiceName + " is stopped.");
            return true;
        }
    }

    public static void StartService()
    {
        using (var sc = new System.ServiceProcess.ServiceController(Paths.ServiceName))
        {
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) sc.Start();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));
            Util.Log(Paths.ServiceName + " started again.");
        }
    }
}

class BackupResult
{
    public bool Ok;
    public string Summary;
    public string Report;
    public string LogFile;
    public List<string> Problems = new List<string>();
}

static class Backup
{
    public static Process Start(bool diff)
    {
        string file = diff ? Paths.DiffBat : Paths.BackupExe;
        if (!File.Exists(file)) throw new FileNotFoundException("Not found: " + file);
        return Process.Start(new ProcessStartInfo(file) { WorkingDirectory = Paths.LabSolutionsDir, UseShellExecute = true });
    }

    public static List<DirectoryInfo> FullBackups(string backupPath)
    {
        try
        {
            return new DirectoryInfo(backupPath).GetDirectories()
                .Where(d => Regex.IsMatch(d.Name, @"^FullBackup_\d{14}$"))
                .OrderByDescending(d => d.Name)
                .ToList();
        }
        catch { return new List<DirectoryInfo>(); }
    }

    public static List<DirectoryInfo> OldBackups(string backupPath, int keep)
    {
        return FullBackups(backupPath).Skip(Math.Max(keep, 1)).ToList();
    }

    public static void Delete(IEnumerable<DirectoryInfo> folders)
    {
        foreach (DirectoryInfo f in folders)
        {
            try
            {
                f.Delete(true);
                Util.Log("Deleted old backup " + f.FullName);
                string zip = f.FullName + ".zip";
                if (File.Exists(zip))
                {
                    File.Delete(zip);
                    Util.Log("Deleted old backup file " + zip);
                }
            }
            catch (Exception ex)
            {
                Util.Log("ERROR: could not delete " + f.FullName + ": " + ex.Message);
            }
        }
    }

    public static string LatestReport(string backupPath)
    {
        return Util.FilesUpToDepth(backupPath, 2)
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^LSSSystemBackupReport_.*\.pdf$", RegexOptions.IgnoreCase))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    // Looks at the files the backup wrote since 'since' and decides if it worked.
    public static BackupResult Check(string backupPath, DateTime since, int? exitCode)
    {
        var r = new BackupResult();
        List<string> files = Util.FilesUpToDepth(backupPath, 2)
            .Where(f => File.GetLastWriteTime(f) >= since.AddMinutes(-1))
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
        r.Report = files.FirstOrDefault(f =>
            Regex.IsMatch(Path.GetFileName(f), @"^LSSSystemBackupReport_.*\.pdf$", RegexOptions.IgnoreCase));
        r.LogFile = files.FirstOrDefault(f =>
            Path.GetFileName(f).StartsWith("LSSSystemBackup_", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        if (r.LogFile != null)
        {
            var bad = new Regex(@"\[ERR|\bfail(ed|ure)?\b|\bNG\b", RegexOptions.IgnoreCase);
            try
            {
                r.Problems = File.ReadLines(r.LogFile).Where(l => bad.IsMatch(l)).Take(10).Select(l => l.Trim()).ToList();
            }
            catch { }
        }

        if (exitCode.HasValue && exitCode.Value != 0)
            r.Summary = "Backup ended with error code " + exitCode.Value + ".";
        else if (r.Report == null && r.LogFile == null)
            r.Summary = "The backup created no files. The backup folder may not be reachable, or the backup did not start.";
        else if (r.Problems.Count > 0)
            r.Summary = "The backup log reports problems.";
        else if (r.Report == null)
            r.Summary = "No backup report (PDF) was created. Check the log file.";
        else
        {
            r.Ok = true;
            r.Summary = "Backup finished. Open the report to confirm it says \"Total: OK\".";
        }
        return r;
    }
}

static class Schedule
{
    public static string Status()
    {
        string output;
        if (Util.RunHidden("schtasks.exe", "/Query /TN " + Util.Quote(Paths.TaskName) + " /FO LIST /V", out output) != 0)
            return "No automatic backup is scheduled.";
        string next = Field(output, "Next Run Time");
        string last = Field(output, "Last Run Time");
        string result = Field(output, "Last Result");
        var sb = new StringBuilder("Scheduled.");
        if (next != null) sb.Append("  Next run: " + next + ".");
        if (last != null && !last.StartsWith("N/A") && !last.Contains("1999"))
            sb.Append("  Last run: " + last + (result != null ? " (result " + result + ")" : "") + ".");
        return sb.ToString();
    }

    static string Field(string text, string name)
    {
        Match m = Regex.Match(text, "^" + Regex.Escape(name) + @":\s*(.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static void Create(string taskCommand, bool weekly, DayOfWeek day, DateTime at, string user, string password)
    {
        string args = "/Create /F /TN " + Util.Quote(Paths.TaskName) +
                      " /TR " + Util.Quote(taskCommand) +
                      (weekly ? " /SC WEEKLY /D " + day.ToString().Substring(0, 3).ToUpperInvariant() : " /SC DAILY") +
                      " /ST " + at.ToString("HH:mm") +
                      " /RL HIGHEST /RU " + Util.Quote(user) + " /RP " + Util.Quote(password);
        string output;
        if (Util.RunHidden("schtasks.exe", args, out output) != 0) throw new Exception(output);
    }

    public static bool Exists()
    {
        string output;
        return Util.RunHidden("schtasks.exe", "/Query /TN " + Util.Quote(Paths.TaskName), out output) == 0;
    }

    public static void Delete()
    {
        string output;
        if (Util.RunHidden("schtasks.exe", "/Delete /F /TN " + Util.Quote(Paths.TaskName), out output) != 0)
            throw new Exception(output);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool unattended = args.Any(a => a.Equals("--unattended", StringComparison.OrdinalIgnoreCase));
        bool diff = args.Any(a => a.Equals("--diff", StringComparison.OrdinalIgnoreCase));
        int keep = 0;
        int keepIndex = Array.FindIndex(args, a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));
        if (keepIndex >= 0 && keepIndex + 1 < args.Length) int.TryParse(args[keepIndex + 1], out keep);

        if (unattended) return RunUnattended(keep, diff);

        if (!Util.IsAdmin())
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Administrator rights are needed to run a LabSolutions backup.", Paths.AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    // Used by the scheduled task: no window, everything goes to the log file.
    static int RunUnattended(int keep, bool diff)
    {
        try
        {
            Locator.FindQuick(out Paths.SystemDir, out Paths.ProgramDir);
            if (Paths.SystemDir == null || Paths.ProgramDir == null)
            {
                Util.Log("ERROR: LabSolutions was not found on this PC. Open the tool once to find it.");
                return 1;
            }
            string backupPath = Config.Get(Config.Load(), "BackupPath");
            DateTime start = DateTime.Now;
            Util.Log("Scheduled backup started (" + (diff ? "differential" : "full") + ").");
            Process p = Backup.Start(diff);
            p.WaitForExit();
            BackupResult r = Backup.Check(backupPath, start, p.ExitCode);
            Util.Log(r.Summary);
            foreach (string line in r.Problems) Util.Log("  " + line);
            if (keep > 0)
            {
                if (r.Ok) Backup.Delete(Backup.OldBackups(backupPath, keep));
                else Util.Log("Old backups were NOT deleted because this backup did not finish cleanly.");
            }
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Util.Log("ERROR: " + ex.Message);
            return 1;
        }
    }
}

class CredentialForm : Form
{
    public TextBox User = new TextBox();
    public TextBox Password = new TextBox();

    public CredentialForm(string defaultUser)
    {
        Text = "Windows account for the schedule";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(420, 170);

        Controls.Add(new Label
        {
            Text = "Windows account (with administrator rights) that runs the backup.\r\nThe task runs even when nobody is logged on.",
            Location = new Point(12, 10), Size = new Size(400, 36)
        });
        Controls.Add(new Label { Text = "User name:", Location = new Point(12, 59), Size = new Size(80, 20) });
        User.Location = new Point(96, 56); User.Size = new Size(310, 23); User.Text = defaultUser;
        Controls.Add(new Label { Text = "Password:", Location = new Point(12, 91), Size = new Size(80, 20) });
        Password.Location = new Point(96, 88); Password.Size = new Size(310, 23); Password.UseSystemPasswordChar = true;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(236, 128), Size = new Size(80, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(326, 128), Size = new Size(80, 28) };
        Controls.AddRange(new Control[] { User, Password, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (s, e) => Password.Focus();
    }
}

class MainForm : Form
{
    TextBox txtBackup, txtAuth, txtWork, txtLog;
    CheckBox chkVerify, chkSchedClean;
    RadioButton rbDiff;
    Button btnRun;
    Label lblStatus, lblSchedule;
    NumericUpDown numKeep;
    ComboBox cmbFreq, cmbDay;
    DateTimePicker dtTime;
    Timer timer = new Timer { Interval = 2000 };
    Process backupProcess;
    DateTime backupStart;
    string lastReport;

    TextBox txtRestoreRoot, txtRestoreWork;
    ListView lstRestore;
    ComboBox cmbRestoreMode;
    Button btnRestore, btnRestartPc;
    Label lblRestoreStatus;
    Timer restoreTimer = new Timer { Interval = 2000 };
    Process restoreProcess;
    DateTime restoreStart;
    string restorePath, restoreLog;
    bool serviceStopped;

    CheckBox chkPack;
    Button btnSaveZip, btnOpenZip;
    bool busy;   // packing or unpacking a .zip, or searching for LabSolutions

    TabControl tabs;
    Label lblLocation;
    Button btnSearch, btnChooseLs;

    static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
    {
        { "BackupPath", "Backup folder" },
        { "AuthBackupPath", "Authentication DB backup folder" },
        { "WorkFolder", "Temp work folder" }
    };

    static T Add<T>(Control parent, T c, int x, int y, int w, int h, string text = null) where T : Control
    {
        c.Location = new Point(x, y);
        c.Size = new Size(w, h);
        if (text != null) c.Text = text;
        parent.Controls.Add(c);
        return c;
    }

    public MainForm()
    {
        Text = Paths.AppTitle;
        ClientSize = new Size(700, 796);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // Header: where LabSolutions is on this PC
        var header = Add(this, new Panel { BackColor = Color.FromArgb(232, 240, 250) }, 0, 0, 700, 56);
        Add(header, new Label { Font = new Font("Segoe UI", 9, FontStyle.Bold) }, 12, 8, 450, 20, "LabSolutions on this PC");
        lblLocation = Add(header, new Label { AutoEllipsis = true }, 12, 28, 460, 20, "Looking for LabSolutions...");
        btnSearch = Add(header, new Button { BackColor = SystemColors.Control }, 480, 14, 96, 28, "Search again");
        btnSearch.Click += (s, e) => SearchLabSolutions();
        btnChooseLs = Add(header, new Button { BackColor = SystemColors.Control }, 584, 14, 106, 28, "Choose folder...");
        btnChooseLs.Click += (s, e) => ChooseLabSolutionsFolder();

        tabs = Add(this, new TabControl { Enabled = false }, 8, 64, 684, 522);
        var backupTab = new TabPage("Backup");
        var restoreTab = new TabPage("Restore");
        tabs.TabPages.Add(backupTab);
        tabs.TabPages.Add(restoreTab);
        tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedTab == restoreTab) RefreshRestoreList(); };

        // 1. Settings
        var grp = Add(backupTab, new GroupBox(), 6, 4, 664, 170, "1. Backup settings");
        txtBackup = FolderRow(grp, 24, "Backup folder:");
        txtAuth = FolderRow(grp, 56, "Auth DB backup folder:");
        txtWork = FolderRow(grp, 88, "Temp work folder (optional):");
        chkVerify = Add(grp, new CheckBox(), 190, 120, 250, 22, "Verify the backup (recommended)");
        Add(grp, new Button(), 516, 122, 130, 28, "Save settings").Click += (s, e) =>
        {
            if (SaveSettings()) SetStatus("Settings saved.", Color.Black);
        };

        // 2. Run
        grp = Add(backupTab, new GroupBox(), 6, 182, 664, 110, "2. Run a backup now");
        Add(grp, new RadioButton { Checked = true }, 14, 24, 120, 22, "Full backup");
        rbDiff = Add(grp, new RadioButton(), 140, 24, 360, 22, "Differential (only changes since the last full backup)");
        btnRun = Add(grp, new Button(), 14, 52, 150, 30, "Run backup now");
        btnRun.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        btnRun.Click += (s, e) => RunBackup();
        Add(grp, new Button(), 172, 52, 140, 30, "Open backup folder").Click += (s, e) => OpenBackupFolder();
        Add(grp, new Button(), 320, 52, 140, 30, "Open last report").Click += (s, e) => OpenReport();
        chkPack = Add(grp, new CheckBox(), 470, 52, 190, 34, "Also save it as one file (.zip) to move to another PC");
        lblStatus = Add(grp, new Label(), 14, 86, 630, 20, "Ready.");

        // 3. Cleanup
        grp = Add(backupTab, new GroupBox(), 6, 300, 664, 64, "3. Delete old backups");
        Add(grp, new Label(), 14, 28, 110, 20, "Keep the newest");
        numKeep = Add(grp, new NumericUpDown { Minimum = 1, Maximum = 365, Value = 5 }, 126, 25, 55, 23);
        Add(grp, new Label(), 188, 28, 140, 20, "full backups");
        Add(grp, new Button(), 466, 22, 180, 28, "Delete old backups now").Click += (s, e) => CleanUp();

        // 4. Schedule
        grp = Add(backupTab, new GroupBox(), 6, 372, 664, 118, "4. Automatic backup (Task Scheduler)");
        cmbFreq = Add(grp, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 14, 26, 90, 23);
        cmbFreq.Items.AddRange(new object[] { "Daily", "Weekly" });
        cmbFreq.SelectedIndex = 0;
        cmbDay = Add(grp, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false }, 112, 26, 110, 23);
        cmbDay.Items.AddRange(Enum.GetNames(typeof(DayOfWeek)));
        cmbDay.SelectedItem = "Friday";
        cmbFreq.SelectedIndexChanged += (s, e) => cmbDay.Enabled = (string)cmbFreq.SelectedItem == "Weekly";
        Add(grp, new Label(), 230, 29, 22, 20, "at");
        dtTime = Add(grp, new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true },
            254, 26, 70, 23);
        dtTime.Value = DateTime.Today.AddHours(22);
        chkSchedClean = Add(grp, new CheckBox(), 336, 27, 320, 22, "Also delete old backups (uses \"Keep the newest\")");
        Add(grp, new Button(), 14, 58, 170, 28, "Create / update schedule").Click += (s, e) => CreateSchedule();
        Add(grp, new Button(), 192, 58, 130, 28, "Remove schedule").Click += (s, e) => RemoveSchedule();
        lblSchedule = Add(grp, new Label(), 14, 92, 640, 20, "");

        BuildRestoreTab(restoreTab);

        // Activity log
        Add(this, new Label(), 10, 592, 200, 20, "Activity:");
        txtLog = Add(this, new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }, 8, 612, 684, 176);
        txtLog.Font = new Font("Consolas", 9);
        Util.Logged += line => txtLog.AppendText(line + "\r\n");

        timer.Tick += (s, e) => BackupTick();
        Shown += (s, e) => SearchLabSolutions();
        FormClosing += (s, e) =>
        {
            if (busy && !Ask("The tool is still working (see the status text). Close anyway? A .zip file being written or unpacked will be incomplete."))
                e.Cancel = true;
        };
    }

    void BuildRestoreTab(TabPage page)
    {
        // 1. Choose
        var grp = Add(page, new GroupBox(), 6, 4, 664, 250, "1. Choose the backup to restore");
        Add(grp, new Label(), 12, 27, 175, 20, "Where is the backup?");
        Add(grp, new Button(), 190, 21, 210, 28, "Choose a backup folder...").Click += (s, e) => ChooseRestoreFolder();
        btnOpenZip = Add(grp, new Button(), 408, 21, 244, 28, "Choose a one-file backup (.zip)...");
        btnOpenZip.Click += (s, e) => UnpackZip();
        Add(grp, new Label(), 12, 59, 175, 20, "Looking in:");
        txtRestoreRoot = Add(grp, new TextBox(), 190, 56, 370, 23);
        txtRestoreRoot.Leave += (s, e) => RefreshRestoreList();
        Add(grp, new Button(), 566, 55, 86, 25, "Refresh").Click += (s, e) => RefreshRestoreList();
        lstRestore = Add(grp, new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true
        }, 12, 88, 640, 118);
        lstRestore.Columns.Add("Backup date", 140);
        lstRestore.Columns.Add("Type", 230);
        lstRestore.Columns.Add("Folder", 246);
        btnSaveZip = Add(grp, new Button(), 12, 212, 270, 28, "Save selected backup as one file (.zip)...");
        btnSaveZip.Click += (s, e) => SaveSelectedAsZip();
        Add(grp, new Label { ForeColor = Color.DimGray }, 290, 218, 362, 20, "...to carry it to another PC, e.g. on a USB stick.");

        // 2. Options
        grp = Add(page, new GroupBox(), 6, 258, 664, 90, "2. Restore options");
        Add(grp, new Label(), 12, 27, 175, 20, "What to restore:");
        cmbRestoreMode = Add(grp, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 190, 24, 260, 23);
        cmbRestoreMode.Items.AddRange(new object[] { "Database and files (recommended)", "Files only", "Database only" });
        cmbRestoreMode.SelectedIndex = 0;
        txtRestoreWork = FolderRow(grp, 56, "Temp work folder (optional):");

        // 3. Restore
        grp = Add(page, new GroupBox(), 6, 352, 664, 136, "3. Restore");
        var warn = Add(grp, new Label(), 12, 20, 640, 36,
            "Restoring replaces the current LabSolutions database and system files with the backup. " +
            "Everything done since that backup is lost. LabSolutions stops during the restore.");
        warn.ForeColor = Color.DarkRed;
        btnRestore = Add(grp, new Button(), 12, 60, 200, 30, "Restore selected backup...");
        btnRestore.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        btnRestore.Click += (s, e) => RunRestore();
        Add(grp, new Button(), 220, 60, 140, 30, "Open restore log").Click += (s, e) => OpenRestoreLog();
        btnRestartPc = Add(grp, new Button { Enabled = false }, 368, 60, 140, 30, "Restart PC now");
        btnRestartPc.Click += (s, e) =>
        {
            if (Ask("Restart this PC now? Save any open work first."))
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
        };
        lblRestoreStatus = Add(grp, new Label(), 12, 100, 640, 20, "");

        restoreTimer.Tick += (s, e) => RestoreTick();
    }

    // Runs slow work (packing / unpacking) without freezing the window.
    void RunInBackground(string what, Label status, Action<Action<int>> work, Action whenDone)
    {
        busy = true;
        btnRun.Enabled = btnRestore.Enabled = btnSaveZip.Enabled = btnOpenZip.Enabled = false;
        btnSearch.Enabled = btnChooseLs.Enabled = false;
        status.ForeColor = Color.DarkBlue;
        status.Text = what + "...";
        int last = -1;
        Action<int> progress = pct =>
        {
            if (pct == last) return;
            last = pct;
            BeginInvoke((Action)(() => status.Text = what + "... " + pct + "%"));
        };
        var thread = new System.Threading.Thread(() =>
        {
            Exception error = null;
            try { work(progress); }
            catch (Exception ex) { error = ex; }
            BeginInvoke((Action)(() =>
            {
                busy = false;
                btnRun.Enabled = btnRestore.Enabled = btnSaveZip.Enabled = btnOpenZip.Enabled = true;
                btnSearch.Enabled = btnChooseLs.Enabled = true;
                if (error != null)
                {
                    status.ForeColor = Color.DarkRed;
                    status.Text = what + " failed.";
                    Error(what + " failed.\r\n\r\n" + error.Message);
                }
                else whenDone();
            }));
        });
        thread.IsBackground = true;
        thread.Start();
    }

    void PackInBackground(string folder, string zipPath, Label status)
    {
        Util.Log("Saving " + folder + " as one file: " + zipPath);
        RunInBackground("Saving the backup as one file", status, progress => Packer.Pack(folder, zipPath, progress), () =>
        {
            long mb = new FileInfo(zipPath).Length / 1048576;
            status.ForeColor = Color.DarkGreen;
            status.Text = "Saved as one file: " + Path.GetFileName(zipPath) + " (" + mb + " MB).";
            Info("The backup was saved as one file (" + mb + " MB):\r\n" + zipPath +
                 "\r\n\r\nCopy it to the new PC, open this tool there, and on the Restore tab click " +
                 "'Restore from a one-file backup (.zip)'.");
        });
    }

    void SaveSelectedAsZip()
    {
        if (lstRestore.SelectedItems.Count == 0)
        {
            Error("Choose a backup in the list first.");
            return;
        }
        string selected = (string)lstRestore.SelectedItems[0].Tag;
        string folder = Packer.FullBackupOf(selected);
        if (!string.Equals(folder, selected, StringComparison.OrdinalIgnoreCase))
            Util.Log("A differential backup needs its full backup, so all of " + Path.GetFileName(folder) +
                     " (with its differential backups) is saved.");
        using (var dlg = new SaveFileDialog
        {
            Title = "Save the backup as one file",
            Filter = "Backup file (*.zip)|*.zip",
            FileName = Path.GetFileName(folder) + ".zip",
            OverwritePrompt = true
        })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string zipFull = Path.GetFullPath(dlg.FileName);
            if (zipFull.StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Error("Save the file outside the backup folder itself, for example on the USB stick.");
                return;
            }
            PackInBackground(folder, zipFull, lblRestoreStatus);
        }
    }

    void UnpackZip()
    {
        string zipPath;
        using (var dlg = new OpenFileDialog { Title = "Choose the one-file backup", Filter = "Backup file (*.zip)|*.zip" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            zipPath = dlg.FileName;
        }

        string targetRoot = Paths.UnpackDir, extractTo, backupFolder;
        long size;
        try { Packer.Plan(zipPath, targetRoot, out extractTo, out backupFolder, out size); }
        catch (Exception ex)
        {
            Error("This file cannot be opened as a .zip file.\r\n\r\n" + ex.Message);
            return;
        }

        double? free = Util.FreeSpaceGB(targetRoot);
        double neededGB = Math.Round(size / 1073741824.0 + 1, 1);
        if (free.HasValue && free.Value < neededGB)
        {
            if (!Ask("The backup needs about " + neededGB + " GB to unpack, but drive " + targetRoot.Substring(0, 2) +
                     " has only " + free.Value + " GB free.\r\n\r\nChoose a folder on another local drive?"))
                return;
            using (var dlg = new FolderBrowserDialog { ShowNewFolderButton = true, Description = "Unpack the backup into:" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                targetRoot = dlg.SelectedPath;
            }
            Packer.Plan(zipPath, targetRoot, out extractTo, out backupFolder, out size);
        }

        bool replace = Directory.Exists(backupFolder);
        if (replace && !Ask("This backup was unpacked before:\r\n" + backupFolder + "\r\n\r\nReplace it with the contents of the file?"))
            return;

        Util.Log("Unpacking " + zipPath + " to " + extractTo);
        RunInBackground("Unpacking the backup", lblRestoreStatus, progress =>
        {
            if (replace) Directory.Delete(backupFolder, true);
            Directory.CreateDirectory(extractTo);
            Packer.Unpack(zipPath, extractTo, progress);
        }, () =>
        {
            txtRestoreRoot.Text = Path.GetFileName(backupFolder).StartsWith("FullBackup_") ? Path.GetDirectoryName(backupFolder) : backupFolder;
            RefreshRestoreList();
            ListViewItem match = lstRestore.Items.Cast<ListViewItem>().FirstOrDefault(i =>
                ((string)i.Tag).StartsWith(backupFolder, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Error("The file was unpacked to " + backupFolder + ", but it does not look like a LabSolutions backup.");
                return;
            }
            match.Selected = true;
            match.EnsureVisible();
            lstRestore.Focus();
            lblRestoreStatus.ForeColor = Color.DarkGreen;
            lblRestoreStatus.Text = "Unpacked. The backup is selected in the list - check the options, then click Restore.";
            Info("The backup was unpacked to:\r\n" + backupFolder + "\r\n\r\n" +
                 "It is now selected in the list. Before restoring on a new PC, LabSolutions must be installed there " +
                 "and its database created in the same folder as on the old PC (manual section 4).");
        });
    }

    // Finds LabSolutions: first the quick places, then (in the background) every local drive.
    void SearchLabSolutions()
    {
        string system, program;
        Locator.FindQuick(out system, out program);
        if (system != null && program != null)
        {
            ApplyLocation(system, program);
            return;
        }

        busy = true;
        tabs.Enabled = btnSearch.Enabled = btnChooseLs.Enabled = false;
        lblLocation.ForeColor = Color.DarkBlue;
        lblLocation.Text = "Searching all drives for LabSolutions...";
        Util.Log("LabSolutions is not in the usual places. Searching all local drives...");
        DateTime lastShown = DateTime.MinValue;
        Action<string> progress = dir =>
        {
            if ((DateTime.Now - lastShown).TotalMilliseconds < 300) return;
            lastShown = DateTime.Now;
            BeginInvoke((Action)(() => lblLocation.Text = "Searching... " + dir));
        };
        var thread = new System.Threading.Thread(() =>
        {
            try { Locator.Search(progress, ref system, ref program); }
            catch { }
            BeginInvoke((Action)(() =>
            {
                busy = false;
                btnSearch.Enabled = btnChooseLs.Enabled = true;
                if (system != null && program != null) ApplyLocation(system, program);
                else NotFound(system, program);
            }));
        });
        thread.IsBackground = true;
        thread.Start();
    }

    void ApplyLocation(string system, string program)
    {
        Paths.SystemDir = system;
        Paths.ProgramDir = program;
        Locator.Remember();
        lblLocation.ForeColor = Color.DarkGreen;
        lblLocation.Text = string.Equals(system, program, StringComparison.OrdinalIgnoreCase)
            ? system
            : system + "     (programs: " + program + ")";
        Util.Log("LabSolutions found: " + system + "  -  programs: " + program);
        tabs.Enabled = true;
        LoadSettings();
    }

    void NotFound(string system, string program)
    {
        tabs.Enabled = false;
        lblLocation.ForeColor = Color.DarkRed;
        lblLocation.Text = "LabSolutions was not found. Click 'Choose folder...' to point to it.";
        Error("LabSolutions was not found on this PC" +
              (system != null ? " (only its data folder " + system + ")" : "") +
              (program != null ? " (only its program folder " + program + ")" : "") + ".\r\n\r\n" +
              "Is LabSolutions installed? It must be installed before a backup can be restored here.\r\n\r\n" +
              "If it is installed, click 'Choose folder...' and choose the LabSolutions folder " +
              @"(the one with the System\config folder inside, usually C:\LabSolutions).");
    }

    string PickFolder(string description, Func<string, bool> isRight, string wrongMessage)
    {
        while (true)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                if (isRight(dlg.SelectedPath)) return dlg.SelectedPath;
                if (!Ask(wrongMessage + "\r\n\r\nChoose another folder?")) return null;
            }
        }
    }

    void ChooseLabSolutionsFolder()
    {
        string system, program;
        Locator.FindQuick(out system, out program);

        string picked = PickFolder(
            @"Choose the LabSolutions folder (the one with System\config inside, usually C:\LabSolutions).",
            d => Locator.IsSystemDir(d) || Locator.IsProgramDir(d),
            @"This is not a LabSolutions folder. It should contain the folder System\config " +
            "(data folder), or LSSSystemBackup.exe (program folder).");
        if (picked == null) return;
        if (Locator.IsSystemDir(picked)) system = picked;
        if (Locator.IsProgramDir(picked)) program = picked;

        if (program == null)
            program = PickFolder(
                @"Now choose the LabSolutions program folder (with LSSSystemBackup.exe, usually C:\Program Files (x86)\LabSolutions).",
                Locator.IsProgramDir, "LSSSystemBackup.exe / LSSSystemRestore.exe are not in this folder.");
        if (system == null)
            system = PickFolder(
                @"Now choose the LabSolutions data folder (with System\config inside, usually C:\LabSolutions).",
                Locator.IsSystemDir, @"There is no System\config folder with the backup settings in this folder.");
        if (system != null && program != null) ApplyLocation(system, program);
    }

    void ChooseRestoreFolder()
    {
        string picked = PickFolder(
            "Choose the folder with the backup: a FullBackup_... folder, or the folder that contains them " +
            "(for example on a USB stick or network drive).",
            d => Restore.Find(d).Count > 0,
            "No LabSolutions backup was found in this folder (folders named FullBackup_<date>). " +
            "If you have a .zip file instead, use 'Choose a one-file backup (.zip)'.");
        if (picked == null) return;
        txtRestoreRoot.Text = picked;
        RefreshRestoreList();
        if (lstRestore.Items.Count > 0)
        {
            lstRestore.Items[0].Selected = true;
            lstRestore.Focus();
        }
    }

    void LoadRestoreSettings()
    {
        txtRestoreRoot.Text = txtBackup.Text;
        if (!File.Exists(Paths.RestoreConfig))
        {
            Util.Log("WARNING: " + Paths.RestoreConfig + " was not found. Restoring will not work.");
            return;
        }
        try
        {
            XmlDocument xml = Config.Load(Paths.RestoreConfig);
            txtRestoreWork.Text = Config.Get(xml, "WorkFolder", "Restore");
            int mode;
            if (int.TryParse(Config.Get(xml, "RestoreMode", "Restore"), out mode) && mode >= 0 && mode <= 2)
                cmbRestoreMode.SelectedIndex = mode;
        }
        catch (Exception ex)
        {
            Util.Log("WARNING: could not read " + Paths.RestoreConfig + ": " + ex.Message);
        }
    }

    void RefreshRestoreList()
    {
        string selected = lstRestore.SelectedItems.Count > 0 ? (string)lstRestore.SelectedItems[0].Tag : null;
        lstRestore.BeginUpdate();
        lstRestore.Items.Clear();
        foreach (RestorePoint p in Restore.Find(txtRestoreRoot.Text.Trim()))
        {
            var item = new ListViewItem(p.Date == DateTime.MinValue ? "?" : p.Date.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(p.Type + (p.LooksComplete ? "" : "  - may be incomplete"));
            item.SubItems.Add(Path.GetFileName(p.Path));
            item.Tag = p.Path;
            if (!p.LooksComplete) item.ForeColor = Color.Gray;
            lstRestore.Items.Add(item);
            if (selected != null && string.Equals(selected, p.Path, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
        }
        lstRestore.EndUpdate();
        if (lstRestore.Items.Count == 0)
            lblRestoreStatus.Text = "No backups (FullBackup_<date> folders) found in this folder.";
        else if (restoreProcess == null)
            lblRestoreStatus.Text = lstRestore.Items.Count + " backup(s) found.";
    }

    void RunRestore()
    {
        if (restoreProcess != null && !restoreProcess.HasExited)
        {
            Info("A restore is already running.");
            return;
        }
        if (backupProcess != null && !backupProcess.HasExited)
        {
            Error("A backup is running. Wait until it has finished.");
            return;
        }
        if (lstRestore.SelectedItems.Count == 0)
        {
            Error("Choose the backup to restore in the list first.");
            return;
        }
        if (!File.Exists(Paths.RestoreExe) || !File.Exists(Paths.RestoreConfig))
        {
            Error("The LabSolutions restore program or its settings file was not found:\r\n" +
                  Paths.RestoreExe + "\r\n" + Paths.RestoreConfig);
            return;
        }

        ListViewItem chosen = lstRestore.SelectedItems[0];
        string path = (string)chosen.Tag;

        string work = txtRestoreWork.Text.Trim();
        if (work.Length > 3) work = work.TrimEnd('\\');
        if (work != "")
        {
            if (!Util.IsLocalDrivePath(work) || Util.MappedDriveToUnc(work) != null)
            {
                Error(@"The temp work folder must be on a local drive (for example D:\RestoreTemp).");
                return;
            }
            try { Directory.CreateDirectory(work); }
            catch (Exception ex)
            {
                Error("Could not create the folder " + work + "\r\n\r\n" + ex.Message);
                return;
            }
            txtRestoreWork.Text = work;
        }

        if (!Ask("Restore this backup?\r\n\r\n" +
                 "    " + chosen.Text + "   " + chosen.SubItems[1].Text + "\r\n    " + path + "\r\n\r\n" +
                 "The current LabSolutions database and system files will be REPLACED. " +
                 "Everything done since this backup is lost.\r\n\r\n" +
                 "LSSService will be stopped. Make sure nobody is using LabSolutions."))
            return;

        if (!Ask("Before you continue:\r\n\r\n" +
                 "If this backup came from another PC or database server, write down the current database " +
                 "connection address now. You may need to set it again after the restore (manual section 4.2).\r\n\r\n" +
                 "In the black window that opens:\r\n" +
                 "    1. Type the reason for the restore and press Enter\r\n" +
                 "    2. Type Y and press Enter (restore with this setting)\r\n" +
                 "    3. Type Y and press Enter (stop LabSolutions)\r\n\r\n" +
                 "Start the restore now?"))
            return;

        try
        {
            var values = new Dictionary<string, string> { { "RestorePath", path }, { "WorkFolder", work } };
            values["RestoreMode"] = cmbRestoreMode.SelectedIndex.ToString();
            List<string> missing = Config.Save(Paths.RestoreConfig, "Restore", values);
            if (missing.Contains("RestorePath"))
            {
                Error("The setting <RestorePath> is missing from " + Paths.RestoreConfig);
                return;
            }
            foreach (string m in missing)
                Util.Log("WARNING: <" + m + "> is not in the restore settings file, so it was left as it is.");
        }
        catch (Exception ex)
        {
            Error("Could not save the restore settings file.\r\n\r\n" + ex.Message);
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            serviceStopped = Restore.StopService();
            if (!serviceStopped && !Ask(Paths.ServiceName + " was not found on this PC. Continue with the restore anyway?"))
                return;
        }
        catch (Exception ex)
        {
            Error("Could not stop " + Paths.ServiceName + ". Stop it in Windows Services, then try again.\r\n\r\n" + ex.Message);
            return;
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        try
        {
            restoreStart = DateTime.Now;
            restorePath = path;
            restoreLog = null;
            restoreProcess = Process.Start(new ProcessStartInfo(Paths.RestoreExe)
            {
                WorkingDirectory = Paths.LabSolutionsDir, UseShellExecute = true
            });
            Util.Log("Restore started from " + path + ".");
            lblRestoreStatus.ForeColor = Color.DarkBlue;
            lblRestoreStatus.Text = "Restore running... answer the questions in the black window.";
            btnRestore.Enabled = false;
            restoreTimer.Start();
        }
        catch (Exception ex)
        {
            Error("Could not start the restore.\r\n\r\n" + ex.Message);
            OfferServiceStart();
        }
    }

    void RestoreTick()
    {
        if (!restoreProcess.HasExited) return;
        restoreTimer.Stop();
        btnRestore.Enabled = true;

        List<string> logs = Util.FilesUpToDepth(restorePath, 0)
            .Where(f => Path.GetFileName(f).StartsWith("LSSSystemRestore_", StringComparison.OrdinalIgnoreCase) &&
                        File.GetLastWriteTime(f) >= restoreStart.AddMinutes(-1))
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
        restoreLog = logs.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) ?? logs.FirstOrDefault();

        bool done = false;
        foreach (string f in logs.Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            try { if (File.ReadAllText(f).Contains("System Restore is done")) done = true; } catch { }
        }

        if (restoreLog == null)
        {
            lblRestoreStatus.ForeColor = Color.DarkRed;
            lblRestoreStatus.Text = "The restore did not run, or it was cancelled.";
            Error("No restore log was created, so the restore probably did not run or was cancelled (answered N).");
            OfferServiceStart();
            return;
        }

        btnRestartPc.Enabled = true;
        lblRestoreStatus.ForeColor = Color.DarkGreen;
        lblRestoreStatus.Text = done
            ? "Restore done. Restart the PC to finish."
            : "Restore finished. Check the log says \"System Restore is done.\", then restart the PC.";
        Util.Log("Restore finished. Log: " + restoreLog);
        string message = (done ? "The restore log says \"System Restore is done.\"" :
                                 "Open the restore log and check it says \"System Restore is done.\"") +
                         "\r\n\r\nThen restart the PC (button 'Restart PC now').\r\n\r\n" +
                         "If you saw \"The name of the database connection server is invalid\", open the LabSolutions " +
                         "database connection setting program after the restart and set the address you wrote down." +
                         "\r\n\r\nOpen the restore log now?";
        if (Ask(message)) OpenRestoreLog();
    }

    void OfferServiceStart()
    {
        if (!serviceStopped) return;
        if (!Ask("Start " + Paths.ServiceName + " again so LabSolutions can be used?")) return;
        try { Restore.StartService(); }
        catch (Exception ex) { Error("Could not start " + Paths.ServiceName + ". Restart the PC instead.\r\n\r\n" + ex.Message); }
    }

    void OpenRestoreLog()
    {
        string log = restoreLog;
        if (log == null || !File.Exists(log))
        {
            string dir = restorePath ?? (lstRestore.SelectedItems.Count > 0 ? (string)lstRestore.SelectedItems[0].Tag : null);
            if (dir != null)
                log = Util.FilesUpToDepth(dir, 0)
                    .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^LSSSystemRestore_.*\.pdf$", RegexOptions.IgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        if (log != null) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        else Info("No restore log was found for the selected backup.");
    }

    TextBox FolderRow(Control parent, int y, string label)
    {
        Add(parent, new Label(), 12, y + 3, 175, 20, label);
        TextBox box = Add(parent, new TextBox(), 190, y, 370, 23);
        Add(parent, new Button(), 566, y - 1, 80, 25, "Browse...").Click += (s, e) =>
        {
            using (var dlg = new FolderBrowserDialog { ShowNewFolderButton = true })
            {
                if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
            }
        };
        return box;
    }

    void Info(string message)
    {
        Util.Log(message);
        MessageBox.Show(this, message, Paths.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Error(string message)
    {
        Util.Log("ERROR: " + message);
        MessageBox.Show(this, message, Paths.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    bool Ask(string message)
    {
        return MessageBox.Show(this, message, Paths.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    void SetStatus(string text, Color color)
    {
        lblStatus.Text = text;
        lblStatus.ForeColor = color;
    }

    void LoadSettings()
    {
        if (!File.Exists(Paths.Config))
            Util.Log("WARNING: " + Paths.Config + " was not found. Backups from this PC will not work (restoring still can).");
        else try
        {
            XmlDocument xml = Config.Load();
            txtBackup.Text = Config.Get(xml, "BackupPath");
            txtAuth.Text = Config.Get(xml, "AuthBackupPath");
            txtWork.Text = Config.Get(xml, "WorkFolder");
            chkVerify.Checked = Config.Get(xml, "Verify") != "false";
            Util.Log("Loaded settings from " + Paths.Config);
        }
        catch (Exception ex)
        {
            Error("Could not read the settings file.\r\n\r\n" + ex.Message);
        }
        if (!File.Exists(Paths.BackupExe))
            Util.Log("WARNING: " + Paths.BackupExe + " was not found. Running a backup will not work.");
        lblSchedule.Text = Schedule.Status();
        LoadRestoreSettings();
    }

    // Checks the folders, creates them, then writes the XML if anything changed.
    bool SaveSettings()
    {
        var boxes = new[]
        {
            new KeyValuePair<string, TextBox>("BackupPath", txtBackup),
            new KeyValuePair<string, TextBox>("AuthBackupPath", txtAuth),
            new KeyValuePair<string, TextBox>("WorkFolder", txtWork)
        };

        foreach (var item in boxes)
        {
            string name = item.Key, label = Labels[item.Key];
            string p = item.Value.Text.Trim();
            if (p.Length > 3) p = p.TrimEnd('\\');

            if (p == "")
            {
                if (name == "WorkFolder") { item.Value.Text = ""; continue; }
                Error(label + " cannot be empty.");
                return false;
            }

            string unc = Util.MappedDriveToUnc(p);
            if (unc != null)
            {
                if (name == "WorkFolder")
                {
                    Error(@"The temp work folder must be on a local drive (for example D:\BackupTemp), not a network drive.");
                    return false;
                }
                if (Ask(label + ": drive " + p.Substring(0, 2) + " is a mapped network drive.\r\n\r\n" +
                        "Mapped drives are usually not available to the backup when it runs as administrator " +
                        "or from Task Scheduler.\r\n\r\nUse this path instead?\r\n" + unc))
                    p = unc;
            }

            if (name == "WorkFolder" && !Util.IsLocalDrivePath(p))
            {
                Error(@"The temp work folder must be on a local drive (for example D:\BackupTemp), not a network path.");
                return false;
            }

            if (Util.IsLocalDrivePath(p) && !Directory.Exists(p.Substring(0, 2) + "\\"))
            {
                Error(label + ": drive " + p.Substring(0, 2) +
                      " does not exist on this PC (or is a mapped drive that this program cannot see).");
                return false;
            }

            if (!Directory.Exists(p))
            {
                try
                {
                    Directory.CreateDirectory(p);
                    Util.Log("Created folder " + p);
                }
                catch (Exception ex)
                {
                    Error("Could not create the folder " + p + "\r\n\r\n" + ex.Message);
                    return false;
                }
            }
            item.Value.Text = p;
        }

        var warnings = new List<string>();
        string backup = txtBackup.Text;
        string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        if (Util.IsLocalDrivePath(backup) && backup.Substring(0, 2).Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
            warnings.Add("The backup is on the system drive (" + systemDrive + "). If that drive fails, the backup is lost too. " +
                         "Copy backups somewhere else now and then.");
        double? free = Util.FreeSpaceGB(backup);
        if (free.HasValue && free.Value < Paths.LowSpaceGB)
            warnings.Add("Only " + free.Value + " GB free on the backup drive.");
        if (!Util.IsLocalDrivePath(backup) && txtWork.Text == "")
        {
            double? freeC = Util.FreeSpaceGB(systemDrive);
            if (freeC.HasValue && freeC.Value < Paths.LowSpaceGB)
                warnings.Add("The backup goes to the network, so the C drive is used for temp files, and it has only " +
                             freeC.Value + " GB free. Set a temp work folder on another local drive.");
        }

        try
        {
            XmlDocument xml = Config.Load();
            var values = new Dictionary<string, string>
            {
                { "BackupPath", txtBackup.Text },
                { "AuthBackupPath", txtAuth.Text },
                { "WorkFolder", txtWork.Text },
                { "Verify", chkVerify.Checked ? "true" : "false" }
            };
            bool changed = false;
            foreach (var v in values)
            {
                XmlElement node = Config.Node(xml, v.Key);
                if (node == null) throw new Exception("The setting <" + v.Key + "> is missing from the settings file.");
                if (node.InnerText != v.Value)
                {
                    node.InnerText = v.Value;
                    node.IsEmpty = false;   // keep <WorkFolder></WorkFolder> instead of <WorkFolder />
                    changed = true;
                }
            }
            if (changed)
            {
                string copy = Paths.Config + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak";
                File.Copy(Paths.Config, copy);
                xml.Save(Paths.Config);
                Util.Log("Settings saved. Old settings copied to " + copy);
            }
        }
        catch (Exception ex)
        {
            Error("Could not save the settings file.\r\n\r\n" + ex.Message);
            return false;
        }

        foreach (string w in warnings) Util.Log("WARNING: " + w);
        return true;
    }

    void RunBackup()
    {
        if (backupProcess != null && !backupProcess.HasExited)
        {
            Info("A backup is already running.");
            return;
        }
        if (!SaveSettings()) return;
        if (rbDiff.Checked && Backup.FullBackups(txtBackup.Text).Count == 0)
        {
            Error("A differential backup needs a full backup first. No full backup was found in the backup folder.");
            return;
        }
        try
        {
            backupStart = DateTime.Now;
            backupProcess = Backup.Start(rbDiff.Checked);
            Util.Log((rbDiff.Checked ? "Differential" : "Full") + " backup started. The black window shows the progress.");
            SetStatus("Backup running... (the black window shows the progress)", Color.DarkBlue);
            btnRun.Enabled = false;
            timer.Start();
        }
        catch (Exception ex)
        {
            Error("Could not start the backup.\r\n\r\n" + ex.Message);
        }
    }

    void BackupTick()
    {
        if (!backupProcess.HasExited) return;
        timer.Stop();
        btnRun.Enabled = true;
        int? exitCode = null;
        try { exitCode = backupProcess.ExitCode; } catch { }
        BackupResult r = Backup.Check(txtBackup.Text, backupStart, exitCode);
        lastReport = r.Report;
        foreach (string line in r.Problems) Util.Log("  " + line);
        if (r.Ok)
        {
            SetStatus("Backup finished. Open the report to confirm \"Total: OK\".", Color.DarkGreen);
            Info(r.Summary);
            if (chkPack.Checked)
            {
                string folder = Packer.FullBackupOf(Path.GetDirectoryName(r.Report));
                PackInBackground(folder, folder + ".zip", lblStatus);
            }
        }
        else
        {
            SetStatus("Backup problem - see Activity below.", Color.DarkRed);
            Error(r.Summary + (r.LogFile != null ? "\r\n\r\nLog file:\r\n" + r.LogFile : ""));
        }
    }

    void OpenBackupFolder()
    {
        if (Directory.Exists(txtBackup.Text)) Process.Start("explorer.exe", Util.Quote(txtBackup.Text));
        else Error("The backup folder does not exist yet:\r\n" + txtBackup.Text);
    }

    void OpenReport()
    {
        string report = (lastReport != null && File.Exists(lastReport)) ? lastReport : Backup.LatestReport(txtBackup.Text);
        if (report != null) Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
        else Info("No backup report was found in the backup folder yet.");
    }

    void CleanUp()
    {
        int keep = (int)numKeep.Value;
        List<DirectoryInfo> old = Backup.OldBackups(txtBackup.Text, keep);
        if (old.Count == 0)
        {
            Info("Nothing to delete. There are " + keep + " or fewer full backups (folders named FullBackup_<date>).");
            return;
        }
        string names = string.Join("\r\n", old.Take(15).Select(d => "    " + d.Name));
        if (old.Count > 15) names += "\r\n    ... and " + (old.Count - 15) + " more";
        if (Ask("Delete these " + old.Count + " old backup folder(s)?\r\n\r\n" + names + "\r\n\r\n" +
                "The newest " + keep + " will be kept. Differential backups inside a deleted folder are deleted too. " +
                "This cannot be undone."))
        {
            Backup.Delete(old);
            SetStatus("Old backups deleted.", Color.Black);
        }
    }

    void CreateSchedule()
    {
        if (!SaveSettings()) return;
        string user, password;
        using (var dlg = new CredentialForm(Environment.UserDomainName + "\\" + Environment.UserName))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            user = dlg.User.Text.Trim();
            password = dlg.Password.Text;
        }

        bool diff = rbDiff.Checked;
        string command;
        if (!diff && !chkSchedClean.Checked)
        {
            // Same as manual section 3.3: start the LabSolutions program directly.
            command = Util.Quote(Paths.BackupExe);
        }
        else
        {
            // This tool runs the backup, checks it and cleans up. It is copied to the PC
            // first, so the schedule works without the USB stick.
            try
            {
                Directory.CreateDirectory(Paths.ToolDir);
                if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Paths.ToolExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(Application.ExecutablePath, Paths.ToolExe, true);
                    Util.Log("Copied this tool to " + Paths.ToolExe + " for the schedule.");
                }
            }
            catch (Exception ex)
            {
                Error("Could not copy the tool to " + Paths.ToolExe + "\r\n\r\n" + ex.Message);
                return;
            }
            command = Util.Quote(Paths.ToolExe) + " --unattended" +
                      (chkSchedClean.Checked ? " --keep " + (int)numKeep.Value : "") +
                      (diff ? " --diff" : "");
        }

        bool weekly = (string)cmbFreq.SelectedItem == "Weekly";
        var day = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), (string)cmbDay.SelectedItem);
        try
        {
            Schedule.Create(command, weekly, day, dtTime.Value, user, password);
            Util.Log("Schedule saved: " + (weekly ? "every " + day : "daily") + " at " + dtTime.Value.ToString("HH:mm") +
                     " as " + user + (diff ? ", differential" : ", full") + (chkSchedClean.Checked ? ", with cleanup" : "") + ".");
            lblSchedule.Text = Schedule.Status();
            Info("Automatic backup scheduled.\r\n\r\nIf the password of " + user +
                 " changes, open this tool and click 'Create / update schedule' again.");
        }
        catch (Exception ex)
        {
            Error("Could not create the scheduled task. Check the user name and password.\r\n\r\n" + ex.Message);
        }
    }

    void RemoveSchedule()
    {
        if (!Schedule.Exists())
        {
            Info("There is no scheduled backup to remove.");
            return;
        }
        if (!Ask("Remove the automatic backup schedule?")) return;
        try
        {
            Schedule.Delete();
            Util.Log("Schedule removed.");
        }
        catch (Exception ex)
        {
            Error("Could not remove the schedule.\r\n\r\n" + ex.Message);
        }
        lblSchedule.Text = Schedule.Status();
    }
}
