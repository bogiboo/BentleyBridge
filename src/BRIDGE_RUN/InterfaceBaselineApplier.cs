/*----------------------------------------------------------------------------------------+
| InterfaceBaselineApplier - operacija APPLY_INTERFACE_BASELINE za BRIDGE_RUN (BEN-UI-002). |
|                                                                                        |
| MUTIRAJUCA operacija. Mijenja SAMO View/UI postavke aktivnog Test_2.dgn:                 |
|   1. UI View 1 -> standardna rotacija Top                                                |
|   2. grid OFF u View 1-8                                                                 |
|   3. radna pozadina bijela RGB 255,255,255 u View 1-8 (+ OverrideBackground ON)          |
|   4. otvoren samo UI View 1                                                              |
|   5. zatvoreni UI View 2-8                                                               |
|   6. View 1 aktivan (best-effort; vidi napomenu nize)                                    |
|   7. Save Settings da stanje prezivi ponovno otvaranje datoteke                          |
|   8. interna read-only provjera prije/poslije + SHA-256 prije/poslije                    |
|                                                                                        |
| NE dira geometriju, modele, razine, reference, rastere, elemente, georeferenciranje.    |
| NE stvara/ne mijenja Display Styleove. NE dira CFG/INC/UCF/DGNWS/Registry/WorkSpace.     |
| Bez COM-a, MVBA, OpenDesignFile. Bez automatskog rollbacka.                              |
|                                                                                        |
| Nacelo R5 (DECISIONS.md): sve sto nije potvrdjeno lokalnim ocitanjem oznaceno je         |
| stringom "NEPOZNATO"/"PRETPOSTAVKA". Sto se ne moze potvrdjeno napraviti - ne radi se.   |
|                                                                                        |
| Lokalno potvrdjeni izvori (reflektirano iz stvarnih DLL-ova + SDK primjeri):            |
|  - Bentley.MstnPlatformNET.Session.Instance.GetActiveDgnFile()  (ustation.dll)          |
|  - Bentley.MstnPlatformNET.Session.Keyin(string)                (ustation.dll)          |
|  - Bentley.DgnPlatformNET.ConfigurationManager.GetVariable(string)                      |
|  - DgnFile.GetViewGroups() -> ViewGroupCollection.GetActive() -> ViewGroup              |
|    (SDK C++ ekvivalent: examples\View\ViewInfoExample\ViewInfoExample.cpp:125-131)       |
|  - ViewGroup.GetViewInformation(int) / SetViewInformation(ViewInformation,int) /         |
|    SynchViewDisplay(int,bool,bool,bool)  (SDK C++: ViewInfoExample.cpp:292-293)          |
|  - ViewInformation.GetStandardViewByName(out RotMatrix,out StandardView,"Top")           |
|    + ViewInformation.GetGeometryInformation()/SetGeometryInformation(...)                |
|    + ViewGeometryInformation.ViewFlags {get;set;} (Grid, OverrideBackground)             |
|    + ViewInformation.SetBackgroundColor(RgbColorDef)  (SDK C++: ViewInfoExample.cpp:263-292)|
|  - ViewGroupCollection.SaveChanges() + DgnFile.ProcessChanges(DgnSaveReason.SaveSettings)|
|    (DgnPlatform\ViewGroup.h:37-40 - "not written back ... unless ViewGroup::SaveChanges")|
|  - VIEW ON <n> / VIEW OFF <n>: SDK Mstn\cmdlist.r.h  CMD_VIEW_ON_1..8 / CMD_VIEW_OFF_1..8 |
|    (kategorija VIEWIMMEDIATE). Doslovni oblik key-ina je kanonski MicroStation oblik,     |
|    ali nije verbatim u lokalnoj datoteci -> u rezultatu oznaceno PRETPOSTAVKA.           |
|  - "View aktivan": nema potvrdjenog namjenskog API-ja/key-ina. Kad je otvoren samo       |
|    View 1, MicroStation ga cini tekucim po eliminaciji -> oznaceno PRETPOSTAVKA;         |
|    stvarni ishod potvrdjuje interna provjera 'after' + Bozidarov kontrolirani test.      |
+----------------------------------------------------------------------------------------*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BridgeRun
{
    internal static class InterfaceBaselineApplier
    {
        private const string SessionTypeName = "Bentley.MstnPlatformNET.Session";
        private const string ConfigMgrTypeName = "Bentley.DgnPlatformNET.ConfigurationManager";
        private const string ViewInfoTypeName = "Bentley.DgnPlatformNET.ViewInformation";
        private const string RgbColorDefTypeName = "Bentley.DgnPlatformNET.RgbColorDef";
        private const string DgnSaveReasonTypeName = "Bentley.DgnPlatformNET.DgnSaveReason";

        private const string BackupRoot = @"C:\AITools\BentleyBridge\runtime\backups\BEN-UI-002";

        // Zadani ciljni kontekst (koristi se ako task.target ne navede vrijednost).
        private const string DefWorkspace = "ETAZIRANJE_RAZVOJ";
        private const string DefWorkset = "TEST_OSNOVA";
        private const string DefFileName = "Test_2.dgn";
        private const string DefForbidFileName = "ETAZIRANJE_3D_METRIC.dgn";

        /// <summary>
        /// Primjenjuje osnovni profil sucelja. NE oznacava taskId dovrsenim - to radi
        /// BridgeRunner samo ako je <paramref name="status"/> == "OK".
        /// </summary>
        /// <param name="task">Cijeli JSON nalog (treba i 'target' i 'parameters').</param>
        /// <param name="taskId">Za imenovanje backupa i log zapisa.</param>
        /// <param name="summary">Kratki sazetak za MessageCenter/log.</param>
        /// <param name="status">"OK" | "ABORTED" | "PARTIAL" | "ERROR".</param>
        /// <returns>Dodatni kljucevi za rezultat JSON (backup, SHA, koraci, provjera).</returns>
        internal static Dictionary<string, object> Apply(
            Dictionary<string, object> task, string taskId, out string summary, out string status)
        {
            var extra = new Dictionary<string, object>();
            var steps = new List<object>();
            var perView = new List<object>();
            extra["operation"] = "APPLY_INTERFACE_BASELINE";
            extra["touchedDgn"] = false;
            extra["savedSettings"] = false;
            extra["rule"] = "R5 - bez nagadanja; nepotvrdeno = NEPOZNATO/PRETPOSTAVKA; bez auto-rollbacka";
            extra["steps"] = steps;
            extra["perView"] = perView;
            extra["generatedAt"] = Now();

            Dictionary<string, object> target = GetObject(task, "target");
            Dictionary<string, object> p = GetObject(task, "parameters");

            string wantWorkspace = GetString(target, "workspace", DefWorkspace);
            string wantWorkset = GetString(target, "workset", DefWorkset);
            string wantFileName = GetString(target, "fileName", DefFileName);
            string forbidFileName = GetString(target, "forbidFileName", DefForbidFileName);

            int[] views = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            int[] openViews = ParseIntArray(p, "openViews", new[] { 1 });
            int[] closeViews = ParseIntArray(p, "closeViews", new[] { 2, 3, 4, 5, 6, 7, 8 });
            bool gridEnabled = ParseBool(p, "gridEnabled", false);
            byte[] bg = ParseRgb(p, "backgroundRgb");
            bool saveSettings = ParseBool(p, "saveSettings", true);
            int activeView = ParseInt(p, "activeView", 1);
            string view1Orientation = GetString(p, "view1Orientation", "Top");

            extra["requested"] = new Dictionary<string, object>
            {
                { "target", new Dictionary<string, object> {
                    { "workspace", wantWorkspace }, { "workset", wantWorkset },
                    { "fileName", wantFileName }, { "forbidFileName", forbidFileName } } },
                { "openViews", Box(openViews) }, { "closeViews", Box(closeViews) },
                { "gridEnabled", gridEnabled },
                { "backgroundRgb", new List<object> { (int)bg[0], (int)bg[1], (int)bg[2] } },
                { "activeView", activeView }, { "view1Orientation", view1Orientation },
                { "saveSettings", saveSettings }
            };

            object session = GetSessionInstance();
            if (session == null)
            {
                status = "ABORTED";
                steps.Add(Step("guardrails", "ABORTED", "NEPOZNATO: Session.Instance je null - nema aktivne OpenCities sesije?"));
                summary = "PREKINUTO: nema Session.Instance.";
                return extra;
            }

            // ---------- 1) GUARDRAILS (prije bilo kakve izmjene) ----------
            bool okGuard;
            string whyGuard;
            object dgnFile;
            string activePath;
            Dictionary<string, object> guard = CheckGuardrails(
                session, wantWorkspace, wantWorkset, wantFileName, forbidFileName,
                out okGuard, out whyGuard, out dgnFile, out activePath);
            extra["guardrails"] = guard;

            if (!okGuard)
            {
                status = "ABORTED";
                steps.Add(Step("guardrails", "ABORTED", whyGuard));
                summary = "PREKINUTO (bez izmjena): " + whyGuard;
                return extra;
            }
            steps.Add(Step("guardrails", "OK",
                "WorkSpace=" + wantWorkspace + " WorkSet=" + wantWorkset + " datoteka=" + activePath));

            // ---------- 2) BACKUP + SHA-256 prije ----------
            string backupPath = null, shaBefore = null;
            try
            {
                Directory.CreateDirectory(BackupRoot);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string baseName = Path.GetFileNameWithoutExtension(activePath);
                string ext = Path.GetExtension(activePath);
                backupPath = Path.Combine(BackupRoot, baseName + "_" + stamp + "_" + Sanitize(taskId) + ext);
                File.Copy(activePath, backupPath, false);
                shaBefore = Sha256OfFile(backupPath);
                extra["backupPath"] = backupPath;
                extra["sha256Before"] = shaBefore;
                extra["sha256BeforeSource"] = "SHA-256 vremenski oznacene kopije aktivnog DGN-a (stanje na disku prije izmjene)";
                steps.Add(Step("backup", "OK", backupPath + "  sha256=" + shaBefore));
            }
            catch (Exception ex)
            {
                status = "ABORTED";
                extra["backupPath"] = backupPath;
                steps.Add(Step("backup", "ABORTED", "NEPOZNATO: backup nije uspio: " + ExMsg(ex)));
                summary = "PREKINUTO (bez izmjena): backup nije uspio: " + ExMsg(ex);
                return extra;
            }

            // ---------- 3) interna provjera PRIJE ----------
            try
            {
                string s;
                var before = InterfaceStateReader.Build(VerifyParams(views), out s);
                extra["verifyBefore"] = Compact(before);
                steps.Add(Step("verify.before", "OK", s));
            }
            catch (Exception ex)
            {
                extra["verifyBefore"] = "NEPOZNATO: " + ExMsg(ex);
                steps.Add(Step("verify.before", "WARN", "citanje 'before' nije uspjelo: " + ExMsg(ex)));
            }

            // ---------- 4) VIEW ON/OFF preko key-ina (prvo - da samo ciljni view ostane ziv) ----------
            // Redoslijed: prvo zatvori/otvori poglede (djeluje na zive prozore), pa tek onda
            // managed izmjene ViewInfo + SynchViewDisplay na preostali otvoreni view. Tako se
            // zivi viewport i pohranjeni ViewInfo ne razilaze prije Save Settings.
            var keyinResults = new List<object>();
            foreach (int v in closeViews) DoKeyin(session, "VIEW OFF " + v, keyinResults);
            foreach (int v in openViews) DoKeyin(session, "VIEW ON " + v, keyinResults);
            extra["keyins"] = keyinResults;
            steps.Add(Step("views.keyin", "PRETPOSTAVKA",
                "Session.Keyin: VIEW OFF " + string.Join(",", IntStrs(closeViews)) +
                " ; VIEW ON " + string.Join(",", IntStrs(openViews)) +
                " (tokeni CMD_VIEW_ON_n/CMD_VIEW_OFF_n iz SDK Mstn\\cmdlist.r.h; doslovni key-in je kanonski oblik)"));

            // ---------- 5) MANAGED izmjene View postavki ----------
            int errCount = 0;
            int changedViews = 0;
            bool anyManagedChange = false;
            object viewGroup = null, viewGroupColl = null;
            object topRotation = null;
            string topRotNote = null;

            try
            {
                viewGroupColl = Inv(dgnFile, "GetViewGroups");
                viewGroup = Inv(viewGroupColl, "GetActive");
                if (viewGroup == null)
                    throw new Exception("aktivni ViewGroup je null (DgnFile.GetViewGroups().GetActive())");

                Type viType = FindType(ViewInfoTypeName);
                Type rgbType = FindType(RgbColorDefTypeName);
                object white = MakeRgb(rgbType, bg);

                foreach (int viewNo in views)
                {
                    int idx = viewNo - 1;
                    var pv = new Dictionary<string, object> { { "view", viewNo }, { "zeroBasedIndex", idx } };
                    try
                    {
                        object vi = Inv(viewGroup, "GetViewInformation", idx);
                        if (vi == null) { pv["result"] = "NEPOZNATO: GetViewInformation(" + idx + ") je null"; errCount++; perView.Add(pv); continue; }

                        // 5a) grid OFF + OverrideBackground ON preko ViewGeometryInformation.ViewFlags (settable)
                        object gi = Inv(vi, "GetGeometryInformation");
                        object vf = Prop(gi, "ViewFlags");
                        bool gridBefore = ToBool(Prop(vf, "Grid"));
                        bool ovrBefore = ToBool(Prop(vf, "OverrideBackground"));
                        SetProp(vf, "Grid", gridEnabled);            // false
                        SetProp(vf, "OverrideBackground", true);
                        SetProp(gi, "ViewFlags", vf);

                        // 5b) rotacija Top - samo View 1
                        bool didRot = false;
                        if (viewNo == 1 && "Top".Equals(view1Orientation, StringComparison.OrdinalIgnoreCase))
                        {
                            topRotation = GetTopRotation(viType, vi, out topRotNote);
                            if (topRotation != null)
                            {
                                SetProp(gi, "Rotation", topRotation);
                                didRot = true;
                            }
                        }

                        Inv(vi, "SetGeometryInformation", gi);

                        // 5c) bijela radna pozadina
                        Inv(vi, "SetBackgroundColor", white);

                        // 5d) upisi ViewInformation natrag u ViewGroup i osvjezi zivi prikaz.
                        // ViewGroup.SynchViewDisplay(Int32,bool,bool,bool) - potvrdjeno Int32
                        // (runtime v0.3.0 je bacao ArgumentException na UInt32; SDK C++ header ViewGroup.h:386
                        // deklarira UInt32, managed wrapper koristi Int32).
                        Inv(viewGroup, "SetViewInformation", vi, idx);
                        try { Inv(viewGroup, "SynchViewDisplay", idx, false, false, true); }
                        catch (Exception exSync) { pv["synchWarn"] = ExMsg(exSync); }

                        anyManagedChange = true;
                        changedViews++;
                        pv["result"] = "OK";
                        pv["grid"] = new Dictionary<string, object> { { "from", gridBefore ? "ON" : "OFF" }, { "to", gridEnabled ? "ON" : "OFF" } };
                        pv["overrideBackground"] = new Dictionary<string, object> { { "from", ovrBefore ? "ON" : "OFF" }, { "to", "ON" } };
                        pv["backgroundRgbSet"] = new List<object> { (int)bg[0], (int)bg[1], (int)bg[2] };
                        pv["rotationTopApplied"] = viewNo == 1 ? (object)didRot : "n/a";
                        if (viewNo == 1) pv["rotationTopNote"] = topRotNote;
                    }
                    catch (Exception exView)
                    {
                        errCount++;
                        pv["result"] = "GRESKA: " + ExMsg(exView);
                    }
                    perView.Add(pv);
                }

                steps.Add(Step("views.managed", errCount == 0 ? "OK" : "PARTIAL",
                    "promijenjeno " + changedViews + "/" + views.Length +
                    " pogleda (grid->OFF, pozadina->bijela, OverrideBackground->ON; View 1 rotacija Top=" +
                    (topRotation != null ? "primijenjeno" : "PRESKOCENO: " + topRotNote) + ")"));
            }
            catch (Exception ex)
            {
                errCount++;
                steps.Add(Step("views.managed", "ERROR", "NEPOZNATO: " + ExMsg(ex)));
            }

            // ---------- 6) osvjezavanje zivog prikaza otvorenih pogleda ----------
            // Managed ViewInfo izmjene se pouzdano upisuju u DGN (potvrdjeno verifyAfter + sha256Changed),
            // ali sam upis ne prebojava vec otvoreni viewport. Zato za svaki otvoreni view jos i:
            //   - VIEW TOP <n>   (token CMD_VIEW_TOP, VIEWING, cmdlist.r.h) - samo ako je trazeno "Top"
            //   - UPDATE VIEW <n>(token CMD_UPDATE_VIEW, VIEWING, cmdlist.r.h) - prisilni redraw
            //   - Viewport.SetNeedsRefresh() na aktivnom viewportu (potvrdjeno refleksijom)
            var refreshResults = new List<object>();
            foreach (int v in openViews)
            {
                if ("Top".Equals(view1Orientation, StringComparison.OrdinalIgnoreCase))
                    DoKeyin(session, "VIEW TOP " + v, refreshResults);
                DoKeyin(session, "UPDATE VIEW " + v, refreshResults);
            }
            try
            {
                object vp = Inv(session, "GetActiveViewport");
                if (vp != null && !(vp is string))
                {
                    Inv(vp, "SetNeedsRefresh");
                    refreshResults.Add(new Dictionary<string, object> {
                        { "call", "Viewport.SetNeedsRefresh()" }, { "result", "OK" } });
                }
            }
            catch (Exception ex)
            {
                refreshResults.Add(new Dictionary<string, object> {
                    { "call", "Viewport.SetNeedsRefresh()" }, { "result", "NEPOZNATO: " + ExMsg(ex) } });
            }
            extra["liveRefresh"] = refreshResults;
            steps.Add(Step("views.liveRefresh", "PRETPOSTAVKA",
                "VIEW TOP / UPDATE VIEW za otvorene poglede (" + string.Join(",", IntStrs(openViews)) +
                ") + Viewport.SetNeedsRefresh(); tokeni CMD_VIEW_TOP / CMD_UPDATE_VIEW iz SDK Mstn\\cmdlist.r.h"));

            // ---------- Aktivni View ----------
            steps.Add(Step("view.active", "PRETPOSTAVKA",
                "Trazeni aktivni View = " + activeView + ". Nema potvrdjenog namjenskog API-ja/key-ina za 'aktivni view'. " +
                "Kad je otvoren samo View " + activeView + ", MicroStation ga cini tekucim po eliminaciji. " +
                "Stvarni aktivni view potvrdjuje 'verifyAfter.activeViewportNumber' i Bozidarov test."));

            // ---------- 7) Save Settings ----------
            bool saved = false;
            if (saveSettings)
            {
                try
                {
                    object sc = Inv(viewGroupColl, "SaveChanges");
                    string scStr = sc != null ? sc.ToString() : "(void)";
                    object dsr = EnumVal(DgnSaveReasonTypeName, "SaveSettings");
                    object pc = null;
                    if (dsr != null)
                    {
                        MethodInfo mpc = dgnFile.GetType().GetMethod("ProcessChanges", new[] { dsr.GetType() });
                        if (mpc != null) pc = mpc.Invoke(dgnFile, new object[] { dsr });
                        else steps.Add(Step("saveSettings.processChanges", "WARN", "NEPOZNATO: DgnFile.ProcessChanges(DgnSaveReason) nije pronadjen"));
                    }
                    else
                    {
                        steps.Add(Step("saveSettings.processChanges", "WARN", "NEPOZNATO: enum DgnSaveReason.SaveSettings nije pronadjen"));
                    }
                    saved = true;
                    extra["savedSettings"] = true;
                    steps.Add(Step("saveSettings", "OK",
                        "ViewGroupCollection.SaveChanges()=" + scStr +
                        "; DgnFile.ProcessChanges(DgnSaveReason.SaveSettings)=" + (pc != null ? pc.ToString() : "(void/skip)")));
                }
                catch (Exception ex)
                {
                    errCount++;
                    steps.Add(Step("saveSettings", "ERROR", "NEPOZNATO: " + ExMsg(ex)));
                }
            }
            else
            {
                steps.Add(Step("saveSettings", "SKIP", "parameters.saveSettings=false - postavke nisu trajno spremljene"));
            }

            // ---------- 8) interna provjera POSLIJE ----------
            try
            {
                string s;
                var after = InterfaceStateReader.Build(VerifyParams(views), out s);
                extra["verifyAfter"] = Compact(after);
                steps.Add(Step("verify.after", "OK", s));
            }
            catch (Exception ex)
            {
                extra["verifyAfter"] = "NEPOZNATO: " + ExMsg(ex);
                steps.Add(Step("verify.after", "WARN", "citanje 'after' nije uspjelo: " + ExMsg(ex)));
            }

            // ---------- 9) SHA-256 poslije ----------
            try
            {
                string shaAfter = Sha256OfFile(activePath);
                extra["sha256After"] = shaAfter;
                extra["sha256AfterSource"] = "SHA-256 aktivnog DGN-a na disku nakon " + (saved ? "Save Settings" : "izmjena (BEZ spremanja)");
                extra["sha256Changed"] = shaBefore != null && shaBefore != shaAfter;
                steps.Add(Step("sha256.after", "OK", shaAfter + (shaBefore == shaAfter ? "  (nepromijenjeno na disku)" : "  (promijenjeno)")));
            }
            catch (Exception ex)
            {
                extra["sha256After"] = "NEPOZNATO: " + ExMsg(ex);
                steps.Add(Step("sha256.after", "WARN", ExMsg(ex)));
            }

            extra["touchedDgn"] = anyManagedChange;

            // ---------- status ----------
            if (!anyManagedChange && errCount > 0)
            {
                status = "ERROR";
                extra["incident"] = "Nijedna managed izmjena nije primijenjena, a doslo je do pogresaka. " +
                                    "Backup je na " + backupPath + ". Bez auto-rollbacka.";
                summary = "GRESKA: nijedna izmjena nije primijenjena (" + errCount + " pogr.). Backup: " + backupPath;
            }
            else if (errCount > 0)
            {
                status = "PARTIAL";
                extra["incident"] = "Djelomicna primjena: " + changedViews + "/" + views.Length +
                                    " pogleda promijenjeno, " + errCount + " pogresaka; spremljeno=" + saved +
                                    ". Backup: " + backupPath + ". Bez auto-rollbacka.";
                summary = "DJELOMICNO: " + changedViews + "/" + views.Length + " pogleda, " + errCount +
                          " pogr., spremljeno=" + saved + ".";
            }
            else
            {
                status = "OK";
                summary = "Primijenjeno na " + changedViews + "/" + views.Length +
                          " pogleda; grid OFF; pozadina bijela; View 1 Top=" + (topRotation != null) +
                          "; VIEW ON/OFF (keyin); spremljeno=" + saved + ".";
            }
            return extra;
        }

        // ================================================================= guardrails

        private static Dictionary<string, object> CheckGuardrails(
            object session, string wantWorkspace, string wantWorkset, string wantFileName, string forbidFileName,
            out bool ok, out string why, out object dgnFile, out string activePath)
        {
            var d = new Dictionary<string, object>();
            ok = false; why = null; dgnFile = null; activePath = null;

            string wsName = GetConfigVar("_USTN_WORKSPACENAME");
            string wsSet = GetConfigVar("_USTN_WORKSETNAME");
            d["_USTN_WORKSPACENAME"] = wsName;
            d["_USTN_WORKSETNAME"] = wsSet;

            object activeDgn = TryCall(session, "GetActiveDgnFile");
            if (activeDgn is string) activeDgn = null;   // TryCall vraca "NEPOZNATO: ..." kod pogreske
            dgnFile = activeDgn;

            string activeFileName = Convert.ToString(TryCall(session, "GetActiveFileName"), CultureInfo.InvariantCulture);
            string fromDgnFile = null;
            if (dgnFile != null)
            {
                try { fromDgnFile = Convert.ToString(Inv(dgnFile, "GetFileName"), CultureInfo.InvariantCulture); }
                catch { }
            }
            activePath = !string.IsNullOrEmpty(fromDgnFile) ? fromDgnFile : activeFileName;
            d["activeFilePath"] = activePath;

            if (dgnFile == null)
            {
                d["activeDgnFile"] = "NEPOZNATO: Session.GetActiveDgnFile() nije vratio DgnFile";
                why = "Nema aktivnog DGN-a (Session.GetActiveDgnFile() je null).";
                return d;
            }

            bool isReadOnly = true;
            try { isReadOnly = ToBool(Prop(dgnFile, "IsReadOnly")); } catch { }
            d["isReadOnly"] = isReadOnly;

            string baseName = null;
            try { baseName = Path.GetFileName(activePath); } catch { }
            d["activeFileBaseName"] = baseName;

            bool cWs = string.Equals(wsName, wantWorkspace, StringComparison.OrdinalIgnoreCase);
            bool cSet = string.Equals(wsSet, wantWorkset, StringComparison.OrdinalIgnoreCase);
            bool cName = baseName != null && string.Equals(baseName, wantFileName, StringComparison.OrdinalIgnoreCase);
            bool cNotForbidName = baseName != null && !string.Equals(baseName, forbidFileName, StringComparison.OrdinalIgnoreCase);
            bool cNotSeedPath = !string.IsNullOrEmpty(activePath) &&
                                activePath.IndexOf(@"\Standards\Seed\", StringComparison.OrdinalIgnoreCase) < 0;
            bool cWritable = !isReadOnly;

            d["checks"] = new Dictionary<string, object>
            {
                { "workspaceMatches", cWs }, { "worksetMatches", cSet },
                { "fileNameMatches", cName }, { "notForbiddenFileName", cNotForbidName },
                { "notSeedPath", cNotSeedPath }, { "fileWritable", cWritable }
            };

            if (!cWs) { why = "WorkSpace nije '" + wantWorkspace + "' (ocitano: '" + wsName + "')."; return d; }
            if (!cSet) { why = "WorkSet nije '" + wantWorkset + "' (ocitano: '" + wsSet + "')."; return d; }
            if (!cName) { why = "Aktivna datoteka nije '" + wantFileName + "' (ocitano: '" + baseName + "')."; return d; }
            if (!cNotForbidName) { why = "Aktivna datoteka je zabranjeno ime '" + forbidFileName + "'."; return d; }
            if (!cNotSeedPath) { why = "Aktivna putanja sadrzi '\\Standards\\Seed\\' - odbijeno (seed)."; return d; }
            if (!cWritable) { why = "Aktivna datoteka je read-only."; return d; }

            ok = true;
            return d;
        }

        // ================================================================= view helpers

        private static object GetTopRotation(Type viType, object viInstance, out string note)
        {
            note = null;
            try
            {
                if (viType == null) { note = "NEPOZNATO: tip ViewInformation nije pronadjen"; return null; }
                MethodInfo m = viType.GetMethod("GetStandardViewByName",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                if (m == null) { note = "NEPOZNATO: GetStandardViewByName nije pronadjen"; return null; }
                object[] args = new object[] { null, null, "Top" };
                object st = m.Invoke(m.IsStatic ? null : viInstance, args); // SDK ViewInfo.h:478 -> staticka
                if (args[0] == null) { note = "NEPOZNATO: rotacija je null (status=" + st + ")"; return null; }
                note = "ViewInformation.GetStandardViewByName(\"Top\") -> status=" + st +
                       (m.IsStatic ? " (static)" : " (instance)");
                return args[0];
            }
            catch (Exception ex) { note = "NEPOZNATO: " + ExMsg(ex); return null; }
        }

        private static object MakeRgb(Type rgbType, byte[] rgb)
        {
            if (rgbType == null) throw new Exception("NEPOZNATO: tip RgbColorDef nije pronadjen");
            object o = Activator.CreateInstance(rgbType); // value type -> boxed, zero-init
            SetProp(o, "R", rgb[0]);
            SetProp(o, "G", rgb[1]);
            SetProp(o, "B", rgb[2]);
            return o;
        }

        private static void DoKeyin(object session, string keyin, List<object> sink)
        {
            var r = new Dictionary<string, object> { { "keyin", keyin } };
            try
            {
                MethodInfo m = session.GetType().GetMethod("Keyin", new[] { typeof(string) });
                if (m == null) { r["result"] = "NEPOZNATO: Session.Keyin(string) nije pronadjen"; sink.Add(r); return; }
                m.Invoke(session, new object[] { keyin });
                r["result"] = "poslano (PRETPOSTAVKA: izvrseno; ishod potvrdjuje verifyAfter)";
            }
            catch (Exception ex) { r["result"] = "GRESKA: " + ExMsg(ex); }
            sink.Add(r);
        }

        private static Dictionary<string, object> VerifyParams(int[] views)
        {
            return new Dictionary<string, object>
            {
                { "views", Box(views) },
                { "includeDisplayStyles", false },
                { "includeWorkspaceContext", true },
                { "includeUiPanels", false }
            };
        }

        /// <summary>Iz pune inventure izvlaci samo ono sto BEN-UI-002 mijenja.</summary>
        private static Dictionary<string, object> Compact(Dictionary<string, object> inv)
        {
            var outp = new Dictionary<string, object>();
            try
            {
                var av = inv["activeViewport"] as Dictionary<string, object>;
                outp["activeViewportNumber"] = av != null && av.ContainsKey("viewNumber") ? av["viewNumber"] : "NEPOZNATO";
            }
            catch { outp["activeViewportNumber"] = "NEPOZNATO"; }

            var list = new List<object>();
            try
            {
                var vv = inv["views"] as Dictionary<string, object>;
                var items = vv != null ? vv["items"] as List<object> : null;
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        var v = it as Dictionary<string, object>;
                        if (v == null) continue;
                        var c = new Dictionary<string, object>();
                        c["view"] = Pick(v, "requestedNumber");
                        c["isOpenOrDisplayed"] = Pick(v, "isOpenOrDisplayed");
                        c["standardViewRotation"] = Pick(v, "standardViewRotation");
                        c["isTop"] = Pick(v, "isTop");
                        c["grid"] = Pick(v, "grid");
                        c["overrideBackgroundFlag"] = Pick(v, "overrideBackgroundFlag");
                        var bg = v.ContainsKey("backgroundColor") ? v["backgroundColor"] as Dictionary<string, object> : null;
                        if (bg != null)
                            c["backgroundRgb"] = new List<object> {
                                bg.ContainsKey("R") ? bg["R"] : "?",
                                bg.ContainsKey("G") ? bg["G"] : "?",
                                bg.ContainsKey("B") ? bg["B"] : "?" };
                        else
                            c["backgroundRgb"] = Pick(v, "backgroundColor");
                        list.Add(c);
                    }
                }
            }
            catch (Exception ex) { outp["viewsError"] = "NEPOZNATO: " + ExMsg(ex); }
            outp["views"] = list;
            return outp;
        }

        // ================================================================= reflection helpers

        private static object GetSessionInstance()
        {
            try
            {
                Type t = FindType(SessionTypeName);
                if (t == null) return null;
                PropertyInfo pi = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                return pi != null ? pi.GetValue(null, null) : null;
            }
            catch { return null; }
        }

        private static string GetConfigVar(string name)
        {
            try
            {
                Type cfg = FindType(ConfigMgrTypeName);
                if (cfg == null) return "NEPOZNATO: tip ConfigurationManager nije pronadjen";
                MethodInfo isDef = cfg.GetMethod("IsVariableDefined", new[] { typeof(string) });
                if (isDef != null && !Convert.ToBoolean(isDef.Invoke(null, new object[] { name })))
                    return "NEPOZNATO: config varijabla '" + name + "' nije definirana";
                MethodInfo getVar = cfg.GetMethod("GetVariable", new[] { typeof(string) });
                object v = getVar != null ? getVar.Invoke(null, new object[] { name }) : null;
                return v as string;
            }
            catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static object EnumVal(string enumTypeName, string valueName)
        {
            try
            {
                Type t = FindType(enumTypeName);
                if (t == null || !t.IsEnum) return null;
                return Enum.Parse(t, valueName);
            }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        private static object Inv(object target, string method, params object[] args)
        {
            if (target == null) throw new Exception("cilj je null (" + method + ")");
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i] != null ? args[i].GetType() : typeof(object);

            MethodInfo mi = null;
            try { mi = target.GetType().GetMethod(method, types); } catch { }
            if (mi == null)
            {
                foreach (MethodInfo cand in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (cand.Name == method && cand.GetParameters().Length == args.Length) { mi = cand; break; }
                }
            }
            if (mi == null) throw new Exception("metoda '" + method + "(" + args.Length + " arg)' nije pronadjena na " + target.GetType().FullName);
            return mi.Invoke(target, args);
        }

        private static object TryCall(object target, string method)
        {
            try { return Inv(target, method); }
            catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static object Prop(object target, string name)
        {
            if (target == null) throw new Exception("cilj je null (get " + name + ")");
            PropertyInfo pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null) throw new Exception("property '" + name + "' nije pronadjen na " + target.GetType().FullName);
            return pi.GetValue(target, null);
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) throw new Exception("cilj je null (set " + name + ")");
            PropertyInfo pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null) throw new Exception("settable property '" + name + "' nije pronadjen na " + target.GetType().FullName);
            object v = value;
            if (pi.PropertyType == typeof(byte) && !(value is byte)) v = Convert.ToByte(value, CultureInfo.InvariantCulture);
            pi.SetValue(target, v, null);
        }

        // ================================================================= util

        private static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static Dictionary<string, object> Step(string step, string st, string detail)
        {
            return new Dictionary<string, object> { { "step", step }, { "status", st }, { "detail", detail } };
        }

        private static string Now()
        {
            return DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "task";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append((char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
            return sb.Length == 0 ? "task" : sb.ToString();
        }

        private static string ExMsg(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static bool ToBool(object o)
        {
            if (o is bool) return (bool)o;
            bool b;
            return bool.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out b) && b;
        }

        private static object Pick(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : "NEPOZNATO";
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
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return fallback;
        }

        private static int ParseInt(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                int r;
                if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            }
            return fallback;
        }

        private static bool ParseBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) ? b : fallback;
        }

        private static int[] ParseIntArray(Dictionary<string, object> d, string key, int[] fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            var result = new List<int>();
            if (v is IEnumerable && !(v is string))
            {
                foreach (object item in (IEnumerable)v)
                {
                    int n;
                    if (item != null && int.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    {
                        if (n >= 1 && n <= 8 && !result.Contains(n)) result.Add(n);
                    }
                }
            }
            return result.Count > 0 ? result.ToArray() : fallback;
        }

        private static byte[] ParseRgb(Dictionary<string, object> d, string key)
        {
            var def = new byte[] { 255, 255, 255 };
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || !(v is IEnumerable) || v is string) return def;
            var nums = new List<byte>();
            foreach (object item in (IEnumerable)v)
            {
                int n;
                if (item != null && int.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    nums.Add((byte)Math.Max(0, Math.Min(255, n)));
            }
            return nums.Count == 3 ? nums.ToArray() : def;
        }

        private static List<object> Box(int[] a)
        {
            var l = new List<object>();
            foreach (int i in a) l.Add(i);
            return l;
        }

        private static IEnumerable<string> IntStrs(int[] a)
        {
            foreach (int i in a) yield return i.ToString(CultureInfo.InvariantCulture);
        }
    }
}
