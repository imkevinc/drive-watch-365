// Drive Watch 365 - a tray app that remembers your drives.
//
// It lists the drives Windows can see, keeps a 30-day history, tells you when fewer show up than
// before (especially after a reboot), keeps a permanent log of problems, and tracks drive health
// (SMART) over time. MIT licensed, see LICENSE.
//
// It is one file on purpose, and is written for the C# 5 compiler that ships with Windows, so it
// builds with no installs: run build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Drive Watch 365")]
[assembly: AssemblyProduct("Drive Watch 365")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace DriveWatch
{
    // ------------------------------------------------------------------ data

    public class Drive
    {
        public string Key, Model, Serial, Letters, Bus, Media, Health, FirstSeen, LastSeen;
        public string Nickname, Note;   // set by the person; the scan never touches these
        public Smart Smart;             // the last good health reading (needs admin rights to read)
        public string SmartWhy;         // why there is no fresh reading right now
        public List<Reading> Trend = new List<Reading>();   // one reading per day, last 180 days
        [ScriptIgnore] public string Pnp, Index;             // plumbing from the scan, not saved
        public long Size;
    }

    // One SMART attribute, as the drive reports it (the same table CrystalDiskInfo shows).
    public class Attr { public int Id, Value, Worst, Thresh; public long Raw; public string Name; }

    public class Smart
    {
        public bool Ok, PredictFail;
        public string ReadAt, Source;                 // Source: "smart" (full table) or "windows" (basic counters)
        public int Temp = -1, Life = -1;              // Life = percent of SSD life left
        public long PowerOnHours = -1, PowerCycles = -1, Reallocated = -1, Pending = -1, Uncorrectable = -1, Crc = -1;
        public List<Attr> Attrs = new List<Attr>();
    }

    public class Reading
    {
        public string Date;
        public int Temp, Life;
        public long Poh, Realloc, Pending, Uncorr, Crc;
    }

    public class Day
    {
        public string Date, CheckedAt, Kind;
        public int Total;
        public List<string> Present = new List<string>();
        public List<string> Dipped = new List<string>();   // missing at some check that day
    }

    public class Ev { public string At, Text, Level; }

    // One thing that went wrong with one drive. Kept forever (it is tiny), unlike the 30-day squares.
    //   Type "missing": the drive vanished. Open (End == null) until it comes back or you remove it.
    //   Type "late":    the drive was not there on the first look after startup but showed up on a re-look.
    public class Incident
    {
        public string Key, Type, Name, Model, Letters, Serial;
        public long Size;
        public string Start, End, EndKind;        // EndKind: "back" or "removed"
        public string StartKind, LastSeen, Note;
    }

    public class Store
    {
        public string Started;
        public Dictionary<string, Drive> Drives = new Dictionary<string, Drive>();
        public Dictionary<string, Day> Days = new Dictionary<string, Day>();
        public List<Ev> Events = new List<Ev>();
        public List<string> LastMissing = new List<string>();   // old field, no longer used
        public List<Incident> Incidents = new List<Incident>();
        public bool TrayHintShown;
    }

    static class Persist
    {
        public static string Dir
        {
            get
            {
                string o = Environment.GetEnvironmentVariable("DRIVEWATCH_DATA");
                if (!string.IsNullOrEmpty(o)) return o;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DriveWatch365");
            }
        }
        static string FilePath { get { return Path.Combine(Dir, "history.json"); } }
        static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

        public static Store Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Store s = Ser().Deserialize<Store>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        if (s.Drives == null) s.Drives = new Dictionary<string, Drive>();
                        if (s.Days == null) s.Days = new Dictionary<string, Day>();
                        if (s.Events == null) s.Events = new List<Ev>();
                        if (s.LastMissing == null) s.LastMissing = new List<string>();
                        if (s.Incidents == null) s.Incidents = new List<Incident>();
                        foreach (Day d in s.Days.Values)
                        {
                            if (d.Present == null) d.Present = new List<string>();
                            if (d.Dipped == null) d.Dipped = new List<string>();
                        }
                        return s;
                    }
                }
            }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            }
            return new Store();
        }

        public static void Save(Store s)
        {
            if (Program.Demo) return;   // demo mode (made-up drives for screenshots) never writes anything
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Ser().Serialize(s));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log("Could not save history: " + ex); }
        }

        // A plain-text log of anything that went wrong, so problems are never invisible. Kept small.
        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string p = Path.Combine(Dir, "error.log");
                if (File.Exists(p) && new FileInfo(p).Length > 200000) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (Program.IsAdmin ? " [admin] " : " ") + text + "\r\n");
            }
            catch { }
        }
    }

    static class Log
    {
        public static Incident Open(Store s, string key)
        {
            return s.Incidents.FirstOrDefault(i => i.Type == "missing" && i.End == null && i.Key == key);
        }
        // How many incidents this drive has had in the 90 days up to (and including) asOf.
        public static int Recent(Store s, string key, DateTime asOf)
        {
            DateTime from = asOf.AddDays(-90);
            return s.Incidents.Count(i => i.Key == key && Fmt.Parse(i.Start) > from && Fmt.Parse(i.Start) <= asOf);
        }
        public static Incident Make(string type, Drive d, string kind, DateTime now)
        {
            return new Incident { Key = d.Key, Type = type, Name = Fmt.Title(d), Model = d.Model, Letters = d.Letters, Serial = d.Serial, Size = d.Size,
                Start = Fmt.Iso(now), StartKind = kind, LastSeen = d.LastSeen };
        }
    }

    // The Windows scheduled task that starts Drive Watch 365 at login WITH admin rights and no prompt.
    static class Startup
    {
        public const string TaskName = "DriveWatch365";

        public static int Run(string file, string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        public static void RunHidden(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }
        // Starts this same program again with the Windows permission prompt ("runas"). Throws Win32Exception 1223 if declined.
        public static int RunElevated(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (Process p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        public static bool Exists()
        {
            try { string o; return Run("schtasks.exe", "/query /tn \"" + TaskName + "\"", out o) == 0; }
            catch { return false; }
        }

        public static bool Create(out string msg)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                + "  <RegistrationInfo><Description>Starts Drive Watch 365 at login with the rights it needs to read drive health.</Description></RegistrationInfo>\r\n"
                + "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>\r\n"
                + "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n"
                + "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>"
                + "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>\r\n"
                + "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) + "</Command><Arguments>--startup</Arguments></Exec></Actions>\r\n"
                + "</Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "drivewatch365-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                return Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out msg) == 0;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static bool Delete(out string msg)
        {
            return Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f", out msg) == 0;
        }

        public static void RemoveRunKey()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue("DriveWatch365", false); }
            catch { }
        }

        // What the elevated helper does: "--setup-admin on" or "off". 0 = done.
        public static int Setup(bool on)
        {
            string msg;
            if (on) { if (!Create(out msg)) return 1; RemoveRunKey(); return 0; }
            RemoveRunKey();
            return (!Exists() || Delete(out msg)) ? 0 : 1;
        }
    }

    // ------------------------------------------------------------------ drive health (SMART)

    static class Health
    {
        static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            {1,"Read error rate"},{2,"Throughput performance"},{3,"Spin-up time"},{4,"Start/stop count"},{5,"Reallocated sectors"},
            {7,"Seek error rate"},{8,"Seek time performance"},{9,"Power-on hours"},{10,"Spin retry count"},{11,"Calibration retry count"},
            {12,"Power cycle count"},{13,"Soft read error rate"},{170,"Reserved block count"},{171,"Program fail count"},{172,"Erase fail count"},
            {173,"Wear leveling count"},{174,"Unexpected power loss"},{177,"Wear leveling count"},{178,"Used reserved blocks"},{179,"Used reserved blocks"},
            {180,"Unused reserved blocks"},{181,"Program fail count"},{182,"Erase fail count"},{183,"Runtime bad blocks"},{184,"End-to-end error"},
            {187,"Reported uncorrectable errors"},{188,"Command timeout"},{189,"High fly writes"},{190,"Airflow temperature"},{191,"G-sense errors"},
            {192,"Power-off retract count"},{193,"Load/unload cycles"},{194,"Temperature"},{195,"Hardware ECC recovered"},{196,"Reallocation events"},
            {197,"Pending sectors"},{198,"Uncorrectable sectors"},{199,"Cable (CRC) errors"},{200,"Write error rate"},{201,"Soft read errors"},
            {202,"Percent lifetime used"},{203,"ECC errors"},{204,"Soft ECC corrections"},{205,"Thermal asperity rate"},{206,"Flying height"},
            {220,"Disk shift"},{222,"Loaded hours"},{223,"Load retry count"},{224,"Load friction"},{226,"Load-in time"},{230,"Drive life protection"},
            {231,"SSD life left"},{232,"Available reserved space"},{233,"Media wearout indicator"},{234,"Total NAND writes"},{240,"Head flying hours"},
            {241,"Total data written"},{242,"Total data read"},{245,"Total NAND writes"},{250,"Read error retry rate"},{254,"Free-fall events"}
        };
        public static string AttrName(int id) { string n; return Names.TryGetValue(id, out n) ? n : "Attribute " + id; }

        // The part of a device path that is unique to one drive (matches the SMART records to Win32_DiskDrive).
        static string Tail(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.LastIndexOf('\\');
            string t = (i >= 0 ? s.Substring(i + 1) : s).ToUpperInvariant();
            int u = t.LastIndexOf('_');
            if (u > 0 && t.Substring(u + 1).All(char.IsDigit)) t = t.Substring(0, u);
            return t;
        }
        static int Int(object o) { if (o == null) return -1; try { return Convert.ToInt32(o); } catch { return -1; } }
        static long Lng(object o) { if (o == null) return -1; try { return Convert.ToInt64(o); } catch { return -1; } }

        public static int TempLimit(Drive d)
        {
            return d.Media == "SSD" ? 70 : 55;   // degrees C at which a warning is logged
        }

        static Attr Find(Smart s, int id) { return s.Attrs.FirstOrDefault(a => a.Id == id); }

        static Smart Parse(byte[] b, byte[] t)
        {
            if (b == null || b.Length < 14) return null;
            Smart s = new Smart { Source = "smart" };
            for (int i = 0; i < 30; i++)
            {
                int o = 2 + i * 12;
                if (o + 11 > b.Length) break;
                int id = b[o]; if (id == 0) continue;
                long raw = 0; for (int k = 5; k >= 0; k--) raw = (raw << 8) | b[o + 5 + k];
                Attr a = new Attr { Id = id, Value = b[o + 3], Worst = b[o + 4], Raw = raw, Name = AttrName(id) };
                if (t != null && o + 1 < t.Length && t[o] == id) a.Thresh = t[o + 1];
                s.Attrs.Add(a);
            }
            if (s.Attrs.Count == 0) return null;
            Attr x = Find(s, 194) ?? Find(s, 190);
            if (x != null) { int tv = (int)(x.Raw & 0xFF); if (tv > 0 && tv < 120) s.Temp = tv; }
            if ((x = Find(s, 9)) != null) s.PowerOnHours = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 12)) != null) s.PowerCycles = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 5)) != null) s.Reallocated = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 197)) != null) s.Pending = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 198) ?? Find(s, 187)) != null) s.Uncorrectable = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 199)) != null) s.Crc = x.Raw & 0xFFFFFFFFL;
            if ((x = Find(s, 231) ?? Find(s, 233) ?? Find(s, 177)) != null && x.Value > 0 && x.Value <= 100) s.Life = x.Value;
            return s;
        }

        // ---- rate of change: a small count that holds steady is fine; a CLIMBING count is the problem
        public static readonly Func<Reading, long> RA = r => r.Realloc, PE = r => r.Pending, UN = r => r.Uncorr, CR = r => r.Crc;

        public static Func<Reading, long> SelFor(int attrId)
        {
            switch (attrId) { case 5: return RA; case 197: return PE; case 198: case 187: return UN; case 199: return CR; default: return null; }
        }
        static List<Reading> Window(Drive d, Func<Reading, long> sel, int days)
        {
            DateTime from = DateTime.Today.AddDays(-days);
            return d.Trend.Where(t => sel(t) >= 0 && Fmt.Parse(t.Date) >= from).ToList();
        }
        // how much the count went up over the last N days (0 if it did not, or there is no history yet)
        public static long Delta(Drive d, Func<Reading, long> sel, int days)
        {
            List<Reading> w = Window(d, sel, days);
            return w.Count < 2 ? 0 : Math.Max(0, sel(w[w.Count - 1]) - sel(w[0]));
        }
        // on how many separate days the count went up
        public static int Rises(Drive d, Func<Reading, long> sel, int days)
        {
            List<Reading> w = Window(d, sel, days); int n = 0;
            for (int i = 1; i < w.Count; i++) if (sel(w[i]) > sel(w[i - 1])) n++;
            return n;
        }
        // days since the count last went up (or since we first looked, if it never has)
        public static int SteadyDays(Drive d, Func<Reading, long> sel)
        {
            List<Reading> w = d.Trend.Where(t => sel(t) >= 0).ToList();
            if (w.Count == 0) return 0;
            int last = 0;
            for (int i = 1; i < w.Count; i++) if (sel(w[i]) > sel(w[i - 1])) last = i;
            return (int)(DateTime.Today - Fmt.Parse(w[last].Date)).TotalDays;
        }
        // fast climbing: 8+ in a week, or rising on 3+ separate days in two weeks
        public static bool Climbing(Drive d, Func<Reading, long> sel) { return Delta(d, sel, 7) >= 8 || Rises(d, sel, 14) >= 3; }
        public static bool RoseRecently(Drive d, Func<Reading, long> sel) { return Rises(d, sel, 30) > 0; }

        // 0 = no data, 1 = looks fine, 2 = caution, 3 = alert
        public static int Level(Drive d)
        {
            Smart s = d.Smart;
            if (s == null || !s.Ok) return 0;
            if (s.PredictFail || s.Attrs.Any(a => a.Thresh > 0 && a.Value > 0 && a.Value <= a.Thresh)) return 3;
            if (Climbing(d, RA) || Climbing(d, PE) || Climbing(d, UN) || (s.Life >= 0 && s.Life <= 10)) return 3;
            if (RoseRecently(d, RA) || RoseRecently(d, PE) || RoseRecently(d, UN) || RoseRecently(d, CR)
                || s.Pending > 0 || (s.Life >= 0 && s.Life <= 20) || s.Temp >= TempLimit(d)) return 2;
            return 1;
        }

        public static void Fill(List<Drive> drives)
        {
            if (!Program.IsAdmin)
            {
                foreach (Drive d in drives) d.SmartWhy = "Needs administrator rights.";
                return;
            }
            Dictionary<string, byte[]> data = new Dictionary<string, byte[]>(), thr = new Dictionary<string, byte[]>();
            Dictionary<string, bool> pred = new Dictionary<string, bool>();
            Dictionary<string, Smart> rel = new Dictionary<string, Smart>();
            string err = null;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"))
                    foreach (ManagementObject o in s.Get()) data[Tail(Convert.ToString(o["InstanceName"]))] = o["VendorSpecific"] as byte[];
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"))
                    foreach (ManagementObject o in s.Get()) thr[Tail(Convert.ToString(o["InstanceName"]))] = o["VendorSpecific"] as byte[];
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
                    foreach (ManagementObject o in s.Get()) pred[Tail(Convert.ToString(o["InstanceName"]))] = o["PredictFailure"] is bool && (bool)o["PredictFailure"];
            }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        string id = Convert.ToString(o["DeviceId"]);
                        foreach (ManagementObject rc in o.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            Smart r = new Smart { Source = "windows" };
                            int tp = Int(rc["Temperature"]); r.Temp = tp > 0 && tp < 120 ? tp : -1;
                            long poh = Lng(rc["PowerOnHours"]); r.PowerOnHours = poh > 0 ? poh : -1;
                            int wear = Int(rc["Wear"]); r.Life = wear >= 0 && wear <= 100 ? 100 - wear : -1;
                            long ue = Lng(rc["ReadErrorsUncorrected"]); r.Uncorrectable = ue >= 0 ? ue : -1;
                            if (r.Temp >= 0 || r.PowerOnHours >= 0 || r.Life >= 0) rel[id] = r;
                        }
                    }
                }
            }
            catch { }

            string now = Fmt.Iso(DateTime.Now);
            foreach (Drive d in drives)
            {
                string tail = Tail(d.Pnp);
                byte[] blob, th = null; bool pf = false;
                Smart s = null;
                if (data.TryGetValue(tail, out blob)) { thr.TryGetValue(tail, out th); s = Parse(blob, th); }
                Smart r; rel.TryGetValue(d.Index ?? "", out r);
                if (s == null && r != null) s = r;
                else if (s != null && r != null)
                {
                    if (s.Temp < 0) s.Temp = r.Temp;
                    if (s.PowerOnHours < 0) s.PowerOnHours = r.PowerOnHours;
                    if (s.Life < 0) s.Life = r.Life;
                }
                if (s == null)
                {
                    d.SmartWhy = err != null ? "Windows would not give the health data (" + err.Trim() + ")."
                        : d.Bus == "USB" ? "This USB enclosure does not pass drive health data through to Windows."
                        : "This drive or its controller does not report health data to Windows.";
                    continue;
                }
                if (pred.TryGetValue(tail, out pf)) s.PredictFail = pf;
                s.Ok = true; s.ReadAt = now;
                d.Smart = s;
            }
        }
    }

    // ------------------------------------------------------------------ reading the drives

    static class Scanner
    {
        // Disk number -> its drive letters, from the partition-to-letter links ("Disk #3, Partition #1" -> "E:").
        static Dictionary<string, string> LetterMap()
        {
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        System.Text.RegularExpressions.Match a = System.Text.RegularExpressions.Regex.Match(Convert.ToString(o["Antecedent"]), "Disk #(\\d+)");
                        System.Text.RegularExpressions.Match d = System.Text.RegularExpressions.Regex.Match(Convert.ToString(o["Dependent"]), "DeviceID=\"([^\"]+)\"");
                        if (!a.Success || !d.Success) continue;
                        List<string> l;
                        if (!map.TryGetValue(a.Groups[1].Value, out l)) { l = new List<string>(); map[a.Groups[1].Value] = l; }
                        if (!l.Contains(d.Groups[1].Value)) l.Add(d.Groups[1].Value);
                    }
                }
            }
            catch { }
            Dictionary<string, string> res = new Dictionary<string, string>();
            foreach (KeyValuePair<string, List<string>> kv in map) { kv.Value.Sort(); res[kv.Key] = string.Join(" ", kv.Value.ToArray()); }
            return res;
        }

        static int Num(object o) { return o == null ? 0 : Convert.ToInt32(o); }

        public static List<Drive> Scan()
        {
            // MSFT_PhysicalDisk knows HDD vs SSD, the bus and the health; it is optional.
            Dictionary<string, int[]> extra = new Dictionary<string, int[]>();
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT DeviceId, MediaType, BusType, HealthStatus FROM MSFT_PhysicalDisk")))
                {
                    foreach (ManagementObject o in s.Get())
                        extra[Convert.ToString(o["DeviceId"])] = new int[] { Num(o["MediaType"]), Num(o["BusType"]), Num(o["HealthStatus"]) };
                }
            }
            catch { }

            Dictionary<string, string> letters = LetterMap();
            List<Drive> res = new List<Drive>();
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                "SELECT DeviceID, Index, Model, SerialNumber, Size, InterfaceType, PNPDeviceID FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject o in s.Get())
                {
                    long size = o["Size"] == null ? 0 : Convert.ToInt64(o["Size"]);
                    string model = ((o["Model"] as string) ?? "Unknown drive").Trim();
                    int[] ex; string idx = Convert.ToString(o["Index"]);
                    if (!extra.TryGetValue(idx, out ex)) ex = null;
                    // Empty card readers and mounted virtual disks are not hard drives.
                    if (size <= 0) continue;
                    if (model.IndexOf("Virtual Disk", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (ex != null && ex[1] == 15) continue;

                    string serial = ((o["SerialNumber"] as string) ?? "").Trim().Trim('.').Trim();
                    string pnp = (o["PNPDeviceID"] as string) ?? "";
                    Drive d = new Drive();
                    d.Model = model;
                    d.Pnp = pnp; d.Index = idx;
                    d.Serial = serial;
                    d.Size = size;
                    d.Key = model + "|" + (serial.Length > 0 ? serial : pnp) + "|" + size;
                    string bus = "";
                    if (ex != null)
                    {
                        switch (ex[1])
                        {
                            case 17: bus = "NVMe"; break;
                            case 11: bus = "SATA"; break;
                            case 7: bus = "USB"; break;
                            case 10: bus = "SAS"; break;
                            case 8: bus = "RAID"; break;
                            case 3: bus = "ATA"; break;
                            case 1: bus = "SCSI"; break;
                        }
                        d.Media = ex[0] == 3 ? "HDD" : (ex[0] == 4 ? "SSD" : (ex[0] == 5 ? "SCM" : ""));
                        d.Health = ex[2] == 0 ? "Healthy" : (ex[2] == 1 ? "Warning" : "Unhealthy");
                    }
                    if (bus.Length == 0) bus = Convert.ToString(o["InterfaceType"]);
                    d.Bus = bus;
                    string lt; d.Letters = letters.TryGetValue(idx, out lt) ? lt : "";
                    res.Add(d);
                }
            }
            Health.Fill(res);
            return res;
        }
    }

    // ------------------------------------------------------------------ small helpers

    static class Fmt
    {
        public static string Iso(DateTime d) { return d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }
        public static string DayKey(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static DateTime Parse(string s)
        {
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return DateTime.MinValue;
        }
        public static string Clock(DateTime d) { return d.ToString("h:mm tt", CultureInfo.InvariantCulture); }
        public static string When(DateTime dt)
        {
            if (dt == DateTime.MinValue) return "never";
            DateTime t = DateTime.Today;
            string day = dt.Date == t ? "Today" : (dt.Date == t.AddDays(-1) ? "Yesterday" : dt.ToString("MMM d", CultureInfo.InvariantCulture));
            return day + " " + Clock(dt);
        }
        // "Mar 3" (adds the year when it is not this year)
        public static string DateOnly(DateTime d)
        {
            if (d == DateTime.MinValue) return "unknown";
            return d.ToString(d.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
        }
        public static string WhenFull(DateTime d)
        {
            if (d == DateTime.MinValue) return "unknown";
            return DateOnly(d) + ", " + Clock(d);
        }
        public static string Span(DateTime a, DateTime b)
        {
            TimeSpan t = b - a; if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalDays >= 2) return Math.Floor(t.TotalDays).ToString(CultureInfo.InvariantCulture) + " days";
            if (t.TotalHours >= 1.5) return Math.Round(t.TotalHours).ToString(CultureInfo.InvariantCulture) + " hours";
            if (t.TotalMinutes >= 2) return Math.Round(t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes";
            if (t.TotalHours >= 1) return "about an hour";
            return t.TotalSeconds < 60 ? "under a minute" : "a minute";
        }
        public static string Hours(long h)
        {
            long yrs = h / 8766, mo = (h % 8766) / 730;
            if (yrs >= 1) return yrs + " yr " + mo + " mo";
            if (mo >= 1) return mo + " mo";
            return Math.Max(1, h / 24) + " days";
        }
        public static string Ordinal(int n)
        {
            if (n % 100 >= 11 && n % 100 <= 13) return n + "th";
            switch (n % 10) { case 1: return n + "st"; case 2: return n + "nd"; case 3: return n + "rd"; default: return n + "th"; }
        }
        public static string Size(long b)
        {
            double tb = b / 1e12;
            if (tb >= 1) return tb.ToString("0.##", CultureInfo.InvariantCulture) + " TB";
            return (b / 1e9).ToString("0", CultureInfo.InvariantCulture) + " GB";
        }
        public static string ShortSerial(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = new string(s.Where(char.IsLetterOrDigit).ToArray());
            if (t.Length == 0) return "";
            return "S/N ..." + (t.Length > 6 ? t.Substring(t.Length - 6) : t);
        }
        public static string KindLabel(string k)
        {
            switch (k)
            {
                case "Startup": return "Startup check";
                case "Daily": return "Daily check";
                case "Manual": return "Manual check";
                case "Snap": return "Daily check";   // screenshot runs only
                default: return "Check";
            }
        }
        public static string Name(Drive d)
        {
            return Title(d) + (string.IsNullOrEmpty(d.Letters) ? "" : " (" + d.Letters + ")") + ", " + Fmt.Size(d.Size);
        }
        // What to call a drive: the nickname if there is one, else the model Windows reports.
        public static string Title(Drive d)
        {
            return string.IsNullOrEmpty(d.Nickname) ? d.Model : d.Nickname;
        }
    }

    static class Theme
    {
        public static float Scale = 1f;
        public static readonly Color Bg = Color.FromArgb(18, 21, 26);
        public static readonly Color Card = Color.FromArgb(27, 32, 39);
        public static readonly Color Line = Color.FromArgb(40, 47, 57);
        public static readonly Color Text = Color.FromArgb(232, 236, 241);
        public static readonly Color Muted = Color.FromArgb(139, 149, 163);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color Ok = Color.FromArgb(52, 199, 123);
        public static readonly Color Warn = Color.FromArgb(245, 185, 66);
        public static readonly Color Bad = Color.FromArgb(255, 92, 92);
        public static readonly Color Empty = Color.FromArgb(46, 54, 65);
        public static readonly Color Before = Color.FromArgb(30, 35, 43);

        public static void Init()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
        }
        public static int Sc(int v) { return (int)Math.Round(v * Scale); }
        public static Font F(float px, FontStyle st) { return new Font("Segoe UI", px * Scale, st, GraphicsUnit.Pixel); }
        public static Color Mix(Color c, Color bg, double a)
        {
            return Color.FromArgb((int)(bg.R + (c.R - bg.R) * a), (int)(bg.G + (c.G - bg.G) * a), (int)(bg.B + (c.B - bg.B) * a));
        }
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static StringFormat Clip()
        {
            StringFormat f = new StringFormat(StringFormatFlags.NoWrap);
            f.Trimming = StringTrimming.EllipsisCharacter;
            return f;
        }
        public static void Txt(Graphics g, string s, Font f, Color c, float x, float y, float w)
        {
            using (Brush b = new SolidBrush(c))
            using (StringFormat sf = Clip())
                g.DrawString(s, f, b, new RectangleF(x, y, Math.Max(1, w), f.Height + 2), sf);
        }
    }

    static class Icons
    {
        // A little hard drive, with an optional status dot. Drawn in code so there are no image files.
        public static Bitmap Draw(int size, Color? dot)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float u = size / 32f;
                RectangleF body = new RectangleF(1.5f * u, 6f * u, 29f * u, 17f * u);
                using (GraphicsPath p = Theme.Round(body, 4f * u))
                using (LinearGradientBrush lb = new LinearGradientBrush(body, Color.FromArgb(214, 223, 235), Color.FromArgb(132, 148, 170), 90f))
                using (Pen pen = new Pen(Color.FromArgb(60, 72, 90), Math.Max(1f, 1.2f * u)))
                {
                    g.FillPath(lb, p);
                    g.DrawPath(pen, p);
                }
                using (Pen sl = new Pen(Color.FromArgb(90, 104, 124), Math.Max(1f, 1.4f * u)))
                {
                    g.DrawLine(sl, 6f * u, 15f * u, 16f * u, 15f * u);
                    g.DrawLine(sl, 6f * u, 18.5f * u, 12f * u, 18.5f * u);
                }
                using (Brush led = new SolidBrush(Color.FromArgb(52, 64, 82)))
                    g.FillEllipse(led, 21f * u, 13.5f * u, 4.2f * u, 4.2f * u);
                if (dot.HasValue)
                {
                    RectangleF ring = new RectangleF(16.5f * u, 15.5f * u, 15f * u, 15f * u);
                    using (Brush rb = new SolidBrush(Color.FromArgb(24, 28, 34))) g.FillEllipse(rb, ring);
                    ring.Inflate(-2f * u, -2f * u);
                    using (Brush db = new SolidBrush(dot.Value)) g.FillEllipse(db, ring);
                }
            }
            return bmp;
        }

        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            List<byte[]> pngs = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using (Bitmap b = Draw(sz, Theme.Ok))
                using (MemoryStream ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int off = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(off);
                    off += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // ------------------------------------------------------------------ controls

    class Surface : Panel
    {
        public Surface() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
    }

    enum Look { Working, Ok, Warn, Bad }

    class Banner : Control
    {
        Look look = Look.Working; string title = "Checking your drives...", detail = "";
        Font ft = Theme.F(21, FontStyle.Bold), fd = Theme.F(13, FontStyle.Regular);
        public Banner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public void Set(Look l, string t, string d) { look = l; title = t; detail = d; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float u = Theme.Scale;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            Color c = look == Look.Ok ? Theme.Ok : look == Look.Bad ? Theme.Bad : look == Look.Warn ? Theme.Warn : Theme.Accent;
            RectangleF r = new RectangleF(16 * u, 4 * u, Width - 32 * u, Height - 8 * u);
            using (GraphicsPath p = Theme.Round(r, 14 * u))
            using (Brush b = new SolidBrush(Theme.Mix(c, Theme.Card, 0.14)))
            using (Pen pen = new Pen(Theme.Mix(c, Theme.Card, 0.55), 1.2f * u))
            { g.FillPath(b, p); g.DrawPath(pen, p); }

            float d = 44 * u, cx = r.X + 22 * u + d / 2, cy = r.Y + r.Height / 2;
            using (Brush b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            using (Pen w = new Pen(Color.White, 3.4f * u))
            {
                w.StartCap = LineCap.Round; w.EndCap = LineCap.Round; w.LineJoin = LineJoin.Round;
                if (look == Look.Ok)
                    g.DrawLines(w, new PointF[] { new PointF(cx - 9 * u, cy + 1 * u), new PointF(cx - 3 * u, cy + 7 * u), new PointF(cx + 9 * u, cy - 7 * u) });
                else if (look == Look.Bad || look == Look.Warn)
                {
                    g.DrawLine(w, cx, cy - 10 * u, cx, cy + 2 * u);
                    using (Brush wb = new SolidBrush(Color.White)) g.FillEllipse(wb, cx - 2.2f * u, cy + 6 * u, 4.4f * u, 4.4f * u);
                }
                else
                    using (Brush wb = new SolidBrush(Color.White))
                        for (int i = -1; i <= 1; i++) g.FillEllipse(wb, cx + i * 10 * u - 2.8f * u, cy - 2.8f * u, 5.6f * u, 5.6f * u);
            }
            float tx = r.X + 86 * u;
            Theme.Txt(g, title, ft, Theme.Text, tx, r.Y + 11 * u, r.Right - tx - 16 * u);
            using (Brush b = new SolidBrush(Color.FromArgb(200, Theme.Text)))
                g.DrawString(detail, fd, b, new RectangleF(tx, r.Y + 44 * u, r.Right - tx - 16 * u, r.Height - 46 * u));
        }
    }

    class DriveGrid : Control
    {
        public class Row { public Drive D; public bool Live; }
        public event Action<Drive> ForgetRequested;

        const int DaysShown = 30;
        List<Row> rows = new List<Row>();
        Store store; bool scanned;
        int hoverRow = -1, hoverDay = -1; bool hoverBtn;
        string lastTip = "";
        ToolTip tip = new ToolTip();
        ContextMenuStrip ctx = new ContextMenuStrip();
        public event Action<Drive> RenameRequested;
        public event Action<Drive> HealthRequested;
        ToolStripMenuItem miHealth = new ToolStripMenuItem("Health details...");
        ToolStripMenuItem miForget = new ToolStripMenuItem("I removed this drive");
        ToolStripMenuItem miRename = new ToolStripMenuItem("Name this drive / add a note...");
        Drive ctxDrive;
        Font fName = Theme.F(15, FontStyle.Bold), fSub = Theme.F(12, FontStyle.Regular), fSm = Theme.F(11, FontStyle.Regular),
             fPill = Theme.F(11, FontStyle.Bold), fHead = Theme.F(11, FontStyle.Bold);

        float U { get { return Theme.Scale; } }
        float HeaderH { get { return 30 * U; } }
        float RowH { get { return 56 * U; } }
        float Gap { get { return 6 * U; } }
        float Pad { get { return 16 * U; } }
        float Cw { get { return 13 * U; } }
        float Cg { get { return 3 * U; } }
        float CellsW { get { return DaysShown * Cw + (DaysShown - 1) * Cg; } }
        float CellsX { get { return Width - Pad - CellsW - 14 * U; } }

        public DriveGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            tip.InitialDelay = 150; tip.ReshowDelay = 50; tip.UseAnimation = false; tip.UseFading = false;
            miForget.Click += delegate { if (ctxDrive != null && ForgetRequested != null) ForgetRequested(ctxDrive); };
            miRename.Click += delegate { if (ctxDrive != null && RenameRequested != null) RenameRequested(ctxDrive); };
            miHealth.Click += delegate { if (ctxDrive != null && HealthRequested != null) HealthRequested(ctxDrive); };
            ctx.Items.Add(miHealth);
            ctx.Items.Add(miRename);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(miForget);
        }

        public void Bind(Store s, Dictionary<string, Drive> live, bool scannedOnce)
        {
            store = s; scanned = scannedOnce;
            rows = s.Drives.Values.Select(d => new Row { D = d, Live = live.ContainsKey(d.Key) })
                .OrderBy(r => r.Live ? 1 : 0)
                .ThenBy(r => string.IsNullOrEmpty(r.D.Letters) ? "~" : r.D.Letters)
                .ThenBy(r => Fmt.Title(r.D)).ThenBy(r => r.D.Size).ToList();
            Height = rows.Count == 0 ? (int)(120 * U) : (int)(HeaderH + rows.Count * (RowH + Gap) + 10 * U);
            Invalidate();
        }

        float RowTop(int i) { return HeaderH + i * (RowH + Gap); }
        RectangleF CellRect(int row, int day)
        {
            float ch = 28 * U;
            return new RectangleF(CellsX + day * (Cw + Cg), RowTop(row) + (RowH - ch) / 2, Cw, ch);
        }
        RectangleF ButtonRect(int row)
        {
            float w = 104 * U, h = 26 * U, pillW = 74 * U;
            return new RectangleF(CellsX - 14 * U - pillW - 8 * U - w, RowTop(row) + (RowH - h) / 2, w, h);
        }

        // 0 present, 1 dipped, 2 missing, 3 no check, 4 before tracking
        int CellState(Drive d, int i, out Day dayRec, out DateTime date)
        {
            date = DateTime.Today.AddDays(-(DaysShown - 1 - i));
            string key = Fmt.DayKey(date);
            DateTime first = Fmt.Parse(d.FirstSeen).Date;
            dayRec = null;
            Day day;
            if (store.Days.TryGetValue(key, out day))
            {
                dayRec = day;
                if (day.Present.Contains(d.Key)) return day.Dipped.Contains(d.Key) ? 1 : 0;
                return date >= first ? 2 : 4;
            }
            return date >= first ? 3 : 4;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float u = U;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            if (store == null) return;
            Theme.Txt(g, "DRIVES  (" + rows.Count + ")", fHead, Theme.Muted, Pad + 4 * u, 8 * u, 300 * u);
            // date labels over the day cells
            int[] marks = { 0, 7, 14, 21 };
            foreach (int m in marks)
            {
                DateTime dt = DateTime.Today.AddDays(-(DaysShown - 1 - m));
                Theme.Txt(g, dt.ToString("MMM d", CultureInfo.InvariantCulture), fSm, Theme.Muted, CellsX + m * (Cw + Cg), 9 * u, 60 * u);
            }
            using (Brush b = new SolidBrush(Theme.Muted))
            {
                StringFormat right = new StringFormat(); right.Alignment = StringAlignment.Far;
                g.DrawString("Today", fSm, b, new RectangleF(CellsX + CellsW - 60 * u, 9 * u, 60 * u, 20 * u), right);
            }
            if (rows.Count == 0)
            {
                Theme.Txt(g, scanned ? "No drives found yet." : "Looking for drives...", fSub, Theme.Muted, Pad + 4 * u, HeaderH + 20 * u, Width);
                return;
            }
            for (int i = 0; i < rows.Count; i++) PaintRow(g, i);
        }

        void PaintRow(Graphics g, int i)
        {
            float u = U; Row r = rows[i]; Drive d = r.D;
            float top = RowTop(i);
            RectangleF rc = new RectangleF(Pad, top, Width - 2 * Pad, RowH);
            Color st = r.Live ? Theme.Ok : Theme.Bad;
            using (GraphicsPath p = Theme.Round(rc, 12 * u))
            using (Brush b = new SolidBrush(r.Live ? Theme.Card : Theme.Mix(Theme.Bad, Theme.Card, 0.10)))
            using (Pen pen = new Pen(r.Live ? Theme.Line : Theme.Mix(Theme.Bad, Theme.Card, 0.6), 1f * u))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
            using (Brush b = new SolidBrush(st)) g.FillEllipse(b, rc.X + 16 * u, top + RowH / 2 - 5 * u, 10 * u, 10 * u);

            float tx = rc.X + 40 * u;
            float pillW = 74 * u;
            float textRight = CellsX - 14 * u - pillW - 12 * u - (104 * u + 8 * u);
            float tw = textRight - tx;
            string letters = string.IsNullOrEmpty(d.Letters) ? "" : d.Letters.Replace(" ", "  ");
            string title = Fmt.Title(d);
            float nameW = Math.Min(g.MeasureString(title, fName).Width - 6 * u, tw);
            Theme.Txt(g, title, fName, Theme.Text, tx, top + 9 * u, tw);
            if (letters.Length > 0 && nameW + 10 * u < tw)
                Theme.Txt(g, letters, fSub, Theme.Accent, tx + nameW + 8 * u, top + 12 * u, tw - nameW - 8 * u);
            // repeat trouble: 2 or more recorded problems for this drive in the last 90 days
            int recent = Log.Recent(store, d.Key, DateTime.Now);
            if (recent >= 2)
            {
                string bt = recent + " issues in 90 days";
                float lx = tx + nameW + 8 * u + (letters.Length > 0 ? g.MeasureString(letters, fSub).Width + 8 * u : 0);
                float bw = g.MeasureString(bt, fPill).Width + 6 * u;
                if (lx + bw < tx + tw)
                {
                    RectangleF badge = new RectangleF(lx, top + 9 * u, bw, 20 * u);
                    using (GraphicsPath bp = Theme.Round(badge, 10 * u))
                    using (Brush bb = new SolidBrush(Theme.Mix(Theme.Warn, Theme.Card, 0.22))) g.FillPath(bb, bp);
                    using (Brush bb = new SolidBrush(Theme.Warn))
                    {
                        StringFormat bc = new StringFormat(); bc.Alignment = StringAlignment.Center; bc.LineAlignment = StringAlignment.Center;
                        g.DrawString(bt, fPill, bb, badge, bc);
                    }
                }
            }
            List<string> bits = new List<string>();
            bits.Add(Fmt.Size(d.Size));
            string kind = ((d.Bus ?? "") + " " + (d.Media ?? "")).Trim();
            if (kind.Length > 0) bits.Add(kind);
            if (!string.IsNullOrEmpty(d.Nickname)) bits.Add(d.Model);   // the real model stays visible under a nickname
            float subX = tx, subW = tw;
            if (!string.IsNullOrEmpty(d.Note))
            {
                // the note comes first, in brighter text, and gets up to 55% of the line
                float nw = Math.Min(g.MeasureString(d.Note, fSub).Width - 4 * u, tw * 0.55f);
                Theme.Txt(g, d.Note, fSub, Color.FromArgb(225, Theme.Text), tx, top + 33 * u, nw);
                subX = tx + nw + 10 * u; subW = tw - nw - 10 * u;
            }
            if (!r.Live) bits.Add("last seen " + Fmt.When(Fmt.Parse(d.LastSeen)));
            string sn = Fmt.ShortSerial(d.Serial); if (sn.Length > 0) bits.Add(sn);
            if (r.Live && !string.IsNullOrEmpty(d.Health) && d.Health != "Healthy") bits.Add("health: " + d.Health);
            Theme.Txt(g, string.Join("  \u00B7  ", bits.ToArray()), fSub, (r.Live && (d.Health == null || d.Health == "Healthy" || d.Health == "")) ? Theme.Muted : (r.Live ? Theme.Warn : Theme.Mix(Theme.Bad, Theme.Text, 0.55)), subX, top + 33 * u, subW);

            // status pill
            RectangleF pill = new RectangleF(CellsX - 14 * u - pillW, top + RowH / 2 - 12 * u, pillW, 24 * u);
            using (GraphicsPath p = Theme.Round(pill, 12 * u))
            using (Brush b = new SolidBrush(Theme.Mix(st, Theme.Card, 0.22))) g.FillPath(b, p);
            using (Brush b = new SolidBrush(st))
            {
                StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                g.DrawString(r.Live ? "PRESENT" : "MISSING", fPill, b, pill, c);
            }
            if (!r.Live)
            {
                RectangleF br = ButtonRect(i);
                bool hot = hoverRow == i && hoverBtn;
                using (GraphicsPath p = Theme.Round(br, 8 * u))
                using (Brush b = new SolidBrush(hot ? Theme.Mix(Theme.Accent, Theme.Card, 0.9) : Theme.Mix(Theme.Accent, Theme.Card, 0.25)))
                using (Pen pen = new Pen(Theme.Accent, 1f * u))
                { g.FillPath(b, p); g.DrawPath(pen, p); }
                using (Brush b = new SolidBrush(hot ? Color.White : Theme.Text))
                {
                    StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                    g.DrawString("I removed it", fSub, b, br, c);
                }
            }
            else
            {
                // health button, coloured by what the drive's own health data says
                RectangleF br = ButtonRect(i);
                bool hot = hoverRow == i && hoverBtn;
                int lv = Health.Level(d);
                Color hc = lv == 3 ? Theme.Bad : lv == 2 ? Theme.Warn : lv == 1 ? Theme.Ok : Theme.Muted;
                string ht = lv == 3 ? "Health: ALERT" : lv == 2 ? "Health: caution" : lv == 1 ? "Health: OK" : "Health";
                using (GraphicsPath p = Theme.Round(br, 8 * u))
                using (Brush b = new SolidBrush(Theme.Mix(hc, Theme.Card, hot ? 0.35 : 0.14)))
                using (Pen pen = new Pen(Theme.Mix(hc, Theme.Card, 0.6), 1f * u))
                { g.FillPath(b, p); g.DrawPath(pen, p); }
                using (Brush b = new SolidBrush(lv == 0 ? Theme.Text : hc))
                {
                    StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center; c.LineAlignment = StringAlignment.Center;
                    g.DrawString(ht, fSub, b, br, c);
                }
            }

            // 30 day history
            for (int k = 0; k < DaysShown; k++)
            {
                Day dr; DateTime dt;
                int s = CellState(d, k, out dr, out dt);
                Color col = s == 0 ? Theme.Ok : s == 1 ? Theme.Warn : s == 2 ? Theme.Bad : s == 3 ? Theme.Empty : Theme.Before;
                RectangleF cr = CellRect(i, k);
                using (GraphicsPath p = Theme.Round(cr, 3 * u))
                {
                    using (Brush b = new SolidBrush(col)) g.FillPath(b, p);
                    if (k == DaysShown - 1 && s >= 3)
                        using (Pen pen = new Pen(Theme.Muted, 1f * u)) g.DrawPath(pen, p);
                    if (hoverRow == i && hoverDay == k)
                        using (Pen pen = new Pen(Color.White, 1.6f * u)) g.DrawPath(pen, p);
                }
            }
        }

        string CellTip(Drive d, int i)
        {
            Day dr; DateTime dt;
            int s = CellState(d, i, out dr, out dt);
            string head = dt.ToString("dddd, MMM d", CultureInfo.InvariantCulture) + "\n";
            string chk = dr == null ? "" : " (" + Fmt.Clock(Fmt.Parse(dr.CheckedAt)) + ", " + Fmt.KindLabel(dr.Kind).ToLowerInvariant() + ")";
            switch (s)
            {
                case 0: return head + "Showing at the check" + chk;
                case 1: return head + "Was missing at one check this day, then came back";
                case 2: return head + "NOT showing at the check" + chk;
                case 3: return head + "No check ran this day";
                default: return head + "Before Drive Watch 365 knew this drive";
            }
        }

        string InfoTip(Row r)
        {
            Drive d = r.D;
            return Fmt.Title(d) + (string.IsNullOrEmpty(d.Nickname) ? "" : "\nModel: " + d.Model)
                + (string.IsNullOrEmpty(d.Note) ? "" : "\nNote: " + d.Note)
                + "\n" + Fmt.Size(d.Size) + (string.IsNullOrEmpty(d.Letters) ? "" : "   " + d.Letters)
                + "\nSerial: " + (string.IsNullOrEmpty(d.Serial) ? "(none reported)" : d.Serial)
                + "\nFirst seen: " + Fmt.When(Fmt.Parse(d.FirstSeen)) + "\nLast seen: " + Fmt.When(Fmt.Parse(d.LastSeen))
                + "\n(double-click to name it or add a note)";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = -1, day = -1; bool btn = false;
            for (int i = 0; i < rows.Count; i++)
            {
                float t = RowTop(i);
                if (e.Y >= t && e.Y <= t + RowH)
                {
                    row = i;
                    if (ButtonRect(i).Contains(e.X, e.Y)) btn = true;
                    for (int k = 0; k < DaysShown; k++)
                    {
                        RectangleF c = CellRect(i, k);
                        if (e.X >= c.X - Cg / 2 && e.X <= c.Right + Cg / 2 && e.Y >= c.Y && e.Y <= c.Bottom) { day = k; break; }
                    }
                    break;
                }
            }
            Cursor = btn ? Cursors.Hand : Cursors.Default;
            if (row != hoverRow || day != hoverDay || btn != hoverBtn)
            {
                hoverRow = row; hoverDay = day; hoverBtn = btn;
                Invalidate();
                string text = "";
                if (row >= 0 && !btn) text = day >= 0 ? CellTip(rows[row].D, day) : (e.X < CellsX - 10 * U ? InfoTip(rows[row]) : "");
                if (text != lastTip)
                {
                    lastTip = text;
                    if (text.Length == 0) tip.Hide(this); else tip.Show(text, this, e.X + 14, e.Y + 20, 8000);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = hoverDay = -1; hoverBtn = false; lastTip = ""; tip.Hide(this); Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && hoverRow >= 0 && hoverRow < rows.Count && !hoverBtn && hoverDay < 0 && e.X < CellsX - 10 * U && RenameRequested != null)
                RenameRequested(rows[hoverRow].D);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (hoverRow < 0 || hoverRow >= rows.Count) return;
            Row r = rows[hoverRow];
            if (e.Button == MouseButtons.Left && hoverBtn)
            {
                if (!r.Live) { if (ForgetRequested != null) ForgetRequested(r.D); }
                else if (HealthRequested != null) HealthRequested(r.D);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ctxDrive = r.D;
                miForget.Enabled = !r.Live;
                miForget.Text = r.Live ? "I removed this drive (only when it is missing)" : "I removed this drive";
                ctx.Show(this, e.Location);
            }
        }
    }

    class EventList : Control
    {
        List<Ev> events = new List<Ev>();
        Font fh = Theme.F(11, FontStyle.Bold), fb = Theme.F(12, FontStyle.Regular), fw = Theme.F(12, FontStyle.Bold);
        public EventList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public void Bind(List<Ev> e) { events = e; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float u = Theme.Scale;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            float mx = 20 * u;
            Theme.Txt(g, "RECENT ACTIVITY", fh, Theme.Muted, mx, 6 * u, 300 * u);
            float rowH = 25 * u, y = 32 * u;
            int fit = Math.Max(1, (int)((Height - y) / rowH));
            if (events.Count == 0) Theme.Txt(g, "Nothing yet.", fb, Theme.Muted, mx, y, Width);
            for (int i = 0; i < events.Count && i < fit; i++)
            {
                Ev ev = events[i];
                Color c = ev.Level == "bad" ? Theme.Bad : ev.Level == "warn" ? Theme.Warn : ev.Level == "info" ? Theme.Accent : Theme.Ok;
                using (Brush b = new SolidBrush(c)) g.FillEllipse(b, mx, y + 7 * u, 8 * u, 8 * u);
                string when = Fmt.When(Fmt.Parse(ev.At));
                Theme.Txt(g, when, fw, Theme.Muted, mx + 18 * u, y + 1 * u, 130 * u);
                Theme.Txt(g, ev.Text, fb, ev.Level == "bad" ? Theme.Text : Color.FromArgb(205, Theme.Text), mx + 150 * u, y + 1 * u, Width - mx - 150 * u - mx);
                y += rowH;
            }
        }
    }

    // ------------------------------------------------------------------ demo mode

    // "--demo" runs the program on a built-in set of MADE-UP drives, and never saves anything.
    // It exists so screenshots (and a first look at the program) never show anyone's real drives.
    static class Demo
    {
        static Store s;
        static readonly DateTime Now = DateTime.Now;

        static string D(int daysAgo) { return Fmt.DayKey(Now.AddDays(-daysAgo)); }
        static string T(int daysAgo, int hour, int min) { return Fmt.Iso(Now.Date.AddDays(-daysAgo).AddHours(hour).AddMinutes(min)); }

        static Drive Mk(string model, string serial, long size, string letters, string bus, string media, string nick, string note)
        {
            Drive d = new Drive { Model = model, Serial = serial, Size = size, Letters = letters, Bus = bus, Media = media, Health = "Healthy", Nickname = nick, Note = note,
                FirstSeen = D(120), LastSeen = Fmt.Iso(Now) };
            d.Key = model + "|" + serial + "|" + size;
            return d;
        }

        static Smart Sm(string source, int temp, long poh, long cycles, long re, long pe, long un, long crc, int life)
        {
            Smart m = new Smart { Ok = true, Source = source, ReadAt = Fmt.Iso(Now), Temp = temp, PowerOnHours = poh, PowerCycles = cycles, Reallocated = re, Pending = pe, Uncorrectable = un, Crc = crc, Life = life };
            if (source == "smart")
            {
                m.Attrs.Add(new Attr { Id = 5, Name = Health.AttrName(5), Value = 100, Worst = 100, Thresh = 5, Raw = re });
                m.Attrs.Add(new Attr { Id = 9, Name = Health.AttrName(9), Value = 97, Worst = 97, Thresh = 0, Raw = poh });
                m.Attrs.Add(new Attr { Id = 12, Name = Health.AttrName(12), Value = 100, Worst = 100, Thresh = 0, Raw = cycles });
                m.Attrs.Add(new Attr { Id = 194, Name = Health.AttrName(194), Value = 100 - temp, Worst = 100 - temp - 6, Thresh = 0, Raw = temp });
                m.Attrs.Add(new Attr { Id = 197, Name = Health.AttrName(197), Value = 100, Worst = 100, Thresh = 0, Raw = pe });
                m.Attrs.Add(new Attr { Id = 198, Name = Health.AttrName(198), Value = 100, Worst = 100, Thresh = 0, Raw = un });
                m.Attrs.Add(new Attr { Id = 199, Name = Health.AttrName(199), Value = 200, Worst = 200, Thresh = 0, Raw = crc });
            }
            return m;
        }

        // 90 days of one-reading-a-day history; "realloc"/"crc" are given as functions of "days ago"
        static void Trend(Drive d, int stopDaysAgo, int baseTemp, Func<int, long> realloc, Func<int, long> crc, int life0)
        {
            for (int i = 90; i >= stopDaysAgo; i--)
                d.Trend.Add(new Reading { Date = D(i), Temp = baseTemp + (i * 7) % 5 - 2, Life = life0 < 0 ? -1 : life0 + i / 30, Poh = d.Smart.PowerOnHours - i * 24,
                    Realloc = realloc(i), Pending = 0, Uncorr = 0, Crc = crc(i) });
        }

        static Incident Inc(Drive d, string type, string start, string end, string endKind, string kind, string seen, string note)
        {
            return new Incident { Key = d.Key, Type = type, Name = Fmt.Title(d), Model = d.Model, Letters = d.Letters, Serial = d.Serial, Size = d.Size,
                Start = start, End = end, EndKind = endKind, StartKind = kind, LastSeen = seen, Note = note };
        }

        public static Store Build()
        {
            s = new Store { Started = D(120), TrayHintShown = true };
            Drive c = Mk("Samsung SSD 870 EVO 1TB", "S5Y1NX0T123456A", 1000204886016, "C:", "SATA", "SSD", "Windows + apps", "M.2 slot on the motherboard");
            Drive d = Mk("WDC WD40EFZX-68AWUN0", "WD-WX12D90A1B2C", 4000787030016, "D:", "SATA", "HDD", "Family photos", "Bay 1");
            Drive e = Mk("ST8000VN004-2M2101", "WSD4K8XZ", 8001563222016, "E:", "SATA", "HDD", "Media library", "Bay 2. Eight remapped sectors, holding steady.");
            Drive f = Mk("WDC WD20EZAZ-00GGJB0", "WD-WXB2A1234567", 2000398934016, "F:", "SATA", "HDD", "Old backups", "Bay 4");
            Drive g = Mk("TOSHIBA X300 HDWE160", "Y9R0A0HVFB7G", 6001175126016, "G:", "SATA", "HDD", "Archive", "Bay 3. Needs a new cable?");
            Drive h = Mk("CT500MX500SSD1", "2145E5A1B3C9", 500107862016, "H:", "SATA", "SSD", "Games", null);
            Drive j = Mk("WD Elements 25A3 USB Device", "575834314131", 5000981077504, "J:", "USB", "", "Travel drive", "In the desk drawer");
            c.Smart = Sm("smart", 34, 9812, 412, 0, 0, 0, 0, 97);        Trend(c, 0, 34, i => 0, i => 0, 97);
            d.Smart = Sm("smart", 36, 31204, 188, 0, 0, 0, 0, -1);       Trend(d, 0, 36, i => 0, i => 0, -1);
            e.Smart = Sm("smart", 41, 43890, 301, 8, 0, 0, 0, -1);       Trend(e, 0, 41, i => 8, i => 0, -1);
            f.Smart = Sm("smart", 32, 24560, 220, 0, 0, 0, 5, -1);       Trend(f, 0, 32, i => 0, i => i >= 25 ? 0 : (i >= 20 ? 1 : (i >= 15 ? 2 : (i >= 10 ? 3 : (i >= 5 ? 4 : 5)))), -1);
            g.Smart = Sm("smart", 38, 38120, 265, 0, 0, 0, 0, -1);       Trend(g, 4, 38, i => 0, i => 0, -1);
            h.Smart = Sm("smart", 31, 14120, 150, 0, 0, 0, 0, 88);       Trend(h, 0, 31, i => 0, i => 0, 88);
            j.Smart = Sm("windows", 29, 5120, -1, -1, -1, 0, -1, -1);    Trend(j, 0, 29, i => -1, i => -1, -1);
            foreach (Drive x in new Drive[] { c, d, e, f, g, h, j }) s.Drives[x.Key] = x;

            // 30 days of daily checks: the archive drive has been gone for the last 4, the old-backups drive had one slow morning
            for (int i = 29; i >= 0; i--)
            {
                Day day = new Day { Date = D(i), CheckedAt = T(i, 8, 2), Kind = i % 3 == 0 ? "Startup" : "Daily" };
                foreach (Drive x in s.Drives.Values) if (!(x == g && i <= 3)) day.Present.Add(x.Key);
                if (i <= 3) day.Dipped.Add(g.Key);
                if (i == 12) day.Dipped.Add(f.Key);
                day.Total = day.Present.Count;
                s.Days[day.Date] = day;
            }

            // permanent problem records
            Drive old = Mk("Maxtor 6L160P0", "L61AB3FC", 160041885696, "L:", "ATA", "HDD", "Old IDE drive", null);
            s.Incidents.Add(Inc(old, "missing", T(75, 8, 2), T(73, 19, 40), "removed", "Daily", T(76, 22, 5), null));
            s.Incidents.Add(Inc(g, "missing", T(41, 8, 2), T(38, 19, 0), "back", "Startup", T(42, 22, 10), null));
            s.Incidents.Add(Inc(f, "late", T(12, 8, 2), T(12, 8, 2), "back", "Startup", null, "Not there on the first look after startup; showed up on try 2 of 4 (about 30 seconds later)."));
            s.Incidents.Add(Inc(f, "health", T(15, 8, 2), T(15, 8, 2), "info", "Daily", null, "Cable/connection errors (often a loose or bad cable) went from 1 to 2."));
            s.Incidents.Add(Inc(f, "health", T(10, 8, 2), T(10, 8, 2), "info", "Daily", null, "Cable/connection errors (often a loose or bad cable) went from 2 to 3."));
            s.Incidents.Add(Inc(f, "health", T(5, 8, 2), T(5, 8, 2), "info", "Daily", null, "Cable/connection errors (often a loose or bad cable) went from 3 to 4."));
            s.Incidents.Add(Inc(g, "missing", T(4, 8, 2), null, null, "Startup", T(5, 22, 10), null));
            s.Events.Add(new Ev { At = T(1, 8, 2), Level = "warn", Text = "Daily check: 6 of 7 drives showing. Still missing: Archive (4 days)" });
            s.Events.Add(new Ev { At = T(2, 8, 2), Level = "warn", Text = "Daily check: 6 of 7 drives showing. Still missing: Archive (3 days)" });
            s.Events.Add(new Ev { At = T(3, 8, 2), Level = "warn", Text = "Daily check: 6 of 7 drives showing. Still missing: Archive (2 days)" });
            s.Events.Add(new Ev { At = T(4, 8, 2), Level = "bad", Text = "Startup check: 6 of 7 drives showing. Newly missing: Archive (G:), 6 TB" });
            s.Events.Add(new Ev { At = T(5, 8, 2), Level = "ok", Text = "Daily check: all 7 drives showing." });
            return s;
        }

        // the drives that "show up" in a demo check: all but the archive drive
        public static List<Drive> Live()
        {
            return s.Drives.Values.Where(x => x.Model != "TOSHIBA X300 HDWE160").ToList();
        }
    }

    // ------------------------------------------------------------------ problem history window

    class IncidentList : Control
    {
        Store store; List<Incident> items = new List<Incident>();
        Font fT = Theme.F(20, FontStyle.Bold), fName = Theme.F(15, FontStyle.Bold), fSub = Theme.F(12, FontStyle.Regular),
             fPill = Theme.F(11, FontStyle.Bold), fSm = Theme.F(12, FontStyle.Regular);
        float U { get { return Theme.Scale; } }
        float HeadH { get { return 92 * U; } }
        float RowH { get { return 62 * U; } }
        float Gap { get { return 6 * U; } }

        public IncidentList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void Bind(Store s)
        {
            store = s;
            items = s.Incidents.OrderByDescending(i => i.Start).ToList();
            Height = (int)(HeadH + Math.Max(1, items.Count) * (RowH + Gap) + 16 * U);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float u = U;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            if (store == null) return;
            float mx = 20 * u;
            Theme.Txt(g, "Problem history", fT, Theme.Text, mx, 12 * u, Width - 2 * mx);
            int open = items.Count(i => i.Type == "missing" && i.End == null);
            int drives = items.Select(i => i.Key).Distinct().Count();
            string sum = items.Count == 0
                ? "Nothing has gone wrong yet. If a drive disappears, or turns up late after a reboot, it is written down here and kept."
                : items.Count + " on record  ·  " + open + " still open  ·  " + drives + (drives == 1 ? " drive" : " drives") + " involved.  Only changes are recorded: a drive that stays gone is one line, not one per day.";
            Theme.Txt(g, sum, fSub, Theme.Muted, mx, 52 * u, Width - 2 * mx);
            for (int i = 0; i < items.Count; i++) PaintRow(g, i);
        }

        void PaintRow(Graphics g, int idx)
        {
            float u = U; Incident n = items[idx];
            float top = HeadH + idx * (RowH + Gap), mx = 16 * u;
            RectangleF rc = new RectangleF(mx, top, Width - 2 * mx, RowH);
            bool open = n.Type == "missing" && n.End == null;
            bool health = n.Type == "health";
            Color c = open ? Theme.Bad : (health ? (n.EndKind == "bad" ? Theme.Bad : Theme.Warn) : (n.EndKind == "removed" ? Theme.Accent : Theme.Warn));
            using (GraphicsPath p = Theme.Round(rc, 12 * u))
            using (Brush b = new SolidBrush(open ? Theme.Mix(Theme.Bad, Theme.Card, 0.10) : Theme.Card))
            using (Pen pen = new Pen(open ? Theme.Mix(Theme.Bad, Theme.Card, 0.6) : Theme.Line, 1f * u))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
            using (Brush b = new SolidBrush(c)) g.FillEllipse(b, rc.X + 16 * u, top + RowH / 2 - 5 * u, 10 * u, 10 * u);

            float pillW = 140 * u;
            RectangleF pill = new RectangleF(rc.Right - 16 * u - pillW, top + RowH / 2 - 12 * u, pillW, 24 * u);
            string pt = open ? "STILL GONE" : (health ? (n.EndKind == "bad" ? "HEALTH ALERT" : "HEALTH WARNING") : (n.Type == "late" ? "SLOW TO APPEAR" : (n.EndKind == "removed" ? "REMOVED" : "BACK")));
            using (GraphicsPath p = Theme.Round(pill, 12 * u))
            using (Brush b = new SolidBrush(Theme.Mix(c, Theme.Card, 0.22))) g.FillPath(b, p);
            using (Brush b = new SolidBrush(c))
            {
                StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(pt, fPill, b, pill, sf);
            }

            float tx = rc.X + 40 * u, tw = pill.X - tx - 12 * u;
            string name = string.IsNullOrEmpty(n.Name) ? n.Model : n.Name;
            float nameW = Math.Min(g.MeasureString(name, fName).Width - 6 * u, tw);
            Theme.Txt(g, name, fName, Theme.Text, tx, top + 8 * u, tw);
            float x = tx + nameW + 8 * u;
            string tail = (string.IsNullOrEmpty(n.Letters) ? "" : n.Letters.Replace(" ", "  "));
            if (tail.Length > 0 && x < tx + tw) { Theme.Txt(g, tail, fSub, Theme.Accent, x, top + 11 * u, tx + tw - x); x += g.MeasureString(tail, fSub).Width + 8 * u; }
            int rec = Log.Recent(store, n.Key, Fmt.Parse(n.Start));
            if (rec >= 2 && x < tx + tw - 60 * u)
                Theme.Txt(g, Fmt.Ordinal(rec) + " problem in 90 days", fPill, Theme.Warn, x, top + 12 * u, tx + tw - x);

            DateTime start = Fmt.Parse(n.Start), end = Fmt.Parse(n.End), seen = Fmt.Parse(n.LastSeen);
            string line;
            if (health) line = Fmt.WhenFull(start) + "  ·  " + (n.Note ?? "Health warning.");
            else if (n.Type == "late") line = n.Note ?? "Slow to appear after startup.";
            else
            {
                line = "Noticed missing " + Fmt.WhenFull(start) + " (" + Fmt.KindLabel(n.StartKind).ToLowerInvariant() + ")";
                if (open) line += "  ·  still gone, " + Fmt.Span(start, DateTime.Now);
                else if (n.EndKind == "removed") line += "  ·  you removed it " + Fmt.DateOnly(end);
                else line += "  ·  back " + Fmt.WhenFull(end) + ", gone " + Fmt.Span(start, end);
                if (seen != DateTime.MinValue) line += "  ·  last seen before that: " + Fmt.When(seen);
            }
            Theme.Txt(g, line, fSub, Color.FromArgb(205, Theme.Text), tx, top + 33 * u, tw);
        }
    }

    class HistoryForm : Form
    {
        public HistoryForm(Store s, Icon icon)
        {
            Text = "Problem history"; Icon = icon; BackColor = Theme.Bg; ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.Sc(940), wa.Width - 60), Math.Min(Theme.Sc(640), wa.Height - 80));
            MinimumSize = new Size(Math.Min(Theme.Sc(640), wa.Width - 60), Math.Min(Theme.Sc(360), wa.Height - 80));
            Panel host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            IncidentList list = new IncidentList { Location = new Point(0, 0), Width = host.ClientSize.Width };
            list.Bind(s);
            host.Controls.Add(list);
            host.ClientSizeChanged += delegate { list.Width = host.ClientSize.Width; };
            Controls.Add(host);
            KeyPreview = true;
            KeyDown += delegate (object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }
    }

    // ------------------------------------------------------------------ health detail window

    class HealthView : Control
    {
        Drive d; Store store;
        Font fT = Theme.F(22, FontStyle.Bold), fB = Theme.F(15, FontStyle.Bold), fS = Theme.F(12, FontStyle.Regular),
             fSm = Theme.F(11, FontStyle.Regular), fBig = Theme.F(24, FontStyle.Bold), fPill = Theme.F(11, FontStyle.Bold);
        float U { get { return Theme.Scale; } }
        List<Incident> changes;

        public HealthView(Drive drive, Store s)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            d = drive; store = s;
            changes = s.Incidents.Where(i => i.Key == d.Key && i.Type == "health").OrderByDescending(i => i.Start).Take(8).ToList();
            int attrs = d.Smart != null && d.Smart.Ok ? d.Smart.Attrs.Count : 0;
            float h = 16 + 36 + 28 + 28 + 70;                      // header + verdict
            if (attrs > 0 || (d.Smart != null && d.Smart.Ok)) h += 2 * 88 + 12 + 170 + 20;   // tiles + charts
            if (attrs > 0) h += 46 + attrs * 26 + 20;               // attribute table
            h += 40 + Math.Max(1, changes.Count) * 26 + 30;         // changes on record
            Height = (int)(h * U);
        }

        string Verdict(out int level)
        {
            level = Health.Level(d);
            Smart s = d.Smart;
            if (s == null || !s.Ok) return (d.SmartWhy ?? "No health reading yet.") + (Program.IsAdmin ? "" : "  Press \"Turn on health details\" above.");
            List<string> bits = new List<string>(), notes = new List<string>();
            if (s.PredictFail) bits.Add("Windows reports this drive is predicted to fail");
            foreach (Attr a in s.Attrs) if (a.Thresh > 0 && a.Value > 0 && a.Value <= a.Thresh) bits.Add(a.Name + " is at its failure threshold");
            // Counts are judged by how they MOVE: a small steady count is noted, a climbing one is flagged.
            object[][] counters = {
                new object[] { "reallocated sectors", Health.RA, s.Reallocated },
                new object[] { "sectors waiting to be remapped", Health.PE, s.Pending },
                new object[] { "uncorrectable sectors", Health.UN, s.Uncorrectable },
                new object[] { "cable errors", Health.CR, s.Crc } };
            foreach (object[] c in counters)
            {
                string name = (string)c[0]; Func<Reading, long> sel = (Func<Reading, long>)c[1]; long cur = (long)c[2];
                if (cur < 0) continue;
                if (cur > 0 && Health.Climbing(d, sel)) bits.Add(name + " climbing fast (+" + Health.Delta(d, sel, 7) + " in the last week)");
                else if (Health.RoseRecently(d, sel)) bits.Add(name + " rose recently (+" + Health.Delta(d, sel, 30) + " in 30 days)");
                else if (cur > 0 && sel == Health.PE) bits.Add(cur + " " + name + " (these usually clear or get remapped; watch whether it grows)");
                else if (cur > 0) notes.Add(cur + " " + name + ", steady for " + Math.Max(1, Health.SteadyDays(d, sel)) + " days");
            }
            if (s.Life >= 0 && s.Life <= 20) bits.Add("SSD life left " + s.Life + "%");
            if (s.Temp >= Health.TempLimit(d)) bits.Add("running hot at " + s.Temp + "°C");
            if (level == 1) return "Looks healthy." + (notes.Count > 0 ? " Not climbing: " + string.Join(";  ", notes.ToArray()) + "." : " Nothing in the drive's own health data is a concern.");
            return (level == 3 ? "ALERT: " : "Caution: ") + string.Join(";  ", bits.ToArray()) + ".";
        }

        void Tile(Graphics g, RectangleF r, string label, string value, string sub, Color vc)
        {
            float u = U;
            using (GraphicsPath p = Theme.Round(r, 10 * u))
            using (Brush b = new SolidBrush(Theme.Card))
            using (Pen pen = new Pen(Theme.Line, 1f * u)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            Theme.Txt(g, label.ToUpperInvariant(), fPill, Theme.Muted, r.X + 14 * u, r.Y + 9 * u, r.Width - 24 * u);
            Theme.Txt(g, value, fBig, vc, r.X + 14 * u, r.Y + 26 * u, r.Width - 24 * u);
            Theme.Txt(g, sub, fSm, Theme.Muted, r.X + 14 * u, r.Y + 62 * u, r.Width - 24 * u);
        }

        // A counter tile: green at 0, calm when steady, amber when it rose lately, red when climbing fast.
        void Counter(Graphics g, RectangleF r, string label, long val, Func<Reading, long> sel, string zeroSub)
        {
            Color c = Theme.Muted; string sub = zeroSub;
            if (val >= 0)
            {
                if (val > 0 && Health.Climbing(d, sel)) { c = Theme.Bad; sub = "climbing: +" + Health.Delta(d, sel, 7) + " this week"; }
                else if (Health.RoseRecently(d, sel)) { c = Theme.Warn; sub = "rose +" + Health.Delta(d, sel, 30) + " in 30 days"; }
                else if (val > 0) { c = Theme.Text; sub = d.Trend.Count(t => sel(t) >= 0) < 2 ? "watching for changes" : "steady for " + Math.Max(1, Health.SteadyDays(d, sel)) + " days"; }
                else c = Theme.Ok;
            }
            Tile(g, r, label, val < 0 ? "n/a" : val.ToString("N0", CultureInfo.InvariantCulture), sub, c);
        }

        void Spark(Graphics g, RectangleF r, string title, Func<Reading, double> sel, Color col, string unit)
        {
            float u = U;
            using (GraphicsPath p = Theme.Round(r, 10 * u))
            using (Brush b = new SolidBrush(Theme.Card))
            using (Pen pen = new Pen(Theme.Line, 1f * u)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            Theme.Txt(g, title, fPill, Theme.Muted, r.X + 14 * u, r.Y + 9 * u, r.Width - 24 * u);
            List<Reading> pts = d.Trend.Where(t => sel(t) >= 0).ToList();
            RectangleF pr = new RectangleF(r.X + 14 * u, r.Y + 34 * u, r.Width - 28 * u, r.Height - 70 * u);
            if (pts.Count < 2)
            {
                Theme.Txt(g, pts.Count == 1 ? "First reading: " + sel(pts[0]).ToString("0", CultureInfo.InvariantCulture) + unit + ". A trend appears after a second day." : "No readings yet.", fS, Theme.Muted, pr.X, pr.Y + 20 * u, pr.Width);
                return;
            }
            DateTime t0 = Fmt.Parse(pts[0].Date), t1 = Fmt.Parse(pts[pts.Count - 1].Date);
            double span = Math.Max(1, (t1 - t0).TotalDays);
            double mn = pts.Min(sel), mx = pts.Max(sel), mn0 = mn, mx0 = mx; if (mx - mn < 1) { mx += 1; mn -= 1; }
            List<PointF> poly = new List<PointF>();
            foreach (Reading t in pts)
                poly.Add(new PointF(pr.X + (float)((Fmt.Parse(t.Date) - t0).TotalDays / span) * pr.Width, pr.Bottom - (float)((sel(t) - mn) / (mx - mn)) * pr.Height));
            using (Pen lp = new Pen(col, 2f * u)) { lp.LineJoin = LineJoin.Round; g.DrawLines(lp, poly.ToArray()); }
            using (Brush b = new SolidBrush(col)) g.FillEllipse(b, poly[poly.Count - 1].X - 3.5f * u, poly[poly.Count - 1].Y - 3.5f * u, 7 * u, 7 * u);
            string cap = "low " + mn0.ToString("0") + unit + "   high " + mx0.ToString("0") + unit + "   now " + sel(pts[pts.Count - 1]).ToString("0") + unit + "   (" + (int)span + " days)";
            Theme.Txt(g, cap, fSm, Theme.Muted, r.X + 14 * u, r.Bottom - 24 * u, r.Width - 24 * u);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float u = U;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Theme.Bg);
            float mx = 20 * u, w = Width - 2 * mx, y = 16 * u;
            Smart s = d.Smart; bool have = s != null && s.Ok;
            string title = Fmt.Title(d);
            Theme.Txt(g, title, fT, Theme.Text, mx, y, w);
            float tw = g.MeasureString(title, fT).Width - 8 * u;
            if (!string.IsNullOrEmpty(d.Letters)) Theme.Txt(g, d.Letters.Replace(" ", "  "), fB, Theme.Accent, mx + tw + 10 * u, y + 6 * u, 200 * u);
            y += 38 * u;
            Theme.Txt(g, d.Model + "  ·  " + Fmt.Size(d.Size) + "  ·  " + ((d.Bus ?? "") + " " + (d.Media ?? "")).Trim() + "  ·  serial " + (string.IsNullOrEmpty(d.Serial) ? "(none)" : d.Serial), fS, Theme.Muted, mx, y, w);
            y += 24 * u;
            Theme.Txt(g, have ? "Read " + Fmt.When(Fmt.Parse(s.ReadAt)) + (s.Source == "windows" ? "  ·  basic Windows counters only (this drive does not give the full table)" : "  ·  full drive health table") : "No health reading", fSm, Theme.Muted, mx, y, w);
            y += 28 * u;

            int lv; string verdict = Verdict(out lv);
            Color vc = lv == 3 ? Theme.Bad : lv == 2 ? Theme.Warn : lv == 1 ? Theme.Ok : Theme.Muted;
            RectangleF vr = new RectangleF(mx, y, w, 56 * u);
            using (GraphicsPath p = Theme.Round(vr, 12 * u))
            using (Brush b = new SolidBrush(Theme.Mix(vc, Theme.Card, 0.14)))
            using (Pen pen = new Pen(Theme.Mix(vc, Theme.Card, 0.55), 1.2f * u)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            using (Brush b = new SolidBrush(vc)) g.FillEllipse(b, vr.X + 16 * u, vr.Y + 22 * u, 12 * u, 12 * u);
            using (Brush b = new SolidBrush(Theme.Text))
                g.DrawString(verdict, fS, b, new RectangleF(vr.X + 40 * u, vr.Y + 8 * u, vr.Width - 54 * u, vr.Height - 12 * u));
            y += 70 * u;

            if (have)
            {
                float gap = 10 * u, tileW = (w - 3 * gap) / 4, th = 88 * u;
                Func<long, string> N = v => v < 0 ? "n/a" : v.ToString("N0", CultureInfo.InvariantCulture);
                Func<long, Color> Z = v => v > 0 ? Theme.Warn : (v == 0 ? Theme.Ok : Theme.Muted);
                int lim = Health.TempLimit(d);
                Tile(g, new RectangleF(mx, y, tileW, th), "Temperature", s.Temp < 0 ? "n/a" : s.Temp + "°C", "warning level " + lim + "°C", s.Temp >= lim ? Theme.Warn : (s.Temp < 0 ? Theme.Muted : Theme.Text));
                Tile(g, new RectangleF(mx + (tileW + gap), y, tileW, th), "Power-on time", s.PowerOnHours < 0 ? "n/a" : N(s.PowerOnHours) + " h", s.PowerOnHours < 0 ? "" : "about " + Fmt.Hours(s.PowerOnHours), Theme.Text);
                Tile(g, new RectangleF(mx + 2 * (tileW + gap), y, tileW, th), "Power cycles", N(s.PowerCycles), "times it was switched on", Theme.Text);
                Tile(g, new RectangleF(mx + 3 * (tileW + gap), y, tileW, th), "SSD life left", s.Life < 0 ? (d.Media == "SSD" ? "n/a" : "—") : s.Life + "%", d.Media == "SSD" ? "of its rated life" : "only SSDs report this", s.Life >= 0 && s.Life <= 20 ? Theme.Warn : Theme.Text);
                y += th + gap;
                Counter(g, new RectangleF(mx, y, tileW, th), "Reallocated sectors", s.Reallocated, Health.RA, "none: bad spots swapped out");
                Counter(g, new RectangleF(mx + (tileW + gap), y, tileW, th), "Pending sectors", s.Pending, Health.PE, "none: nothing waiting");
                Counter(g, new RectangleF(mx + 2 * (tileW + gap), y, tileW, th), "Uncorrectable", s.Uncorrectable, Health.UN, "none: nothing lost");
                Counter(g, new RectangleF(mx + 3 * (tileW + gap), y, tileW, th), "Cable errors (CRC)", s.Crc, Health.CR, "none: cable looks fine");
                y += th + 12 * u;

                float cw = (w - gap) / 2, ch = 170 * u;
                Spark(g, new RectangleF(mx, y, cw, ch), "TEMPERATURE OVER TIME", t => t.Temp > 0 ? t.Temp : -1, Theme.Accent, "°C");
                Spark(g, new RectangleF(mx + cw + gap, y, cw, ch), "REALLOCATED + PENDING SECTORS OVER TIME", t => (t.Realloc < 0 && t.Pending < 0) ? -1 : Math.Max(0, t.Realloc) + Math.Max(0, t.Pending), Theme.Warn, "");
                y += ch + 20 * u;
            }

            if (have && s.Attrs.Count > 0)
            {
                Theme.Txt(g, "ALL HEALTH ATTRIBUTES THE DRIVE REPORTS", fPill, Theme.Muted, mx, y, w);
                y += 22 * u;
                float[] cx = { 0, 56, 340, 430, 520, 620 };
                string[] hd = { "ID", "Attribute", "Current", "Worst", "Limit", "Raw value" };
                for (int i = 0; i < hd.Length; i++) Theme.Txt(g, hd[i], fSm, Theme.Muted, mx + 30 * u + cx[i] * u, y, 200 * u);
                y += 24 * u;
                foreach (Attr a in s.Attrs)
                {
                    bool bad = a.Thresh > 0 && a.Value > 0 && a.Value <= a.Thresh;
                    Func<Reading, long> asel = Health.SelFor(a.Id);
                    Color dc = bad ? Theme.Bad : (asel != null && (Health.Climbing(d, asel) ? true : false) ? Theme.Bad : (asel != null && Health.RoseRecently(d, asel) ? Theme.Warn : Theme.Ok));
                    using (Brush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, mx, y - 2 * u, w, 24 * u);
                    using (Brush b = new SolidBrush(dc)) g.FillEllipse(b, mx + 10 * u, y + 5 * u, 9 * u, 9 * u);
                    Theme.Txt(g, a.Id.ToString("X2"), fS, Theme.Muted, mx + 30 * u + cx[0] * u, y, 50 * u);
                    Theme.Txt(g, a.Name, fS, Theme.Text, mx + 30 * u + cx[1] * u, y, 280 * u);
                    Theme.Txt(g, a.Value.ToString(), fS, Theme.Text, mx + 30 * u + cx[2] * u, y, 80 * u);
                    Theme.Txt(g, a.Worst.ToString(), fS, Theme.Text, mx + 30 * u + cx[3] * u, y, 80 * u);
                    Theme.Txt(g, a.Thresh.ToString(), fS, Theme.Muted, mx + 30 * u + cx[4] * u, y, 80 * u);
                    Theme.Txt(g, a.Raw.ToString("N0", CultureInfo.InvariantCulture), fS, bad ? Theme.Bad : Theme.Text, mx + 30 * u + cx[5] * u, y, 200 * u);
                    y += 26 * u;
                }
                y += 20 * u;
            }

            Theme.Txt(g, "CHANGES ON RECORD FOR THIS DRIVE", fPill, Theme.Muted, mx, y, w);
            y += 26 * u;
            if (changes.Count == 0) Theme.Txt(g, "None. If a health number gets worse, it is written down here and in History.", fS, Theme.Muted, mx, y, w);
            foreach (Incident n in changes)
            {
                using (Brush b = new SolidBrush(n.EndKind == "bad" ? Theme.Bad : Theme.Warn)) g.FillEllipse(b, mx + 2 * u, y + 5 * u, 9 * u, 9 * u);
                Theme.Txt(g, Fmt.WhenFull(Fmt.Parse(n.Start)), fS, Theme.Muted, mx + 20 * u, y, 150 * u);
                Theme.Txt(g, n.Note ?? "", fS, Theme.Text, mx + 180 * u, y, w - 180 * u);
                y += 26 * u;
            }
        }
    }

    class HealthForm : Form
    {
        public bool TurnOnRequested;
        public HealthForm(Store s, Drive d, Icon icon)
        {
            Text = "Drive health - " + Fmt.Title(d); Icon = icon; BackColor = Theme.Bg; ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(Theme.Sc(900), wa.Width - 60), Math.Min(Theme.Sc(780), wa.Height - 80));
            MinimumSize = new Size(Math.Min(Theme.Sc(700), wa.Width - 60), Math.Min(Theme.Sc(420), wa.Height - 80));
            Panel host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            HealthView view = new HealthView(d, s) { Location = new Point(0, 0), Width = host.ClientSize.Width };
            host.Controls.Add(view);
            host.ClientSizeChanged += delegate { view.Width = host.ClientSize.Width; };
            if (!Program.IsAdmin)
            {
                Button on = new Button { Text = "Turn on health details", FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.F(13, FontStyle.Bold), Size = new Size(Theme.Sc(220), Theme.Sc(38)), Dock = DockStyle.Top, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
                on.FlatAppearance.BorderSize = 0;
                on.Click += delegate { TurnOnRequested = true; Close(); };
                Controls.Add(host);
                Controls.Add(on);
            }
            else Controls.Add(host);
            KeyPreview = true;
            KeyDown += delegate (object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        }
    }

    // ------------------------------------------------------------------ main window + tray

    enum Status { Unknown, Ok, Missing, Retrying, Error }

    class MainForm : Form
    {
        const int MaxAttempts = 4;       // startup check: first look + 3 re-looks
        const int RetrySeconds = 30;

        Store store;
        Dictionary<string, Drive> live = new Dictionary<string, Drive>();
        bool checking, scannedOnce, exiting, startHidden, allowShow, test;
        int attempt; string kind = "Launch", lastError = "", snapPath;
        HashSet<string> sawMissing = new HashSet<string>(), lateLogged = new HashSet<string>();
        List<Incident> healthNew = new List<Incident>();
        bool taskExists; Panel adminBar;
        Status status = Status.Unknown;

        Surface head; Label lblTitle, lblSub; Button btnCheck, btnHistory; CheckBox chkAuto;
        Banner banner; DriveGrid grid; Panel gridHost; EventList events;
        NotifyIcon tray; ContextMenuStrip trayMenu; ToolStripMenuItem miAuto;
        System.Windows.Forms.Timer retryTimer, dailyTimer;
        Icon trayIcon, formIcon; Bitmap glyph;
        EventWaitHandle showSignal;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public MainForm(bool hidden, string snap, bool testMode)
        {
            startHidden = hidden; snapPath = snap; test = testMode;
            store = Program.Demo ? Demo.Build() : Persist.Load();
            ImportOld();
            Theme.Init();
            Text = "Drive Watch 365";
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.F(13, FontStyle.Regular);
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size want = new Size(Theme.Sc(1060), Theme.Sc(760));
            ClientSize = new Size(Math.Min(want.Width, wa.Width - 40), Math.Min(want.Height, wa.Height - 60));
            MinimumSize = new Size(Math.Min(Theme.Sc(900), wa.Width - 40), Math.Min(Theme.Sc(560), wa.Height - 60));

            glyph = Icons.Draw(Theme.Sc(48), null);
            formIcon = IconFor(Theme.Ok);
            Icon = formIcon;

            // header
            head = new Surface { Dock = DockStyle.Top, Height = Theme.Sc(88), BackColor = Theme.Bg };
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawImage(glyph, Theme.Sc(24), Theme.Sc(20), glyph.Width, glyph.Height);
            };
            lblTitle = new Label { Text = "Drive Watch 365", Font = Theme.F(26, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = true, Location = new Point(Theme.Sc(84), Theme.Sc(14)) };
            lblSub = new Label { Text = "", Font = Theme.F(13, FontStyle.Regular), ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = true, Location = new Point(Theme.Sc(86), Theme.Sc(54)) };
            btnCheck = new Button { Text = "Check now", FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.F(13, FontStyle.Bold), Size = new Size(Theme.Sc(124), Theme.Sc(38)), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            btnCheck.FlatAppearance.BorderSize = 0;
            btnCheck.FlatAppearance.MouseOverBackColor = Color.FromArgb(104, 160, 255);
            btnCheck.Click += delegate { StartCheck("Manual"); };
            chkAuto = new CheckBox { Text = "Start with Windows", ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.F(13, FontStyle.Regular), AutoSize = true, Cursor = Cursors.Hand };
            chkAuto.Click += delegate { SetAuto(chkAuto.Checked); SyncAuto(); };
            btnHistory = new Button { Text = "History", FlatStyle = FlatStyle.Flat, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.F(13, FontStyle.Bold), Size = new Size(Theme.Sc(100), Theme.Sc(38)), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            btnHistory.FlatAppearance.BorderColor = Theme.Line;
            btnHistory.FlatAppearance.MouseOverBackColor = Theme.Line;
            btnHistory.Click += delegate { ShowHistory(); };
            head.Controls.AddRange(new Control[] { lblTitle, lblSub, btnCheck, chkAuto, btnHistory });
            head.Resize += delegate { LayoutHead(); };

            banner = new Banner { Dock = DockStyle.Top, Height = Theme.Sc(96) };
            events = new EventList { Dock = DockStyle.Bottom, Height = Theme.Sc(150) };
            gridHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            grid = new DriveGrid { Location = new Point(0, 0), Width = gridHost.ClientSize.Width, Height = Theme.Sc(120) };
            grid.ForgetRequested += OnForget;
            grid.RenameRequested += OnRename;
            grid.HealthRequested += ShowHealth;
            gridHost.Controls.Add(grid);
            gridHost.Resize += delegate { grid.Width = gridHost.ClientSize.Width; };
            gridHost.ClientSizeChanged += delegate { grid.Width = gridHost.ClientSize.Width; };

            Controls.Add(gridHost);
            Controls.Add(events);
            // slim bar offering health details, shown only while the app is running without admin rights
            adminBar = new Panel { Dock = DockStyle.Top, Height = Theme.Sc(46), BackColor = Theme.Mix(Theme.Accent, Theme.Bg, 0.12), Visible = !Program.IsAdmin };
            Label abl = new Label { Text = "Drive health (temperature, bad sectors, hours on) needs administrator rights.", ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = true, Location = new Point(Theme.Sc(20), Theme.Sc(13)), Font = Theme.F(13, FontStyle.Regular) };
            Button abb = new Button { Text = "Turn on health details", FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.F(12, FontStyle.Bold), Size = new Size(Theme.Sc(190), Theme.Sc(30)), Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            abb.FlatAppearance.BorderSize = 0;
            abb.Click += delegate { EnableAdmin(); };
            adminBar.Controls.Add(abl); adminBar.Controls.Add(abb);
            adminBar.Resize += delegate { abb.Location = new Point(adminBar.Width - abb.Width - Theme.Sc(24), Theme.Sc(8)); };
            abb.Location = new Point(Theme.Sc(840), Theme.Sc(8));
            Controls.Add(adminBar);
            Controls.Add(banner);
            Controls.Add(head);
            LayoutHead();

            // tray
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem miOpen = new ToolStripMenuItem("Open Drive Watch 365"); miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miOpen.Click += delegate { ShowMain(); };
            ToolStripMenuItem miNow = new ToolStripMenuItem("Check now");
            miNow.Click += delegate { StartCheck("Manual"); };
            ToolStripMenuItem miHist = new ToolStripMenuItem("Problem history");
            miHist.Click += delegate { ShowMain(); ShowHistory(); };
            ToolStripMenuItem miAbout = new ToolStripMenuItem("About Drive Watch 365");
            miAbout.Click += delegate { ShowMain(); ShowAbout(); };
            miAuto = new ToolStripMenuItem("Start with Windows");
            miAuto.Click += delegate { SetAuto(!GetAuto()); SyncAuto(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate { ExitApp(); };
            trayMenu.Items.AddRange(new ToolStripItem[] { miOpen, miNow, miHist, new ToolStripSeparator(), miAuto, new ToolStripSeparator(), miAbout, miExit });
            tray = new NotifyIcon { ContextMenuStrip = trayMenu, Text = "Drive Watch 365", Visible = !test };
            tray.DoubleClick += delegate { ShowMain(); };
            tray.BalloonTipClicked += delegate { ShowMain(); };
            SetTrayLook(Theme.Accent);

            retryTimer = new System.Windows.Forms.Timer { Interval = RetrySeconds * 1000 };
            retryTimer.Tick += delegate { retryTimer.Stop(); RunScan(); };
            dailyTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            dailyTimer.Tick += delegate { if (!checking && !store.Days.ContainsKey(Fmt.DayKey(DateTime.Now))) StartCheck("Daily"); };
            dailyTimer.Start();

            taskExists = !test && Startup.Exists();
            SyncAuto();
            FixAutoPath();
            // right after "Turn on health details" the app restarts hidden; show the window once so you can see it worked
            try
            {
                string flag = Path.Combine(Persist.Dir, "show.flag");
                if (!test && File.Exists(flag)) { if ((DateTime.Now - File.GetLastWriteTime(flag)).TotalMinutes < 3) startHidden = false; File.Delete(flag); }
            }
            catch { }
            UpdateUi();
            IntPtr h = Handle;   // make sure we can be invoked into while hidden

            if (!test)
            {
                try
                {
                    // open to the normal and the admin copy alike (otherwise a copy started without admin rights could not reach this one)
                    EventWaitHandleSecurity es = new EventWaitHandleSecurity();
                    es.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
                    bool madeNew;
                    showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DriveWatch365.Show", out madeNew, es);
                    Thread t = new Thread(delegate ()
                    {
                        while (showSignal.WaitOne()) { try { BeginInvoke(new Action(ShowMain)); } catch { return; } }
                    });
                    t.IsBackground = true; t.Start();
                }
                catch { }
            }

            // First check shortly after the message loop starts (works whether or not the window is shown).
            System.Windows.Forms.Timer boot = new System.Windows.Forms.Timer { Interval = 400 };
            boot.Tick += delegate
            {
                boot.Stop();
                StartCheck(snapPath != null ? "Snap" : (startHidden ? "Startup" : "Launch"));
            };
            boot.Start();
        }

        // If a file called history-import.json sits next to the program, fold it into the current history
        // (nicknames, notes, day records, activity, problem records), then rename it so it only happens once.
        void ImportOld()
        {
            string f = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "history-import.json");
            if (test || !File.Exists(f)) return;
            try
            {
                Store old = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Store>(File.ReadAllText(f));
                int names = 0, days = 0;
                if (old != null)
                {
                    foreach (KeyValuePair<string, Drive> kv in old.Drives ?? new Dictionary<string, Drive>())
                    {
                        Drive cur;
                        // a drive this copy has not seen yet (a first run) is adopted as it is; the next scan updates it
                        if (!store.Drives.TryGetValue(kv.Key, out cur)) { store.Drives[kv.Key] = kv.Value; if (!string.IsNullOrEmpty(kv.Value.Nickname)) names++; continue; }
                        if (string.IsNullOrEmpty(cur.Nickname) && !string.IsNullOrEmpty(kv.Value.Nickname)) { cur.Nickname = kv.Value.Nickname; names++; }
                        if (string.IsNullOrEmpty(cur.Note) && !string.IsNullOrEmpty(kv.Value.Note)) cur.Note = kv.Value.Note;
                        if (string.CompareOrdinal(kv.Value.FirstSeen ?? "9", cur.FirstSeen ?? "9") < 0) cur.FirstSeen = kv.Value.FirstSeen;
                    }
                    foreach (KeyValuePair<string, Day> kv in old.Days ?? new Dictionary<string, Day>())
                        if (!store.Days.ContainsKey(kv.Key)) { store.Days[kv.Key] = kv.Value; days++; }
                    foreach (Incident i in old.Incidents ?? new List<Incident>())
                        if (!store.Incidents.Any(x => x.Key == i.Key && x.Start == i.Start && x.Type == i.Type)) store.Incidents.Add(i);
                    foreach (Ev e in old.Events ?? new List<Ev>())
                        if (!store.Events.Any(x => x.At == e.At && x.Text == e.Text)) store.Events.Add(e);
                    store.Events = store.Events.OrderByDescending(x => x.At).Take(120).ToList();
                    if (!string.IsNullOrEmpty(old.Started) && string.CompareOrdinal(old.Started, store.Started ?? "9") < 0) store.Started = old.Started;
                }
                Persist.Save(store);
                File.Move(f, f + ".done");
                Persist.Log("imported old history: " + names + " nicknames, " + days + " day records");
            }
            catch (Exception ex) { Persist.Log("import of old history failed: " + ex.Message); }
        }

        // ---- window / tray plumbing

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !allowShow) { if (!IsHandleCreated) CreateHandle(); base.SetVisibleCore(false); return; }
            base.SetVisibleCore(value);
        }

        void ShowMain()
        {
            allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false;
            Activate(); BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!store.TrayHintShown)
                {
                    store.TrayHintShown = true; Persist.Save(store);
                    tray.ShowBalloonTip(5000, "Drive Watch 365 is still running", "It lives in the system tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            exiting = true; tray.Visible = false; tray.Dispose();
            Application.Exit();
        }

        void LayoutHead()
        {
            btnCheck.Location = new Point(head.Width - btnCheck.Width - Theme.Sc(24), Theme.Sc(24));
            chkAuto.Location = new Point(btnCheck.Left - chkAuto.Width - Theme.Sc(20), Theme.Sc(32));
            btnHistory.Location = new Point(chkAuto.Left - btnHistory.Width - Theme.Sc(20), Theme.Sc(24));
        }

        // ---- start with Windows

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "DriveWatch365";
        static bool GetAuto()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }
        // Start with Windows has two forms: a plain login entry (no health data), or, once health details are
        // turned on, a Windows scheduled task that starts the app with admin rights and no prompt.
        void SetAuto(bool on)
        {
            if (test) return;
            try
            {
                if (on)
                {
                    string msg;
                    if (Program.IsAdmin)
                    {
                        if (Startup.Create(out msg)) { taskExists = true; Startup.RemoveRunKey(); }
                        else MessageBox.Show(this, "Couldn't set up the startup task: " + msg, "Drive Watch 365");
                    }
                    else using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --startup");
                }
                else
                {
                    Startup.RemoveRunKey();
                    if (taskExists)
                    {
                        string msg;
                        if (Program.IsAdmin) { if (Startup.Delete(out msg)) taskExists = false; }
                        else { Startup.RunElevated("--setup-admin off"); taskExists = Startup.Exists(); }
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) MessageBox.Show(this, "Couldn't change the startup setting: " + ex.Message, "Drive Watch 365");
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't change the startup setting: " + ex.Message, "Drive Watch 365"); }
        }

        void EnableAdmin()
        {
            if (test) { MessageBox.Show(this, "Not available in test mode.", "Drive Watch 365"); return; }
            string ask = "To read drive health (temperature, bad sectors, hours on...) Drive Watch 365 has to run with administrator rights.\n\n"
                + "Windows will ask your permission ONCE, now. After that Drive Watch 365 starts at every login with those rights and no more prompts. "
                + "(You can undo it any time by unticking Start with Windows.)\n\nContinue?";
            if (MessageBox.Show(this, ask, "Turn on health details", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int rc;
            try { rc = Startup.RunElevated("--setup-admin on"); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                MessageBox.Show(this, ex.NativeErrorCode == 1223 ? "Cancelled. Nothing was changed." : "Couldn't ask Windows for permission: " + ex.Message, "Drive Watch 365");
                return;
            }
            if (rc != 0 || !Startup.Exists()) { MessageBox.Show(this, "The startup task could not be created, so nothing was changed.", "Drive Watch 365"); return; }
            taskExists = true; SyncAuto();
            try { File.WriteAllText(Path.Combine(Persist.Dir, "show.flag"), "1"); } catch { }
            // this (limited) copy quits, and the same program is started again by the task, now with admin rights
            Startup.RunHidden("cmd.exe", "/c timeout /t 2 /nobreak >nul & schtasks /run /tn \"" + Startup.TaskName + "\"");
            ExitApp();
        }

        void ShowHealth(Drive d)
        {
            bool again;
            using (HealthForm hf = new HealthForm(store, d, formIcon)) { hf.ShowDialog(this); again = hf.TurnOnRequested; }
            if (again) EnableAdmin();
        }
        void FixAutoPath()   // if the exe was moved, repoint the startup entry
        {
            if (test || !GetAuto()) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string want = "\"" + Application.ExecutablePath + "\" --startup";
                    if ((k.GetValue(RunName) as string) != want) k.SetValue(RunName, want);
                }
            }
            catch { }
        }
        void SyncAuto() { bool a = GetAuto() || taskExists; chkAuto.Checked = a; miAuto.Checked = a; }

        // ---- checking

        void StartCheck(string k)
        {
            if (checking) return;
            checking = true; attempt = 1; kind = k;
            sawMissing.Clear(); lateLogged.Clear(); healthNew.Clear();
            btnCheck.Text = "Checking..."; btnCheck.Enabled = false;
            UpdateUi();
            RunScan();
        }

        void RunScan()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Drive> d = null; string err = null;
                Stopwatch sw = Stopwatch.StartNew();
                try { d = Program.Demo ? Demo.Live() : Scanner.Scan(); } catch (Exception ex) { err = ex.Message; Persist.Log("Scan failed: " + ex); }
                if (sw.ElapsedMilliseconds > 8000) Persist.Log("Scan was slow: " + sw.ElapsedMilliseconds + " ms");
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        try { ScanDone(d, err); }
                        catch (Exception ex)
                        {
                            Persist.Log("Check failed after the scan: " + ex);
                            checking = false; btnCheck.Text = "Check now"; btnCheck.Enabled = true;
                        }
                    }));
                }
                catch (Exception ex) { Persist.Log("Could not hand the scan to the window: " + ex.Message); }
            });
        }

        void AddEvent(string level, string text)
        {
            store.Events.Insert(0, new Ev { At = Fmt.Iso(DateTime.Now), Level = level, Text = text });
            DateTime cut = DateTime.Now.AddDays(-30);
            store.Events = store.Events.Where(x => Fmt.Parse(x.At) >= cut).Take(120).ToList();
        }

        void EndCheck()
        {
            checking = false;
            btnCheck.Text = "Check now"; btnCheck.Enabled = true;
            Persist.Save(store);
            UpdateUi();
            if (snapPath != null)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 700 };
                t.Tick += delegate { t.Stop(); Snap(); };
                t.Start();
            }
        }

        // ---- drive health: keep a daily reading and write down anything that got worse

        void AddHealth(Drive k, string note, bool bad, DateTime now)
        {
            Incident i = Log.Make("health", k, kind, now);
            i.End = i.Start; i.EndKind = bad ? "bad" : "info"; i.Note = note;
            store.Incidents.Add(i); healthNew.Add(i);
            AddEvent(bad ? "bad" : "warn", "Health: " + Fmt.Title(k) + " - " + note);
        }

        // A rise is always written down. It only becomes an ALERT when the count is climbing fast.
        void Rise(Drive k, string what, long before, long after, Func<Reading, long> sel, DateTime now)
        {
            if (before < 0 || after <= before) return;
            bool fast = Health.Climbing(k, sel);
            AddHealth(k, what + " went from " + before + " to " + after + "." + (fast ? " It is climbing quickly, and a climbing count is the pattern that matters." : ""), fast, now);
        }

        void ApplyHealth(Drive k, Smart cur, DateTime now)
        {
            if (cur == null || !cur.Ok) return;
            Smart prev = k.Smart;

            // today's reading goes into the daily trend first, so the rate rules can see it
            string today = Fmt.DayKey(now);
            k.Trend.RemoveAll(r => r.Date == today);
            k.Trend.Add(new Reading { Date = today, Temp = cur.Temp, Life = cur.Life, Poh = cur.PowerOnHours, Realloc = cur.Reallocated, Pending = cur.Pending, Uncorr = cur.Uncorrectable, Crc = cur.Crc });
            if (k.Trend.Count > 180) k.Trend.RemoveRange(0, k.Trend.Count - 180);

            if (cur.PredictFail && (prev == null || !prev.PredictFail))
                AddHealth(k, "Windows reports this drive is predicted to fail. Back up its data now.", true, now);
            if (prev != null)
            {
                Rise(k, "Sectors the drive had to remap (reallocated)", prev.Reallocated, cur.Reallocated, Health.RA, now);
                Rise(k, "Sectors waiting to be remapped (unreadable right now)", prev.Pending, cur.Pending, Health.PE, now);
                Rise(k, "Sectors that could not be corrected", prev.Uncorrectable, cur.Uncorrectable, Health.UN, now);
                Rise(k, "Cable/connection errors (often a loose or bad cable)", prev.Crc, cur.Crc, Health.CR, now);
            }
            else if (cur.Pending > 0)
                AddHealth(k, "Had " + cur.Pending + " sectors waiting to be remapped when first checked. These often clear by themselves; what matters is whether the number keeps growing.", false, now);
            int lim = Health.TempLimit(k);
            if (cur.Temp >= lim && (prev == null || prev.Temp < lim))
                AddHealth(k, "Reached " + cur.Temp + "°C (the warning level for this drive is " + lim + "°C).", false, now);
            if (cur.Life >= 0 && cur.Life <= 10 && (prev == null || prev.Life < 0 || prev.Life > 10))
                AddHealth(k, "SSD life left is down to " + cur.Life + "%.", true, now);
            k.Smart = cur;
        }

        void ScanDone(List<Drive> disks, string err)
        {
            // An empty list while we know of drives means Windows failed to answer, not that every drive died.
            if (err != null || disks == null || (disks.Count == 0 && store.Drives.Count > 0))
            {
                lastError = err ?? "Windows returned an empty drive list";
                status = Status.Error;
                AddEvent("warn", "Couldn't read the drive list (" + lastError + "). Nothing was recorded.");
                EndCheck();
                return;
            }

            DateTime now = DateTime.Now; string today = Fmt.DayKey(now);
            bool first = store.Drives.Count == 0;
            if (store.Started == null) store.Started = today;

            live.Clear();
            foreach (Drive d in disks)
            {
                string key = d.Key; int n = 2;
                while (live.ContainsKey(key)) key = d.Key + "#" + (n++);
                d.Key = key; live[key] = d;
            }
            List<Drive> added = new List<Drive>();
            foreach (Drive d in live.Values.ToList())
            {
                Drive k;
                if (!store.Drives.TryGetValue(d.Key, out k))
                {
                    d.FirstSeen = today; d.LastSeen = Fmt.Iso(now);
                    Smart firstRead = d.Smart; d.Smart = null;
                    store.Drives[d.Key] = d;
                    ApplyHealth(d, firstRead, now);
                    if (!first) added.Add(d);
                }
                else
                {
                    k.SmartWhy = d.SmartWhy;
                    ApplyHealth(k, d.Smart, now);
                    k.Model = d.Model; k.Serial = d.Serial; k.Size = d.Size; k.Letters = d.Letters;
                    k.Bus = d.Bus; k.Media = d.Media; k.Health = d.Health; k.LastSeen = Fmt.Iso(now);
                    live[d.Key] = k;
                }
            }
            List<Drive> missing = store.Drives.Values.Where(k => !live.ContainsKey(k.Key)).ToList();


            // A drive that was not there on an earlier look this run, but is now, was slow to appear: keep a record.
            foreach (string k in sawMissing.ToList())
            {
                Drive sd;
                if (!live.ContainsKey(k) || lateLogged.Contains(k) || !store.Drives.TryGetValue(k, out sd)) continue;
                lateLogged.Add(k);
                if (Log.Open(store, k) != null) continue;   // it was already on record as missing; that record closes below
                Incident li = Log.Make("late", sd, kind, now);
                li.End = li.Start; li.EndKind = "back";
                int secs = (attempt - 1) * RetrySeconds;
                li.Note = "Not there on the first look after startup; showed up on try " + attempt + " of " + MaxAttempts + " (about " + secs + " seconds later).";
                store.Incidents.Add(li);
                AddEvent("warn", "Slow to appear after startup: " + Fmt.Name(sd) + " (try " + attempt + " of " + MaxAttempts + ").");
            }
            foreach (Drive m in missing) sawMissing.Add(m.Key);

            // After a reboot the drives can take a minute to show up; look again before raising the alarm.
            if (missing.Count > 0 && kind == "Startup" && attempt < MaxAttempts)
            {
                attempt++; status = Status.Retrying;
                UpdateUi();
                retryTimer.Start();
                return;
            }

            Day day;
            if (!store.Days.TryGetValue(today, out day)) { day = new Day { Date = today }; store.Days[today] = day; }
            day.CheckedAt = Fmt.Iso(now); day.Kind = kind; day.Total = live.Count; day.Present = live.Keys.ToList();
            foreach (Drive m in missing) if (!day.Dipped.Contains(m.Key)) day.Dipped.Add(m.Key);
            string cutoff = Fmt.DayKey(now.AddDays(-40));
            foreach (string old in store.Days.Keys.Where(x => string.CompareOrdinal(x, cutoff) < 0).ToList()) store.Days.Remove(old);

            foreach (Drive a in added) AddEvent("info", "New drive spotted: " + Fmt.Name(a));
            // The permanent record: only CHANGES are written. A drive that stays gone is one open record, not one per day.
            List<Drive> newlyGone = new List<Drive>(), stillGone = new List<Drive>();
            foreach (Drive m in missing)
            {
                if (Log.Open(store, m.Key) == null) { store.Incidents.Add(Log.Make("missing", m, kind, now)); newlyGone.Add(m); }
                else stillGone.Add(m);
            }
            List<Incident> backAgain = new List<Incident>();
            foreach (Incident oi in store.Incidents.Where(i => i.Type == "missing" && i.End == null).ToList())
            {
                if (!live.ContainsKey(oi.Key)) continue;
                oi.End = Fmt.Iso(now); oi.EndKind = "back"; backAgain.Add(oi);
            }

            string label = Fmt.KindLabel(kind);
            if (kind != "Snap")
            {
                List<string> parts = new List<string>();
                if (newlyGone.Count > 0) parts.Add("Newly missing: " + string.Join(", ", newlyGone.Select(m => Fmt.Name(m)).ToArray()));
                if (stillGone.Count > 0) parts.Add("Still missing: " + string.Join(", ", stillGone.Select(m => Fmt.Title(m) + " (" + Fmt.Span(Fmt.Parse(Log.Open(store, m.Key).Start), now) + ")").ToArray()));
                if (backAgain.Count > 0) parts.Add("Back again: " + string.Join(", ", backAgain.Select(b => b.Name + " (was gone " + Fmt.Span(Fmt.Parse(b.Start), now) + ")").ToArray()));
                string lead = missing.Count > 0 ? live.Count + " of " + store.Drives.Count + " drives showing. " : "all " + live.Count + " drives showing. ";
                AddEvent(newlyGone.Count > 0 ? "bad" : (missing.Count > 0 ? "warn" : "ok"), label + ": " + lead + string.Join("  ", parts.ToArray()));
            }
            status = missing.Count > 0 ? Status.Missing : Status.Ok;
            scannedOnce = true;

            // Be loud only when something NEW goes missing. After that: red tray icon, and a quiet reminder at startup.
            Persist.Save(store);   // save BEFORE any alert, so a notification can never stop the record being kept
            if (!test && snapPath == null && kind != "Manual")
            {
                if (newlyGone.Count > 0)
                {
                    string names = string.Join("; ", newlyGone.Take(3).Select(m => Fmt.Title(m) + (string.IsNullOrEmpty(m.Letters) ? "" : " " + m.Letters)).ToArray());
                    string body = live.Count + " of " + store.Drives.Count + " drives are showing. Missing: " + names;
                    if (body.Length > 240) body = body.Substring(0, 240) + "...";
                    tray.ShowBalloonTip(20000, "Drive Watch 365: a drive is missing", body, ToolTipIcon.Warning);
                    if (kind == "Startup" || kind == "Launch") { SystemSounds.Exclamation.Play(); ShowMain(); }
                }
                else if (backAgain.Count > 0)
                {
                    tray.ShowBalloonTip(10000, "Drive Watch 365: a drive is back", string.Join("; ", backAgain.Take(3).Select(b => b.Name + " (gone " + Fmt.Span(Fmt.Parse(b.Start), now) + ")").ToArray()), ToolTipIcon.Info);
                }
                else if (healthNew.Count > 0)
                {
                    bool worst = healthNew.Any(h => h.EndKind == "bad");
                    string hb = string.Join("; ", healthNew.Take(3).Select(h => h.Name + ": " + h.Note).ToArray());
                    if (hb.Length > 240) hb = hb.Substring(0, 240) + "...";
                    tray.ShowBalloonTip(20000, "Drive Watch 365: drive health warning", hb, worst ? ToolTipIcon.Error : ToolTipIcon.Warning);
                    if (worst && (kind == "Startup" || kind == "Launch")) { SystemSounds.Exclamation.Play(); ShowMain(); }
                }
                else if (stillGone.Count > 0 && kind == "Startup")
                {
                    tray.ShowBalloonTip(8000, "Drive Watch 365: still missing", stillGone.Count + " drive(s) are still gone since before: " + string.Join("; ", stillGone.Take(3).Select(m => Fmt.Title(m)).ToArray()), ToolTipIcon.Warning);
                }
            }
            EndCheck();
        }

        void ShowAbout()
        {
            using (Form f = new Form())
            {
                f.Text = "About Drive Watch 365"; f.Icon = formIcon; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.CenterParent; f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; f.Font = Theme.F(13, FontStyle.Regular);
                f.ClientSize = new Size(Theme.Sc(520), Theme.Sc(330));
                int m = Theme.Sc(24), w = Theme.Sc(472);
                PictureBox pic = new PictureBox { Image = glyph, SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(m, Theme.Sc(22)) };
                Label t = new Label { Text = "Drive Watch 365", Font = Theme.F(24, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = true, Location = new Point(m + Theme.Sc(66), Theme.Sc(18)) };
                Label v = new Label { Text = "Version " + Program.Version, ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoSize = true, Location = new Point(m + Theme.Sc(68), Theme.Sc(56)) };
                Label body = new Label { ForeColor = Theme.Text, BackColor = Color.Transparent, Location = new Point(m, Theme.Sc(98)), Size = new Size(w, Theme.Sc(150)),
                    Text = "Remembers your drives: tells you when one disappears (especially after a reboot), keeps a permanent log of problems, and tracks drive health over time.\n\n"
                         + "Free and open source (MIT license). It never connects to the internet; everything stays on this PC in " + Persist.Dir + "." };
                LinkLabel link = new LinkLabel { Text = Program.HomePage, LinkColor = Theme.Accent, ActiveLinkColor = Color.White, BackColor = Color.Transparent, AutoSize = true, Location = new Point(m, Theme.Sc(252)) };
                link.LinkClicked += delegate { try { Process.Start(Program.HomePage); } catch { } };
                Button ok = new Button { Text = "Close", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Size = new Size(Theme.Sc(100), Theme.Sc(36)), Location = new Point(m + w - Theme.Sc(100), Theme.Sc(278)), UseVisualStyleBackColor = false };
                ok.FlatAppearance.BorderSize = 0;
                f.Controls.AddRange(new Control[] { pic, t, v, body, link, ok });
                f.AcceptButton = ok; f.CancelButton = ok;
                f.ShowDialog(this);
            }
        }

        void ShowHistory()
        {
            using (HistoryForm hf = new HistoryForm(store, formIcon)) hf.ShowDialog(this);
        }

        // ---- nicknames and notes

        void OnRename(Drive d)
        {
            string nick = d.Nickname ?? "", note = d.Note ?? "";
            if (!AskName(d, ref nick, ref note)) return;
            nick = nick.Trim(); note = note.Trim();
            d.Nickname = nick.Length == 0 ? null : nick;
            d.Note = note.Length == 0 ? null : note;
            AddEvent("info", d.Nickname == null ? "Cleared the nickname on " + d.Model + "." : "Named " + d.Model + " \"" + d.Nickname + "\".");
            Persist.Save(store);
            UpdateUi();
        }

        bool AskName(Drive d, ref string nick, ref string note)
        {
            using (Form f = new Form())
            {
                f.Text = "Name this drive";
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent; f.BackColor = Theme.Bg; f.ForeColor = Theme.Text;
                f.Font = Theme.F(13, FontStyle.Regular); f.Icon = formIcon;
                f.ClientSize = new Size(Theme.Sc(520), Theme.Sc(300));
                int m = Theme.Sc(20), w = Theme.Sc(480);
                Label info = new Label { Text = d.Model + "  ·  " + Fmt.Size(d.Size) + (string.IsNullOrEmpty(d.Letters) ? "" : "  ·  " + d.Letters) + "\n" + Fmt.ShortSerial(d.Serial),
                    ForeColor = Theme.Muted, BackColor = Color.Transparent, Location = new Point(m, Theme.Sc(16)), Size = new Size(w, Theme.Sc(44)) };
                Label l1 = new Label { Text = "Nickname (shown as the drive's name)", ForeColor = Theme.Text, BackColor = Color.Transparent, Location = new Point(m, Theme.Sc(72)), AutoSize = true };
                TextBox t1 = new TextBox { Text = nick, MaxLength = 60, Location = new Point(m, Theme.Sc(98)), Width = w, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                Label l2 = new Label { Text = "Longer note (where it sits, what's on it, anything)", ForeColor = Theme.Text, BackColor = Color.Transparent, Location = new Point(m, Theme.Sc(144)), AutoSize = true };
                TextBox t2 = new TextBox { Text = note, MaxLength = 200, Location = new Point(m, Theme.Sc(170)), Width = w, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                Button ok = new Button { Text = "Save", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Size = new Size(Theme.Sc(100), Theme.Sc(36)), Location = new Point(m + w - Theme.Sc(210), Theme.Sc(240)), UseVisualStyleBackColor = false };
                ok.FlatAppearance.BorderSize = 0;
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card, ForeColor = Theme.Text, Size = new Size(Theme.Sc(100), Theme.Sc(36)), Location = new Point(m + w - Theme.Sc(100), Theme.Sc(240)), UseVisualStyleBackColor = false };
                cancel.FlatAppearance.BorderColor = Theme.Line;
                f.Controls.AddRange(new Control[] { info, l1, t1, l2, t2, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += delegate { t1.Focus(); t1.SelectAll(); };
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                nick = t1.Text; note = t2.Text;
                return true;
            }
        }

        // ---- "I removed it"

        void OnForget(Drive d)
        {
            string msg = "Tell Drive Watch 365 you took this drive out on purpose?\n\n" + Fmt.Name(d)
                + "\n\nIt will stop being counted as missing. If you plug it back in later, it will show up as a new drive.";
            if (MessageBox.Show(this, msg, "I removed this drive", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            // The record of it going missing stays (with its name), now marked as removed on purpose.
            Incident oi = Log.Open(store, d.Key);
            if (oi != null) { oi.End = Fmt.Iso(DateTime.Now); oi.EndKind = "removed"; }
            store.Drives.Remove(d.Key);
            AddEvent("info", "You removed " + Fmt.Name(d) + ". It is no longer expected.");
            List<Drive> missing = store.Drives.Values.Where(k => !live.ContainsKey(k.Key)).ToList();
            if (!checking) status = !scannedOnce ? Status.Unknown : (missing.Count > 0 ? Status.Missing : Status.Ok);
            Persist.Save(store);
            UpdateUi();
        }

        // ---- showing the state

        string GoneText(Drive m)
        {
            Incident oi = Log.Open(store, m.Key);
            DateTime since = oi != null ? Fmt.Parse(oi.Start) : Fmt.Parse(m.LastSeen);
            return "missing since " + Fmt.DateOnly(since) + " (" + Fmt.Span(since, DateTime.Now) + ")";
        }

        void UpdateUi()
        {
            grid.Bind(store, live, scannedOnce);
            events.Bind(store.Events);
            List<Drive> missing = store.Drives.Values.Where(k => !live.ContainsKey(k.Key)).ToList();
            DateTime last = DateTime.MinValue; string lastKind = "";
            foreach (Day d in store.Days.Values) { DateTime c = Fmt.Parse(d.CheckedAt); if (c > last) { last = c; lastKind = d.Kind; } }
            string lastText = last == DateTime.MinValue ? "Not checked yet" : "Last check: " + Fmt.When(last) + "  \u00B7  " + Fmt.KindLabel(lastKind).ToLowerInvariant();
            lblSub.Text = checking ? "Checking now..." : lastText;

            Color tray = Theme.Ok; string tip;
            switch (status)
            {
                case Status.Retrying:
                    banner.Set(Look.Warn, "A drive isn't showing yet",
                        "Windows may still be starting up. Looking again in " + RetrySeconds + " seconds (try " + attempt + " of " + MaxAttempts + ").");
                    tray = Theme.Warn; tip = "Drive Watch 365: checking again..."; break;
                case Status.Missing:
                    {
                        string names = string.Join("\n", missing.Take(2).Select(m => Fmt.Title(m) + (string.IsNullOrEmpty(m.Letters) ? "" : " (" + m.Letters + ")") + " - " + GoneText(m)).ToArray());
                        if (missing.Count > 2) names += "\n+ " + (missing.Count - 2) + " more";
                        banner.Set(Look.Bad, missing.Count == 1 ? "1 drive is missing" : missing.Count + " drives are missing",
                            live.Count + " of " + store.Drives.Count + " known drives are showing.\n" + names);
                        tray = Theme.Bad; tip = "Drive Watch 365: " + missing.Count + " MISSING (" + live.Count + " of " + store.Drives.Count + ")"; break;
                    }
                case Status.Error:
                    banner.Set(Look.Warn, "Couldn't read the drive list", lastError + "\nNothing was recorded. Try Check now.");
                    tray = Theme.Warn; tip = "Drive Watch 365: couldn't read drives"; break;
                case Status.Ok:
                    banner.Set(Look.Ok, "All " + live.Count + " drives are showing", "Nothing is missing.  " + lastText + ".");
                    tip = "Drive Watch 365: all " + live.Count + " drives OK"; break;
                default:
                    banner.Set(Look.Working, "Checking your drives...", "One moment.");
                    tray = Theme.Accent; tip = "Drive Watch 365"; break;
            }
            SetTrayLook(tray);
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            this.tray.Text = tip;
        }

        Icon IconFor(Color dot)
        {
            using (Bitmap b = Icons.Draw(32, dot))
            {
                IntPtr h = b.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                tmp.Dispose(); DestroyIcon(h);
                return copy;
            }
        }

        void SetTrayLook(Color c)
        {
            Icon old = trayIcon;
            trayIcon = IconFor(c);
            tray.Icon = trayIcon;
            if (old != null) old.Dispose();
        }

        void Snap()
        {
            try
            {
                Form target = this, other = null;
                string outPath = snapPath;
                if (Program.HistorySnap != null) { other = new HistoryForm(store, formIcon); outPath = Program.HistorySnap; }
                else if (Program.HealthSnap != null)
                {
                    Drive pick = null;
                    if (!string.IsNullOrEmpty(Program.SnapDrive)) pick = store.Drives.Values.FirstOrDefault(x => (x.Letters ?? "").Contains(Program.SnapDrive));
                    if (pick == null) pick = store.Drives.Values.OrderBy(x => string.IsNullOrEmpty(x.Letters) ? "~" : x.Letters).FirstOrDefault(x => x.Smart != null && x.Smart.Ok) ?? store.Drives.Values.First();
                    other = new HealthForm(store, pick, formIcon); outPath = Program.HealthSnap;
                }
                if (other != null) { other.Show(); Application.DoEvents(); target = other; }
                using (Bitmap b = new Bitmap(target.Width, target.Height))
                {
                    target.DrawToBitmap(b, new Rectangle(0, 0, target.Width, target.Height));
                    b.Save(outPath, ImageFormat.Png);
                }
                if (other != null) other.Dispose();
            }
            catch { }
            exiting = true; tray.Visible = false; Application.Exit();
        }
    }

    static class Program
    {
        public const string Version = "1.0.0";
        public const string HomePage = "https://github.com/imkevinc/drive-watch-365";
        public static bool Demo;                        // --demo: made-up drives, nothing is saved
        public static string HistorySnap, HealthSnap, SnapDrive;   // --snap-history / --snap-health / --snap-drive: for making screenshots
        public static readonly bool IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Persist.Log("Unhandled: " + e.Exception); };
            string snap = null, scanOut = null, icoOut = null, setup = null, healthOut = null; bool hidden = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--startup") hidden = true;
                else if (args[i] == "--snap" && i + 1 < args.Length) snap = args[++i];
                else if (args[i] == "--snap-history" && i + 1 < args.Length) HistorySnap = args[++i];
                else if (args[i] == "--snap-health" && i + 1 < args.Length) HealthSnap = args[++i];
                else if (args[i] == "--snap-drive" && i + 1 < args.Length) SnapDrive = args[++i];
                else if (args[i] == "--demo") Demo = true;
                else if (args[i] == "--setup-admin" && i + 1 < args.Length) setup = args[++i];
                else if (args[i] == "--health" && i + 1 < args.Length) healthOut = args[++i];
                else if (args[i] == "--scan" && i + 1 < args.Length) scanOut = args[++i];
                else if (args[i] == "--make-icon" && i + 1 < args.Length) icoOut = args[++i];
            }
            if (icoOut != null) { Icons.WriteIco(icoOut); return 0; }
            if (setup != null) return Startup.Setup(setup == "on");
            if (healthOut != null)
            {
                // diagnostic: what health data can this program read right now?
                List<string> lines = new List<string>();
                lines.Add("admin=" + IsAdmin);
                foreach (Drive d in Scanner.Scan().OrderBy(x => x.Letters))
                {
                    Smart s = d.Smart;
                    lines.Add("== " + d.Model + " | " + d.Letters + " | " + d.Bus + " " + d.Media + " | " + (s != null ? "source=" + s.Source : "NO DATA: " + d.SmartWhy));
                    if (s != null)
                    {
                        lines.Add("   temp=" + s.Temp + " poh=" + s.PowerOnHours + " cycles=" + s.PowerCycles + " realloc=" + s.Reallocated + " pending=" + s.Pending
                            + " uncorr=" + s.Uncorrectable + " crc=" + s.Crc + " life=" + s.Life + " predictFail=" + s.PredictFail + " level=" + Health.Level(d));
                        foreach (Attr a in s.Attrs) lines.Add("   " + a.Id.ToString("X2") + " " + a.Name + " cur=" + a.Value + " worst=" + a.Worst + " thr=" + a.Thresh + " raw=" + a.Raw);
                    }
                }
                File.WriteAllLines(healthOut, lines.ToArray());
                return 0;
            }
            if (scanOut != null)
            {
                List<string> lines = new List<string>();
                foreach (Drive d in Scanner.Scan().OrderBy(x => x.Letters))
                    lines.Add(string.Join(" | ", new string[] { d.Model, Fmt.Size(d.Size), d.Letters, d.Bus, d.Media, d.Health, d.Serial, d.Key }));
                File.WriteAllLines(scanOut, lines.ToArray());
                return 0;
            }
            // "test" = a separate throwaway run: no tray icon, no alerts, no single-instance lock.
            // DRIVEWATCH_DATA also lets you keep the history somewhere other than %APPDATA%\DriveWatch365.
            bool test = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRIVEWATCH_DATA")) || Demo;
            Mutex m = null;
            if (!test)
            {
                bool created;
                // open to the normal and the admin copy alike, so either can find the other
                MutexSecurity ms = new MutexSecurity();
                ms.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
                try { m = new Mutex(true, @"Local\DriveWatch365.Single", out created, ms); }
                catch (UnauthorizedAccessException) { created = false; }
                if (!created)
                {
                    try { EventWaitHandle.OpenExisting(@"Local\DriveWatch365.Show", EventWaitHandleRights.Modify).Set(); } catch { }
                    return 0;
                }
            }
            Application.Run(new MainForm(hidden && snap == null, snap, test));
            if (m != null) GC.KeepAlive(m);
            return 0;
        }
    }
}
