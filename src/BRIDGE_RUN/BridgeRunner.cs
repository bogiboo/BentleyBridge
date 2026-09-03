/*----------------------------------------------------------------------------------------+
| BridgeRunner - jezgra BRIDGE_RUN AddIna (prva verzija).                                  |
|                                                                                        |
| Prva verzija NE cita, NE mijenja i NE sprema aktivni DGN. Smije samo:                    |
|   - procitati i provjeriti JSON nalog iz runtime\inbox\current-task.json                 |
|   - za operaciju SHOW_MESSAGE prikazati poruku                                           |
|   - zapisati rezultat u runtime\results i log u runtime\logs                             |
|   - idempotentno preskociti vec dovrseni taskId                                          |
|                                                                                        |
| Poruka: Bentley-native MessageCenter (obrazac iz SDK ManagedToolsExample)               |
|   Bentley.MstnPlatformNET.MessageCenter.Instance.ShowInfoMessage(brief, detail, false)  |
+----------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace BridgeRun
{
    internal static class BridgeRunner
    {
        private const string RuntimeRoot = @"C:\AITools\BentleyBridge\runtime";
        private static string InboxTask { get { return Path.Combine(RuntimeRoot, @"inbox\current-task.json"); } }
        private static string ResultsDir { get { return Path.Combine(RuntimeRoot, "results"); } }
        private static string LogsDir { get { return Path.Combine(RuntimeRoot, "logs"); } }
        private static string StateDir { get { return Path.Combine(RuntimeRoot, "state"); } }
        private static string CompletedLog { get { return Path.Combine(StateDir, "completed.log"); } }

        private static readonly string[] SupportedOperations = { "SHOW_MESSAGE", "READ_INTERFACE_STATE", "APPLY_INTERFACE_BASELINE" };

        // Read-only operacije se smiju ponavljati: ne troše idempotentni no-op i
        // svaki poziv daje svjež snimak stanja. NE smiju pregaziti puni rezultat stub-om.
        private static readonly string[] ReadOnlyOperations = { "READ_INTERFACE_STATE" };

        private const int SupportedSchemaVersion = 1;
        private const string RunnerVersion = "0.3.1-ui-baseline";

        internal static void Execute()
        {
            EnsureDirs();
            string taskId = "(nepoznat)";
            try
            {
                if (!File.Exists(InboxTask))
                {
                    Report(taskId, "NO_TASK", "Nema naloga: " + InboxTask);
                    return;
                }

                string raw = File.ReadAllText(InboxTask, Encoding.UTF8);
                Dictionary<string, object> task = Parse(raw);
                if (task == null)
                {
                    Report(taskId, "INVALID_JSON", "Nalog nije valjan JSON objekt.");
                    return;
                }

                // --- validacija prema schemas\task.schema.json ---
                int schemaVersion = GetInt(task, "schemaVersion", -1);
                if (schemaVersion != SupportedSchemaVersion)
                {
                    Report(taskId, "BAD_SCHEMA", "schemaVersion=" + schemaVersion + " (podrzana: " + SupportedSchemaVersion + ")");
                    return;
                }

                taskId = GetString(task, "taskId", null);
                if (string.IsNullOrEmpty(taskId))
                {
                    taskId = "(prazan)";
                    Report(taskId, "BAD_TASKID", "taskId nedostaje ili je prazan.");
                    return;
                }

                string operation = GetString(task, "operation", null);
                if (Array.IndexOf(SupportedOperations, operation) < 0)
                {
                    Report(taskId, "UNSUPPORTED_OP", "operation='" + operation + "' nije podrzana. Podrzano: " + string.Join(",", SupportedOperations));
                    return;
                }

                bool isReadOnlyOp = Array.IndexOf(ReadOnlyOperations, operation) >= 0;

                // --- idempotentnost (samo za operacije koje mijenjaju stanje) ---
                if (!isReadOnlyOp && IsCompleted(taskId))
                {
                    ShowMessage("BRIDGE_RUN: zadatak '" + taskId + "' je vec izvrsen (no-op).");
                    Report(taskId, "ALREADY_DONE", "Ponovno pokretanje istog dovrsenog taskId-a. Nista nije napravljeno.");
                    return;
                }

                // --- operacija ---
                if (operation == "SHOW_MESSAGE")
                {
                    Dictionary<string, object> p = GetObject(task, "parameters");
                    string message = p != null ? GetString(p, "message", null) : null;
                    if (string.IsNullOrEmpty(message))
                    {
                        Report(taskId, "BAD_PARAMS", "SHOW_MESSAGE trazi parameters.message.");
                        return;
                    }
                    ShowMessage(message);
                    MarkCompleted(taskId);
                    Report(taskId, "OK", "SHOW_MESSAGE prikazan: " + message);
                    return;
                }

                if (operation == "READ_INTERFACE_STATE")
                {
                    // Strogo read-only: cita trenutno stanje OpenCities sucelja i pogleda.
                    // NE dira DGN/seed/model/View postavke/preferencije, NE radi Save Settings.
                    Dictionary<string, object> p = GetObject(task, "parameters");
                    string summary;
                    Dictionary<string, object> inventory = InterfaceStateReader.Build(p, out summary);

                    // Namjerno BEZ MarkCompleted: read-only op se smije ponovno pokrenuti
                    // (svjež snimak) i nikad ne pregazi puni rezultat ALREADY_DONE stub-om.
                    var extra = new Dictionary<string, object> { { "inventory", inventory } };
                    ShowMessage("BRIDGE_RUN: inventura sucelja zavrsena (read-only).\n" + summary +
                                "\nRezultat: " + Path.Combine(ResultsDir, Sanitize(taskId) + ".json"));
                    ReportResult(taskId, "OK", "READ_INTERFACE_STATE inventura zavrsena. " + summary, extra);
                    return;
                }

                if (operation == "APPLY_INTERFACE_BASELINE")
                {
                    // Mutirajuca: mijenja SAMO View/UI postavke aktivnog Test_2.dgn (BEN-UI-002).
                    // Idempotentnost po taskId je vec provjerena gore (nije read-only op).
                    string applySummary;
                    string applyStatus;
                    Dictionary<string, object> extra =
                        InterfaceBaselineApplier.Apply(task, taskId, out applySummary, out applyStatus);

                    if (applyStatus == "OK")
                        MarkCompleted(taskId);

                    string prefix =
                        applyStatus == "OK"      ? "BRIDGE_RUN: osnovni profil sucelja primijenjen." :
                        applyStatus == "ABORTED" ? "BRIDGE_RUN: APPLY_INTERFACE_BASELINE PREKINUT (bez izmjena)." :
                        applyStatus == "PARTIAL" ? "BRIDGE_RUN: APPLY_INTERFACE_BASELINE DJELOMICNO - vidi rezultat." :
                                                   "BRIDGE_RUN: APPLY_INTERFACE_BASELINE GRESKA - vidi rezultat.";
                    ShowMessage(prefix + "\n" + applySummary +
                                "\nRezultat: " + Path.Combine(ResultsDir, Sanitize(taskId) + ".json"));
                    ReportResult(taskId, applyStatus, "APPLY_INTERFACE_BASELINE: " + applySummary, extra);
                    return;
                }

                Report(taskId, "UNSUPPORTED_OP", "Neocekivana operacija: " + operation);
            }
            catch (Exception ex)
            {
                try { ShowMessage("BRIDGE_RUN GRESKA: " + ex.Message); } catch { }
                Report(taskId, "ERROR", ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------- poruke ----------------

        private static void ShowMessage(string text)
        {
            // Bentley-native, nemodalno (Message Center + statusna traka)
            try
            {
                Bentley.MstnPlatformNET.MessageCenter.Instance.ShowInfoMessage(text, "", false);
            }
            catch { }
            // Nedvosmisleno vidljivo za kontrolirani test
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    text, "BRIDGE_RUN v" + RunnerVersion,
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch { }
        }

        // ---------------- rezultat + log ----------------

        private static void Report(string taskId, string status, string detail)
        {
            ReportResult(taskId, status, detail, null);
        }

        /// <summary>
        /// Zapisuje rezultat u runtime\results\&lt;taskId&gt;.json. Ako je <paramref name="extra"/> zadan,
        /// njegovi kljucevi se spajaju u rezultat (npr. "inventory" za READ_INTERFACE_STATE).
        /// </summary>
        private static void ReportResult(string taskId, string status, string detail, Dictionary<string, object> extra)
        {
            string ts = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            Log(ts + "  [" + status + "]  task=" + taskId + "  " + detail);

            try
            {
                var result = new Dictionary<string, object>
                {
                    { "taskId", taskId },
                    { "status", status },
                    { "detail", detail },
                    { "runnerVersion", RunnerVersion },
                    { "timestamp", ts },
                    { "touchedDgn", false }
                };
                if (extra != null)
                    foreach (var kv in extra)
                        result[kv.Key] = kv.Value;

                var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                string json = serializer.Serialize(result);
                string file = Path.Combine(ResultsDir, Sanitize(taskId) + ".json");
                File.WriteAllText(file, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log(ts + "  [WARN]  zapis rezultata nije uspio: " + ex.Message);
            }
        }

        private static void Log(string line)
        {
            try
            {
                string file = Path.Combine(LogsDir, "bridge_run_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------- idempotentnost ----------------

        private static bool IsCompleted(string taskId)
        {
            try
            {
                if (!File.Exists(CompletedLog)) return false;
                foreach (string l in File.ReadAllLines(CompletedLog))
                    if (l.Trim() == taskId) return true;
            }
            catch { }
            return false;
        }

        private static void MarkCompleted(string taskId)
        {
            try { File.AppendAllText(CompletedLog, taskId + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        // ---------------- pomocno ----------------

        private static void EnsureDirs()
        {
            foreach (string d in new[] { ResultsDir, LogsDir, StateDir })
                try { Directory.CreateDirectory(d); } catch { }
        }

        private static Dictionary<string, object> Parse(string raw)
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw); }
            catch { return null; }
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        private static string GetString(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                int r;
                if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            }
            return fallback;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append((char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
            return sb.Length == 0 ? "task" : sb.ToString();
        }
    }
}
