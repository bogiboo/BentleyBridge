/*----------------------------------------------------------------------------------------+
| DisplayModeManager - operacije CREATE_DISPLAY_MODES i APPLY_DISPLAY_MODE (BEN-UI-003).   |
|                                                                                        |
| Tri korisnicka nacina prikaza u aktivnom Test_2.dgn:                                     |
|   ETAZ_PUNA_ISPUNA   - ispuna ON,  prozirnost 0%,  obrisi vidljivi                       |
|   ETAZ_TRANSPARENTNO - ispuna ON,  prozirnost 70%, obrisi vidljivi                       |
|   ETAZ_BEZ_ISPUNE    - ispuna OFF,                  obrisi vidljivi                      |
|                                                                                        |
| ------------------------------------------------------------------------------------ R5 |
| TEHNICKI DOKAZ (reflektirano iz Bentley.DgnPlatformNET.dll) - gdje se sto pohranjuje:   |
|                                                                                        |
|  * OBRISI (vidljivi rubovi)  -> U DISPLAY STYLE.                                         |
|      DisplayStyle.GetFlags()/SetFlags(DisplayStyleFlags); DisplayStyleFlags.DisplayVisibleEdges {get;set}. |
|  * GLOBALNA PROZIRNOST       -> U DISPLAY STYLE.                                         |
|      DisplayStyle.GetOverrides()/SetOverrides(ViewDisplayOverrides,DgnFile);             |
|      ViewDisplayOverrides.OverrideUseTransparency {get;set} + OverrideTransparency (Double 0..1). |
|  * RENDER MODE              -> U DISPLAY STYLE. DisplayStyle.DisplayMode (MSRenderMode). |
|      Naslijedjen iz baznog stila "Illustration" (Clone) - ne mijenja se.                 |
|  * ISPUNA (Fill on/off)     -> NIJE u Display Styleu. To je per-View postavka:           |
|      ViewFlags.Fill {get;set} (DisplayStyleFlags NEMA Fill zastavicu).                   |
|      -> zato je Fill "prateca View postavka" koju operacija primjenjuje uz stil.        |
|  * BIJELA POZADINA (OBAVEZNA za svaki nacin) -> per-View postavka:                       |
|      ViewFlags.OverrideBackground=true + ViewInformation.SetBackgroundColor(255,255,255). |
|      Imenovani Display Style NE moze nositi boju pozadine: managed API ima samo           |
|      DisplayStyleFlags.OverrideBackgroundColor (bool) BEZ settera boje. Zato se ta        |
|      zastavica drzi na FALSE (stil ne pregazi View), a bijela se osigurava kao prateca    |
|      View postavka u svih 8 pogleda (AssertWhiteBaselineAllViews) pri svakoj primjeni.    |
|  * ByLevel simbolika        -> ocuvana: ViewDisplayOverrides.OverrideElementColor/LineStyle/Weight = false. |
|                                                                                        |
| Zbog Fill-a operacija je razdvojena kako trazi BEN-UI-003 spec:                          |
|   CREATE_DISPLAY_MODES - stvori 3 imenovana stila + primijeni defaultMode na View 1 +   |
|                          prateca View postavka Fill + ocuva Top/grid/pozadinu.          |
|   APPLY_DISPLAY_MODE   - primijeni imenovani stil na View + prateci Fill + spremi.       |
|                                                                                        |
| Potvrdjeni API-ji (reflektirano): DisplayStyle.Clone(DgnFile,String),                   |
|   DisplayStyle.GetFlags/SetFlags, DisplayStyle.GetOverrides/SetOverrides,                |
|   DisplayStyle.DisplayMode, DisplayStyle.IsUsableForViews, EnsureDisplayStyleHandler,    |
|   DisplayStyleManager.{DoesDisplayStyleExistInFile, WriteDisplayStyleToFile,             |
|   ApplyDisplayStyleToView, GetDisplayStyleForViewInformation, CopyDisplayStyleToFile,    |
|   RemoveDisplayStyleFromFile}. SDK C++ pandan: examples\Visualization\DisplayStyleExample.|
|                                                                                        |
| Bez COM/MVBA/OpenDesignFile. Ne dira geometriju/modele/razine/reference/rastere/        |
| element properties/georeferenciranje/CFG/Registry/WorkSpace/seed. Bez auto-rollbacka.   |
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
    internal static class DisplayModeManager
    {
        private const string SessionTypeName = "Bentley.MstnPlatformNET.Session";
        private const string ConfigMgrTypeName = "Bentley.DgnPlatformNET.ConfigurationManager";
        private const string ViewInfoTypeName = "Bentley.DgnPlatformNET.ViewInformation";
        private const string RgbColorDefTypeName = "Bentley.DgnPlatformNET.RgbColorDef";
        private const string DgnSaveReasonTypeName = "Bentley.DgnPlatformNET.DgnSaveReason";
        private const string DisplayStyleMgrTypeName = "Bentley.DgnPlatformNET.DisplayStyleManager";

        private const string BackupRoot = @"C:\AITools\BentleyBridge\runtime\backups\BEN-UI-003";

        private const string DefWorkspace = "ETAZIRANJE_RAZVOJ";
        private const string DefWorkset = "TEST_OSNOVA";
        private const string DefFileName = "Test_2.dgn";
        private const string DefForbidFileName = "ETAZIRANJE_3D_METRIC.dgn";

        private static readonly string[] KnownModes = { "ETAZ_PUNA_ISPUNA", "ETAZ_TRANSPARENTNO", "ETAZ_BEZ_ISPUNE" };

        // Prateca (companion) View postavka Fill po nazivu nacina - deterministicki, ne pogadja se.
        private static bool FillForMode(string mode)
        {
            return !string.Equals(mode, "ETAZ_BEZ_ISPUNE", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================================
        //  CREATE_DISPLAY_MODES
        // =====================================================================================
        internal static Dictionary<string, object> CreateModes(
            Dictionary<string, object> task, string taskId, out string summary, out string status)
        {
            var extra = NewExtra("CREATE_DISPLAY_MODES");
            var steps = (List<object>)extra["steps"];

            Dictionary<string, object> target = GetObject(task, "target");
            Dictionary<string, object> p = GetObject(task, "parameters");
            string baseStyleName = GetString(p, "baseDisplayStyle", "Illustration");
            string defaultMode = GetString(p, "defaultMode", "ETAZ_PUNA_ISPUNA");
            byte[] bg = ParseRgb(p, "backgroundRgb");
            bool saveSettings = ParseBool(p, "saveSettings", true);
            bool overwriteExisting = ParseBool(p, "overwriteExisting", false);
            List<Dictionary<string, object>> modes = ParseModes(p);

            extra["requested"] = new Dictionary<string, object>
            {
                { "baseDisplayStyle", baseStyleName }, { "defaultMode", defaultMode },
                { "modes", ModesEcho(modes) },
                { "backgroundRgb", new List<object> { (int)bg[0], (int)bg[1], (int)bg[2] } },
                { "saveSettings", saveSettings }, { "overwriteExisting", overwriteExisting }
            };

            object session = GetSessionInstance();
            if (session == null) { status = "ABORTED"; steps.Add(Step("guardrails", "ABORTED", "NEPOZNATO: Session.Instance je null")); summary = "PREKINUTO: nema Session.Instance."; return extra; }

            // 1) guardrails
            bool okGuard; string whyGuard; object dgnFile; string activePath;
            extra["guardrails"] = CheckGuardrails(session, target, out okGuard, out whyGuard, out dgnFile, out activePath);
            if (!okGuard) { status = "ABORTED"; steps.Add(Step("guardrails", "ABORTED", whyGuard)); summary = "PREKINUTO (bez izmjena): " + whyGuard; return extra; }
            steps.Add(Step("guardrails", "OK", activePath));

            // 2) backup + sha before
            string backupPath, shaBefore;
            if (!DoBackup(dgnFile, activePath, taskId, extra, steps, out backupPath, out shaBefore))
            { status = "ABORTED"; summary = "PREKINUTO (bez izmjena): backup nije uspio."; return extra; }

            // 3) verify.before
            SafeVerify(extra, "verifyBefore", steps, "verify.before");

            int errCount = 0;
            bool anyChange = false;
            object viewGroup = null, viewGroupColl = null, view1 = null;
            var createdInfo = new List<object>();
            object defaultStyleObj = null;
            bool defaultFill = FillForMode(defaultMode);

            try
            {
                viewGroupColl = Inv(dgnFile, "GetViewGroups");
                viewGroup = Inv(viewGroupColl, "GetActive");
                if (viewGroup == null) throw new Exception("aktivni ViewGroup je null");
                view1 = Inv(viewGroup, "GetViewInformation", 0);
                if (view1 == null) throw new Exception("View 1 ViewInformation je null");

                Type dsm = FindType(DisplayStyleMgrTypeName);
                if (dsm == null) throw new Exception("NEPOZNATO: tip DisplayStyleManager nije pronadjen");

                // --- BAZNI STIL SE DOHVACA PRVI (prije ikakvog uklanjanja) ---
                // Primarno po imenu (baseDisplayStyle, npr. "Illustration") preko CopyDisplayStyleToFile(name,dgnFile,dgnFile),
                // jer nakon prijasnjeg APPLY-a View 1 moze koristiti bas neki od ETAZ_ stilova koje cemo ukloniti.
                // Rezerva: View1.GetDisplayStyle() ako to nije jedan od ETAZ_ imena.
                object baseStyle = null;
                object byNameTry = TryStatic(dsm, "CopyDisplayStyleToFile", new object[] { baseStyleName, dgnFile, dgnFile });
                if (byNameTry != null && !(byNameTry is string)) baseStyle = byNameTry;
                if (baseStyle == null)
                {
                    object cur = TryCall(view1, "GetDisplayStyle");
                    if (cur != null && !(cur is string))
                    {
                        string curName = Convert.ToString(SafeGet(cur, "Name"), CultureInfo.InvariantCulture);
                        if (Array.IndexOf(KnownModes, curName) < 0) baseStyle = cur;
                    }
                }
                if (baseStyle == null || baseStyle is string)
                    throw new Exception("NEPOZNATO: ne mogu dohvatiti bazni DisplayStyle '" + baseStyleName +
                                        "' (CopyDisplayStyleToFile) niti prihvatljiv trenutni stil View 1. Rezultat: " + byNameTry);
                string baseNameActual = Convert.ToString(Prop(baseStyle, "Name"), CultureInfo.InvariantCulture);

                // --- postojeci ciljni stilovi ---
                var existing = new List<string>();
                foreach (var m in modes)
                {
                    string name = GetString(m, "name", null);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (DsExists(dsm, name, dgnFile)) existing.Add(name);
                }
                if (existing.Count > 0 && !overwriteExisting)
                {
                    // CONFLICT: ne prepisujem tiho (BEN-UI-003 spec). Bez ijedne izmjene.
                    status = "CONFLICT";
                    extra["conflict"] = "Vec postoje stilovi: " + string.Join(", ", existing.ToArray()) +
                                        ". Za dokumentiranu migraciju pokreni s parameters.overwriteExisting=true i novim taskId.";
                    steps.Add(Step("displayStyles.conflict", "CONFLICT", (string)extra["conflict"]));
                    summary = "CONFLICT: stil(ovi) vec postoje: " + string.Join(", ", existing.ToArray()) + ". Bez izmjena.";
                    return extra;
                }
                if (existing.Count > 0)
                {
                    // Dokumentirana migracija: prvo vrati View 1 na bazni stil (da ne ostane siroce
                    // referencirajuci stil kojeg brisemo), pa ukloni ETAZ_ stilove, pa ponovno stvori.
                    TryStatic(dsm, "ApplyDisplayStyleToView", new[] { baseStyle, view1 });
                    var removed = new List<object>();
                    foreach (string name in existing)
                    {
                        object rr = TryStatic(dsm, "RemoveDisplayStyleFromFile", new object[] { name, dgnFile });
                        removed.Add(new Dictionary<string, object> {
                            { "name", name }, { "removed", !DsExists(dsm, name, dgnFile) },
                            { "call", rr is string ? (string)rr : "RemoveDisplayStyleFromFile OK" } });
                    }
                    extra["overwriteMigration"] = removed;
                    steps.Add(Step("displayStyles.overwrite", "OK",
                        "overwriteExisting=true: View 1 vracen na '" + baseNameActual + "', uklonjeni (" +
                        string.Join(", ", existing.ToArray()) + ") prije ponovnog stvaranja"));
                }
                object baseModeEnum = Prop(baseStyle, "DisplayMode");
                extra["baseStyle"] = new Dictionary<string, object>
                {
                    { "requestedName", baseStyleName },
                    { "actualName", baseNameActual },
                    { "displayMode", Convert.ToString(baseModeEnum, CultureInfo.InvariantCulture) },
                    { "acquiredBy", "DisplayStyleManager.CopyDisplayStyleToFile(name,dgnFile,dgnFile) ili View1.GetDisplayStyle() rezerva" },
                    { "note", string.Equals(baseNameActual, baseStyleName, StringComparison.OrdinalIgnoreCase)
                              ? "poklapa se sa zahtjevom"
                              : "PAZI: bazni stil je '" + baseNameActual + "', ne '" + baseStyleName + "'." }
                };

                // --- stvori 3 stila ---
                foreach (var m in modes)
                {
                    string name = GetString(m, "name", null);
                    bool fillEnabled = ParseBoolV(m, "fillEnabled", true);
                    int tp = ParseIntV(m, "transparencyPercent", 0);
                    bool edgesVisible = ParseBoolV(m, "edgesVisible", true);
                    var ci = new Dictionary<string, object> { { "name", name } };
                    try
                    {
                        object clone = Inv(baseStyle, "Clone", dgnFile, name);
                        if (clone == null) throw new Exception("Clone vratio null");

                        // render mode = bazni (naslijedjen klonom); eksplicitno postavimo isti radi jasnoce
                        SetProp(clone, "DisplayMode", baseModeEnum);
                        SetProp(clone, "IsUsableForViews", true);

                        // flags: obrisi vidljivi; stil NE pregazi View pozadinu
                        object flags = Inv(clone, "GetFlags");
                        SetProp(flags, "DisplayVisibleEdges", edgesVisible);
                        TrySet(flags, "OverrideBackgroundColor", false);
                        Inv(clone, "SetFlags", flags);

                        // overrides: globalna prozirnost; ByLevel simbolika ocuvana (nema element override)
                        object ovr = Inv(clone, "GetOverrides");
                        double frac = Math.Max(0.0, Math.Min(1.0, tp / 100.0));
                        SetProp(ovr, "OverrideUseTransparency", tp > 0);
                        SetProp(ovr, "OverrideTransparency", frac);
                        TrySet(ovr, "OverrideElementColor", false);
                        TrySet(ovr, "OverrideElementLineStyle", false);
                        TrySet(ovr, "OverrideElementWeight", false);
                        TrySet(ovr, "OverrideMaterial", false);
                        Inv(clone, "SetOverrides", ovr, dgnFile);

                        TryCall1(clone, "EnsureDisplayStyleHandler", dgnFile);
                        object written = TryStatic(dsm, "WriteDisplayStyleToFile", new[] { clone, dgnFile });
                        object writtenStyle = (written != null && !(written is string)) ? written : clone;

                        // read-back
                        object rbFlags = Inv(writtenStyle, "GetFlags");
                        object rbOvr = Inv(writtenStyle, "GetOverrides");
                        ci["created"] = true;
                        ci["inFile"] = DsExists(dsm, name, dgnFile);
                        ci["displayMode"] = Convert.ToString(Prop(writtenStyle, "DisplayMode"), CultureInfo.InvariantCulture);
                        ci["displayVisibleEdges"] = SafeGet(rbFlags, "DisplayVisibleEdges");
                        ci["overrideBackgroundColor"] = SafeGet(rbFlags, "OverrideBackgroundColor"); // mora ostati false: bijela dolazi iz View postavke
                        ci["overrideUseTransparency"] = SafeGet(rbOvr, "OverrideUseTransparency");
                        ci["overrideTransparency"] = SafeGet(rbOvr, "OverrideTransparency");
                        ci["companionViewFill"] = fillEnabled;
                        ci["companionViewBackground"] = new List<object> { (int)bg[0], (int)bg[1], (int)bg[2] };
                        anyChange = true;

                        if (string.Equals(name, defaultMode, StringComparison.OrdinalIgnoreCase))
                        {
                            defaultStyleObj = writtenStyle;
                            defaultFill = fillEnabled;
                        }
                    }
                    catch (Exception exM)
                    {
                        errCount++;
                        ci["created"] = false;
                        ci["error"] = "GRESKA: " + ExMsg(exM);
                    }
                    createdInfo.Add(ci);
                }
                extra["displayStyles"] = createdInfo;
                steps.Add(Step("displayStyles.create", errCount == 0 ? "OK" : "PARTIAL",
                    "stvoreno " + CountCreated(createdInfo) + "/" + modes.Count + " imenovanih stilova"));

                // --- bijela pozadina je OBAVEZNA za svaki nacin; drzimo je kao pratecu View postavku
                //     u svih 8 pogleda (imenovani Display Style NE moze nositi boju pozadine - managed API
                //     ima samo DisplayStyleFlags.OverrideBackgroundColor bez settera boje; drzimo ga false). ---
                extra["whiteBackgroundAllViews"] = AssertWhiteBaselineAllViews(viewGroup, bg, steps);

                // --- primijeni defaultMode na View 1 + prateca View postavka + ocuvaj Top/grid/pozadinu ---
                if (defaultStyleObj != null)
                {
                    ApplyStyleAndCompanion(dsm, session, viewGroup, view1, defaultStyleObj, defaultMode, defaultFill, bg, extra, steps, true);
                }
                else
                {
                    steps.Add(Step("applyDefault", "WARN", "defaultMode '" + defaultMode + "' nije stvoren - nista nije primijenjeno na View 1"));
                    errCount++;
                }
            }
            catch (Exception ex)
            {
                errCount++;
                steps.Add(Step("displayStyles", "ERROR", "NEPOZNATO: " + ExMsg(ex)));
            }

            // save
            bool saved = DoSave(dgnFile, viewGroupColl, saveSettings, steps, ref errCount);
            extra["savedSettings"] = saved;

            SafeVerify(extra, "verifyAfter", steps, "verify.after");
            AddDisplayStyleReadback(extra, dgnFile);
            ShaAfter(extra, activePath, shaBefore, saved, steps);
            extra["touchedDgn"] = anyChange;

            status = anyChange && errCount == 0 ? "OK" : (anyChange ? "PARTIAL" : "ERROR");
            summary = status == "OK"
                ? "Stvorena 3 nacina prikaza; '" + defaultMode + "' primijenjen na View 1 (Fill=" + defaultFill + "); spremljeno=" + saved + "."
                : (status == "PARTIAL"
                    ? "DJELOMICNO: " + CountCreated(createdInfo) + "/" + modes.Count + " stilova, " + errCount + " pogr., spremljeno=" + saved + "."
                    : "GRESKA: nijedan nacin nije stvoren. Backup: " + backupPath);
            if (status != "OK") extra["incident"] = summary + " Backup: " + backupPath + ". Bez auto-rollbacka.";
            return extra;
        }

        // =====================================================================================
        //  APPLY_DISPLAY_MODE
        // =====================================================================================
        internal static Dictionary<string, object> ApplyMode(
            Dictionary<string, object> task, string taskId, out string summary, out string status)
        {
            var extra = NewExtra("APPLY_DISPLAY_MODE");
            var steps = (List<object>)extra["steps"];

            Dictionary<string, object> target = GetObject(task, "target");
            Dictionary<string, object> p = GetObject(task, "parameters");
            string mode = GetString(p, "mode", null);
            int viewNo = ParseInt(p, "view", 1);
            byte[] bg = ParseRgb(p, "backgroundRgb");
            bool saveSettings = ParseBool(p, "saveSettings", true);
            extra["requested"] = new Dictionary<string, object> { { "mode", mode }, { "view", viewNo }, { "saveSettings", saveSettings } };

            if (string.IsNullOrEmpty(mode)) { status = "ABORTED"; steps.Add(Step("params", "ABORTED", "parameters.mode nedostaje")); summary = "PREKINUTO: nedostaje 'mode'."; return extra; }

            object session = GetSessionInstance();
            if (session == null) { status = "ABORTED"; steps.Add(Step("guardrails", "ABORTED", "Session.Instance null")); summary = "PREKINUTO: nema Session.Instance."; return extra; }

            bool okGuard; string whyGuard; object dgnFile; string activePath;
            extra["guardrails"] = CheckGuardrails(session, target, out okGuard, out whyGuard, out dgnFile, out activePath);
            if (!okGuard) { status = "ABORTED"; steps.Add(Step("guardrails", "ABORTED", whyGuard)); summary = "PREKINUTO (bez izmjena): " + whyGuard; return extra; }
            steps.Add(Step("guardrails", "OK", activePath));

            string backupPath, shaBefore;
            if (!DoBackup(dgnFile, activePath, taskId, extra, steps, out backupPath, out shaBefore))
            { status = "ABORTED"; summary = "PREKINUTO: backup nije uspio."; return extra; }

            SafeVerify(extra, "verifyBefore", steps, "verify.before");

            int errCount = 0; bool anyChange = false;
            object viewGroupColl = null;
            try
            {
                Type dsm = FindType(DisplayStyleMgrTypeName);
                if (dsm == null) throw new Exception("NEPOZNATO: DisplayStyleManager nije pronadjen");
                if (!DsExists(dsm, mode, dgnFile))
                {
                    status = "ABORTED";
                    steps.Add(Step("mode.lookup", "ABORTED", "Stil '" + mode + "' ne postoji u datoteci. Prvo pokreni CREATE_DISPLAY_MODES."));
                    summary = "PREKINUTO: stil '" + mode + "' ne postoji.";
                    return extra;
                }

                viewGroupColl = Inv(dgnFile, "GetViewGroups");
                object viewGroup = Inv(viewGroupColl, "GetActive");
                int idx = viewNo - 1;
                object vi = Inv(viewGroup, "GetViewInformation", idx);
                if (vi == null) throw new Exception("View " + viewNo + " ViewInformation je null");

                // Dohvat imenovanog stila: CopyDisplayStyleToFile(name, dgnFile, dgnFile).
                // PRETPOSTAVKA: kod istog src==dest i postojeceg imena vraca postojeci stil bez dupliciranja.
                // Kontrolirani test provjerava da NE nastaje duplikat.
                object style = TryStatic(dsm, "CopyDisplayStyleToFile", new[] { mode, dgnFile, dgnFile });
                if (style == null || style is string)
                    throw new Exception("NEPOZNATO: ne mogu dohvatiti stil '" + mode + "' (CopyDisplayStyleToFile): " + style);
                extra["styleHandleNote"] = "dohvat preko DisplayStyleManager.CopyDisplayStyleToFile(name, dgnFile, dgnFile) - PRETPOSTAVKA: bez duplikata; provjeri u testu";

                bool fill = FillForMode(mode);
                ApplyStyleAndCompanion(dsm, session, viewGroup, vi, style, mode, fill, bg, extra, steps, viewNo == 1);
                anyChange = true;
            }
            catch (Exception ex)
            {
                errCount++;
                steps.Add(Step("apply", "ERROR", "NEPOZNATO: " + ExMsg(ex)));
            }

            bool saved = DoSave(dgnFile, viewGroupColl, saveSettings, steps, ref errCount);
            extra["savedSettings"] = saved;
            SafeVerify(extra, "verifyAfter", steps, "verify.after");
            AddDisplayStyleReadback(extra, dgnFile);
            ShaAfter(extra, activePath, shaBefore, saved, steps);
            extra["touchedDgn"] = anyChange;

            status = anyChange && errCount == 0 ? "OK" : (anyChange ? "PARTIAL" : "ERROR");
            summary = status == "OK"
                ? "Nacin '" + mode + "' primijenjen na View " + viewNo + " (Fill=" + FillForMode(mode) + "); spremljeno=" + saved + "."
                : "Nacin '" + mode + "' nije primijenjen (" + errCount + " pogr.). Backup: " + backupPath;
            if (status != "OK") extra["incident"] = summary + ". Bez auto-rollbacka.";
            return extra;
        }

        // =====================================================================================
        //  Bijela radna pozadina (OBAVEZNA) - prateca View postavka u svih 8 pogleda
        // =====================================================================================
        private static Dictionary<string, object> AssertWhiteBaselineAllViews(object viewGroup, byte[] bg, List<object> steps)
        {
            var res = new Dictionary<string, object>();
            var per = new List<object>();
            int ok = 0;
            Type rgbType = FindType(RgbColorDefTypeName);
            for (int idx = 0; idx < 8; idx++)
            {
                var d = new Dictionary<string, object> { { "view", idx + 1 } };
                try
                {
                    object vi = Inv(viewGroup, "GetViewInformation", idx);
                    if (vi == null) { d["result"] = "NEPOZNATO: ViewInformation null"; per.Add(d); continue; }
                    object gi = Inv(vi, "GetGeometryInformation");
                    object vf = Prop(gi, "ViewFlags");
                    TrySet(vf, "OverrideBackground", true);
                    TrySet(vf, "Grid", false);
                    SetProp(gi, "ViewFlags", vf);
                    Inv(vi, "SetGeometryInformation", gi);
                    Inv(vi, "SetBackgroundColor", MakeRgb(rgbType, bg));
                    Inv(viewGroup, "SetViewInformation", vi, idx);
                    try { Inv(viewGroup, "SynchViewDisplay", idx, false, false, true); } catch { }
                    d["result"] = "OK";
                    ok++;
                }
                catch (Exception ex) { d["result"] = "GRESKA: " + ExMsg(ex); }
                per.Add(d);
            }
            res["backgroundRgb"] = new List<object> { (int)bg[0], (int)bg[1], (int)bg[2] };
            res["viewsOk"] = ok;
            res["perView"] = per;
            steps.Add(Step("whiteBackground", ok == 8 ? "OK" : "PARTIAL",
                "bijela pozadina RGB " + bg[0] + "," + bg[1] + "," + bg[2] + " + OverrideBackground ON u " + ok + "/8 pogleda"));
            return res;
        }

        // =====================================================================================
        //  ZAJEDNICKO: primjena stila + prateca View postavka + ocuvanje Top/grid/pozadine
        // =====================================================================================
        private static void ApplyStyleAndCompanion(
            Type dsm, object session, object viewGroup, object vi,
            object style, string mode, bool fill, byte[] bg,
            Dictionary<string, object> extra, List<object> steps, bool isView1)
        {
            var d = new Dictionary<string, object> { { "mode", mode }, { "companionFill", fill } };
            try
            {
                int viewNumber = ToInt(SafeGet(vi, "ViewNumber"), 0);
                int idx = viewNumber; // ViewInformation.ViewNumber je 0-baziran

                object dgnFile = TryCall(vi, "GetRootDgnFile");
                if (dgnFile is string) dgnFile = null;

                // 1) upisi stil u POHRANJENI ViewInfo: CopySettingsTo (persist + asocijacija) + ApplyDisplayStyleToView
                try { Inv(style, "CopySettingsTo", vi, dgnFile); d["copySettingsTo"] = "OK"; }
                catch (Exception exC) { d["copySettingsTo"] = "NEPOZNATO: " + ExMsg(exC); }
                object st = TryStatic(dsm, "ApplyDisplayStyleToView", new[] { style, vi });
                d["applyDisplayStyleToView"] = Convert.ToString(st, CultureInfo.InvariantCulture);

                // 2) prateca View postavka na POHRANJENOM ViewInfo (Fill, master Transparency, grid OFF,
                //    OverrideBackground ON + bijela). ApplyDisplayStyleToView zna iskljuciti override
                //    pozadine (stil ima OverrideBackgroundColor=false) - zato ovo IDE POSLIJE stila.
                CompanionToViewInfo(vi, fill, isView1, bg, d, "stored");
                Inv(viewGroup, "SetViewInformation", vi, idx);
                try { Inv(viewGroup, "SynchViewDisplay", idx, false, false, true); }
                catch (Exception exS) { d["synchWarn"] = ExMsg(exS); }

                // 3) PUSH na ZIVI aktivni viewport - pohranjeni ViewInfo ne prebojava vec otvoreni
                //    prozor; ApplyDisplayStyleToView na zivom je iskljucio bijelu, pa je ovdje vracamo.
                if (isView1)
                {
                    try
                    {
                        object vp = Inv(session, "GetActiveViewport");
                        if (vp != null && !(vp is string))
                        {
                            object lvi = Inv(vp, "GetViewInformation");
                            if (lvi != null && !(lvi is string))
                            {
                                TryStatic(dsm, "ApplyDisplayStyleToView", new[] { style, lvi });
                                CompanionToViewInfo(lvi, fill, true, bg, d, "live");
                                object sw = Inv(vp, "SynchWithViewInformation", true, true);
                                d["liveViewportSynch"] = Convert.ToString(sw, CultureInfo.InvariantCulture);
                            }
                            else d["liveViewport"] = "NEPOZNATO: Viewport.GetViewInformation() = " + lvi;
                        }
                        else d["liveViewport"] = "NEPOZNATO: Session.GetActiveViewport() = " + vp;
                    }
                    catch (Exception exL) { d["liveViewport"] = "NEPOZNATO: " + ExMsg(exL); }
                }

                // 4) View 1 jedini otvoren + aktivan (key-in, kao BEN-UI-002)
                if (isView1)
                {
                    var ki = new List<object>();
                    for (int v = 2; v <= 8; v++) DoKeyin(session, "VIEW OFF " + v, ki);
                    DoKeyin(session, "VIEW ON 1", ki);
                    d["keyins"] = ki;
                }

                d["result"] = "OK";
            }
            catch (Exception ex)
            {
                d["result"] = "GRESKA: " + ExMsg(ex);
            }
            extra["apply"] = d;
            steps.Add(Step("apply", d.ContainsKey("result") && "OK".Equals(d["result"]) ? "OK" : "PARTIAL",
                "stil '" + mode + "' + Fill=" + fill + " na " + (isView1 ? "View 1 (uz Top/grid/pozadinu + push na zivi viewport)" : "trazeni pogled")));
        }

        /// <summary>Prateca View postavka (Fill, master Transparency, grid OFF, bijela pozadina, Top za View 1)
        /// na danom ViewInformation objektu - koristi se i za pohranjeni i za zivi (Viewport) ViewInfo.</summary>
        private static void CompanionToViewInfo(object vi, bool fill, bool isView1, byte[] bg, Dictionary<string, object> d, string tag)
        {
            try
            {
                object gi = Inv(vi, "GetGeometryInformation");
                object vf = Prop(gi, "ViewFlags");
                SetProp(vf, "Fill", fill);
                TrySet(vf, "Transparency", true);
                TrySet(vf, "Grid", false);
                TrySet(vf, "OverrideBackground", true);
                if (isView1)
                {
                    object rot = GetTopRotation(FindType(ViewInfoTypeName), vi);
                    if (rot != null) TrySet(gi, "Rotation", rot);
                }
                SetProp(gi, "ViewFlags", vf);
                Inv(vi, "SetGeometryInformation", gi);
                Inv(vi, "SetBackgroundColor", MakeRgb(FindType(RgbColorDefTypeName), bg));
                d["companion_" + tag] = "OK (Fill=" + fill + ", OverrideBackground=ON, bg=" + bg[0] + "," + bg[1] + "," + bg[2] + ")";
            }
            catch (Exception ex) { d["companion_" + tag] = "GRESKA: " + ExMsg(ex); }
        }

        // =====================================================================================
        //  pomocno - guardrails / backup / save / verify (isti obrazac kao BEN-UI-002)
        // =====================================================================================
        private static Dictionary<string, object> NewExtra(string op)
        {
            var e = new Dictionary<string, object>
            {
                { "operation", op }, { "touchedDgn", false }, { "savedSettings", false },
                { "rule", "R5 - bez nagadanja; nepotvrdeno = NEPOZNATO/PRETPOSTAVKA; bez auto-rollbacka" },
                { "steps", new List<object>() },
                { "generatedAt", Now() }
            };
            return e;
        }

        private static Dictionary<string, object> CheckGuardrails(
            object session, Dictionary<string, object> target,
            out bool ok, out string why, out object dgnFile, out string activePath)
        {
            var d = new Dictionary<string, object>();
            ok = false; why = null; activePath = null;
            string wantWs = GetString(target, "workspace", DefWorkspace);
            string wantSet = GetString(target, "workset", DefWorkset);
            string wantName = GetString(target, "fileName", DefFileName);
            string forbid = GetString(target, "forbidFileName", DefForbidFileName);

            string wsName = GetConfigVar("_USTN_WORKSPACENAME");
            string wsSet = GetConfigVar("_USTN_WORKSETNAME");
            d["_USTN_WORKSPACENAME"] = wsName; d["_USTN_WORKSETNAME"] = wsSet;

            object adf = TryCall(session, "GetActiveDgnFile");
            if (adf is string) adf = null;
            dgnFile = adf;
            string fromDgn = null;
            if (dgnFile != null) { try { fromDgn = Convert.ToString(Inv(dgnFile, "GetFileName"), CultureInfo.InvariantCulture); } catch { } }
            string byName = Convert.ToString(TryCall(session, "GetActiveFileName"), CultureInfo.InvariantCulture);
            activePath = !string.IsNullOrEmpty(fromDgn) ? fromDgn : byName;
            d["activeFilePath"] = activePath;
            if (dgnFile == null) { why = "Nema aktivnog DGN-a (Session.GetActiveDgnFile() je null)."; return d; }

            bool ro = true; try { ro = ToBool(Prop(dgnFile, "IsReadOnly")); } catch { }
            d["isReadOnly"] = ro;
            string bn = null; try { bn = Path.GetFileName(activePath); } catch { }
            d["activeFileBaseName"] = bn;

            bool cWs = string.Equals(wsName, wantWs, StringComparison.OrdinalIgnoreCase);
            bool cSet = string.Equals(wsSet, wantSet, StringComparison.OrdinalIgnoreCase);
            bool cName = bn != null && string.Equals(bn, wantName, StringComparison.OrdinalIgnoreCase);
            bool cNotForbid = bn != null && !string.Equals(bn, forbid, StringComparison.OrdinalIgnoreCase);
            bool cNotSeed = !string.IsNullOrEmpty(activePath) && activePath.IndexOf(@"\Standards\Seed\", StringComparison.OrdinalIgnoreCase) < 0;
            bool cWr = !ro;
            d["checks"] = new Dictionary<string, object> {
                { "workspaceMatches", cWs }, { "worksetMatches", cSet }, { "fileNameMatches", cName },
                { "notForbiddenFileName", cNotForbid }, { "notSeedPath", cNotSeed }, { "fileWritable", cWr } };

            if (!cWs) { why = "WorkSpace nije '" + wantWs + "' (ocitano '" + wsName + "')."; return d; }
            if (!cSet) { why = "WorkSet nije '" + wantSet + "' (ocitano '" + wsSet + "')."; return d; }
            if (!cName) { why = "Aktivna datoteka nije '" + wantName + "' (ocitano '" + bn + "')."; return d; }
            if (!cNotForbid) { why = "Aktivna datoteka je zabranjeno ime '" + forbid + "'."; return d; }
            if (!cNotSeed) { why = "Aktivna putanja sadrzi '\\Standards\\Seed\\'."; return d; }
            if (!cWr) { why = "Aktivna datoteka je read-only."; return d; }
            ok = true; return d;
        }

        private static bool DoBackup(object dgnFile, string activePath, string taskId,
            Dictionary<string, object> extra, List<object> steps, out string backupPath, out string shaBefore)
        {
            backupPath = null; shaBefore = null;
            try
            {
                Directory.CreateDirectory(BackupRoot);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                backupPath = Path.Combine(BackupRoot,
                    Path.GetFileNameWithoutExtension(activePath) + "_" + stamp + "_" + Sanitize(taskId) + Path.GetExtension(activePath));
                File.Copy(activePath, backupPath, false);
                shaBefore = Sha256OfFile(backupPath);
                extra["backupPath"] = backupPath;
                extra["sha256Before"] = shaBefore;
                steps.Add(Step("backup", "OK", backupPath + "  sha256=" + shaBefore));
                return true;
            }
            catch (Exception ex)
            {
                steps.Add(Step("backup", "ABORTED", "NEPOZNATO: backup nije uspio: " + ExMsg(ex)));
                return false;
            }
        }

        private static bool DoSave(object dgnFile, object viewGroupColl, bool saveSettings, List<object> steps, ref int errCount)
        {
            if (!saveSettings) { steps.Add(Step("saveSettings", "SKIP", "parameters.saveSettings=false")); return false; }
            try
            {
                object sc = viewGroupColl != null ? Inv(viewGroupColl, "SaveChanges") : "(nema viewGroupColl)";
                object dsr = EnumVal(DgnSaveReasonTypeName, "SaveSettings");
                object pc = null;
                if (dsr != null)
                {
                    MethodInfo m = dgnFile.GetType().GetMethod("ProcessChanges", new[] { dsr.GetType() });
                    if (m != null) pc = m.Invoke(dgnFile, new object[] { dsr });
                }
                steps.Add(Step("saveSettings", "OK", "SaveChanges()=" + sc + "; ProcessChanges(SaveSettings)=" + (pc != null ? pc.ToString() : "(void)")));
                return true;
            }
            catch (Exception ex) { errCount++; steps.Add(Step("saveSettings", "ERROR", "NEPOZNATO: " + ExMsg(ex))); return false; }
        }

        private static void SafeVerify(Dictionary<string, object> extra, string key, List<object> steps, string stepName)
        {
            try
            {
                string s;
                var inv = InterfaceStateReader.Build(new Dictionary<string, object> {
                    { "views", new List<object>{1,2,3,4,5,6,7,8} },
                    { "includeDisplayStyles", true },
                    { "includeWorkspaceContext", false },
                    { "includeUiPanels", false } }, out s);
                extra[key] = CompactView(inv);
                steps.Add(Step(stepName, "OK", s));
            }
            catch (Exception ex)
            {
                extra[key] = "NEPOZNATO: " + ExMsg(ex);
                steps.Add(Step(stepName, "WARN", ExMsg(ex)));
            }
        }

        private static void AddDisplayStyleReadback(Dictionary<string, object> extra, object dgnFile)
        {
            try
            {
                Type dsm = FindType(DisplayStyleMgrTypeName);
                var d = new Dictionary<string, object>();
                foreach (string n in KnownModes) d[n] = DsExists(dsm, n, dgnFile);
                extra["displayStylesInFile"] = d;
            }
            catch (Exception ex) { extra["displayStylesInFile"] = "NEPOZNATO: " + ExMsg(ex); }
        }

        private static void ShaAfter(Dictionary<string, object> extra, string activePath, string shaBefore, bool saved, List<object> steps)
        {
            try
            {
                string a = Sha256OfFile(activePath);
                extra["sha256After"] = a;
                extra["sha256Changed"] = shaBefore != null && shaBefore != a;
                steps.Add(Step("sha256.after", "OK", a + (shaBefore == a ? "  (nepromijenjeno)" : "  (promijenjeno)")));
            }
            catch (Exception ex) { extra["sha256After"] = "NEPOZNATO: " + ExMsg(ex); steps.Add(Step("sha256.after", "WARN", ExMsg(ex))); }
        }

        // ------------------------------------------------------------------ DisplayStyle helpers

        private static bool DsExists(Type dsm, string name, object dgnFile)
        {
            try
            {
                object r = TryStatic(dsm, "DoesDisplayStyleExistInFile", new object[] { name, dgnFile });
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        private static object GetTopRotation(Type viType, object viInstance)
        {
            try
            {
                if (viType == null) return null;
                MethodInfo m = viType.GetMethod("GetStandardViewByName",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                if (m == null) return null;
                object[] args = { null, null, "Top" };
                m.Invoke(m.IsStatic ? null : viInstance, args);
                return args[0];
            }
            catch { return null; }
        }

        private static object MakeRgb(Type rgbType, byte[] rgb)
        {
            object o = Activator.CreateInstance(rgbType);
            SetProp(o, "R", rgb[0]); SetProp(o, "G", rgb[1]); SetProp(o, "B", rgb[2]);
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
                r["result"] = "poslano (PRETPOSTAVKA: izvrseno; potvrdjuje verifyAfter)";
            }
            catch (Exception ex) { r["result"] = "GRESKA: " + ExMsg(ex); }
            sink.Add(r);
        }

        private static Dictionary<string, object> CompactView(Dictionary<string, object> inv)
        {
            var o = new Dictionary<string, object>();
            try
            {
                var av = inv["activeViewport"] as Dictionary<string, object>;
                o["activeViewportNumber"] = av != null && av.ContainsKey("viewNumber") ? av["viewNumber"] : "NEPOZNATO";
            }
            catch { o["activeViewportNumber"] = "NEPOZNATO"; }
            var list = new List<object>();
            try
            {
                var vv = inv["views"] as Dictionary<string, object>;
                var items = vv != null ? vv["items"] as List<object> : null;
                if (items != null)
                    foreach (var it in items)
                    {
                        var v = it as Dictionary<string, object>; if (v == null) continue;
                        var c = new Dictionary<string, object>
                        {
                            { "view", Pick(v, "requestedNumber") },
                            { "isOpenOrDisplayed", Pick(v, "isOpenOrDisplayed") },
                            { "standardViewRotation", Pick(v, "standardViewRotation") },
                            { "isTop", Pick(v, "isTop") },
                            { "grid", Pick(v, "grid") },
                            { "fillDisplay", Pick(v, "fillDisplay") },
                            { "transparency", Pick(v, "transparency") },
                            { "overrideBackgroundFlag", Pick(v, "overrideBackgroundFlag") },
                            { "displayStyleName", Pick(v, "displayStyleName") }
                        };
                        var bg = v.ContainsKey("backgroundColor") ? v["backgroundColor"] as Dictionary<string, object> : null;
                        if (bg != null) c["backgroundRgb"] = new List<object> { bg.ContainsKey("R") ? bg["R"] : "?", bg.ContainsKey("G") ? bg["G"] : "?", bg.ContainsKey("B") ? bg["B"] : "?" };
                        list.Add(c);
                    }
            }
            catch (Exception ex) { o["viewsError"] = "NEPOZNATO: " + ExMsg(ex); }
            o["views"] = list;
            try { o["targetStylesExist"] = ((inv["displayStyles"] as Dictionary<string, object>) ?? new Dictionary<string, object>())["targetStylesExistInActiveFile"]; }
            catch { }
            return o;
        }

        // ------------------------------------------------------------------ reflection utils

        private static object GetSessionInstance()
        {
            try
            {
                Type t = FindType(SessionTypeName);
                PropertyInfo pi = t != null ? t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static) : null;
                return pi != null ? pi.GetValue(null, null) : null;
            }
            catch { return null; }
        }

        private static string GetConfigVar(string name)
        {
            try
            {
                Type cfg = FindType(ConfigMgrTypeName);
                if (cfg == null) return "NEPOZNATO: ConfigurationManager nije pronadjen";
                MethodInfo isDef = cfg.GetMethod("IsVariableDefined", new[] { typeof(string) });
                if (isDef != null && !Convert.ToBoolean(isDef.Invoke(null, new object[] { name })))
                    return "NEPOZNATO: '" + name + "' nije definirana";
                MethodInfo g = cfg.GetMethod("GetVariable", new[] { typeof(string) });
                return g != null ? g.Invoke(null, new object[] { name }) as string : null;
            }
            catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static object EnumVal(string typeName, string valueName)
        {
            try { Type t = FindType(typeName); return t != null && t.IsEnum ? Enum.Parse(t, valueName) : null; }
            catch { return null; }
        }

        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(fullName); if (t != null) return t; } catch { }
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
                foreach (MethodInfo c in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (c.Name == method && c.GetParameters().Length == args.Length) { mi = c; break; }
            if (mi == null) throw new Exception("metoda '" + method + "(" + args.Length + ")' nije pronadjena na " + target.GetType().FullName);
            return mi.Invoke(target, args);
        }

        private static object TryCall(object target, string method)
        {
            try { return Inv(target, method); } catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static void TryCall1(object target, string method, object arg)
        {
            try { Inv(target, method, arg); } catch { }
        }

        private static object TryStatic(Type t, string method, object[] args)
        {
            if (t == null) return "NEPOZNATO: tip null (" + method + ")";
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i] != null ? args[i].GetType() : typeof(object);
            MethodInfo mi = null;
            try { mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, types, null); } catch { }
            if (mi == null)
                foreach (MethodInfo c in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (c.Name == method && c.GetParameters().Length == args.Length) { mi = c; break; }
            if (mi == null) return "NEPOZNATO: staticka '" + method + "(" + args.Length + ")' nije pronadjena na " + t.FullName;
            try { return mi.Invoke(null, args); }
            catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static object Prop(object target, string name)
        {
            if (target == null) throw new Exception("cilj je null (get " + name + ")");
            PropertyInfo pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null) throw new Exception("property '" + name + "' nije pronadjen na " + target.GetType().FullName);
            return pi.GetValue(target, null);
        }

        private static object SafeGet(object target, string name)
        {
            try { return Prop(target, name); } catch (Exception ex) { return "NEPOZNATO: " + ExMsg(ex); }
        }

        private static void SetProp(object target, string name, object value)
        {
            if (target == null) throw new Exception("cilj je null (set " + name + ")");
            PropertyInfo pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null) throw new Exception("settable '" + name + "' nije pronadjen na " + target.GetType().FullName);
            object v = value;
            if (pi.PropertyType == typeof(byte) && !(value is byte)) v = Convert.ToByte(value, CultureInfo.InvariantCulture);
            else if (pi.PropertyType == typeof(double) && !(value is double)) v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            else if (pi.PropertyType.IsEnum && value != null && !pi.PropertyType.IsInstanceOfType(value))
                v = Enum.ToObject(pi.PropertyType, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            pi.SetValue(target, v, null);
        }

        private static void TrySet(object target, string name, object value)
        {
            try { SetProp(target, name, value); } catch { }
        }

        // ------------------------------------------------------------------ misc

        private static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static Dictionary<string, object> Step(string s, string st, string d)
        {
            return new Dictionary<string, object> { { "step", s }, { "status", st }, { "detail", d } };
        }

        private static string Now() { return DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture); }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "task";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append((char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
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
            bool b; return bool.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out b) && b;
        }

        private static int ToInt(object o, int fallback)
        {
            if (o is int) return (int)o;
            int n; return int.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        private static object Pick(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v : "NEPOZNATO";
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            object v; if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
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
                int r; if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            }
            return fallback;
        }

        private static int ParseIntV(Dictionary<string, object> d, string key, int fallback) { return ParseInt(d, key, fallback); }

        private static bool ParseBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool b; return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) ? b : fallback;
        }

        private static bool ParseBoolV(Dictionary<string, object> d, string key, bool fallback) { return ParseBool(d, key, fallback); }

        private static byte[] ParseRgb(Dictionary<string, object> d, string key)
        {
            var def = new byte[] { 255, 255, 255 };
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || !(v is IEnumerable) || v is string) return def;
            var nums = new List<byte>();
            foreach (object it in (IEnumerable)v)
            {
                int n;
                if (it != null && int.TryParse(Convert.ToString(it, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    nums.Add((byte)Math.Max(0, Math.Min(255, n)));
            }
            return nums.Count == 3 ? nums.ToArray() : def;
        }

        private static List<Dictionary<string, object>> ParseModes(Dictionary<string, object> p)
        {
            var outl = new List<Dictionary<string, object>>();
            object v;
            if (p != null && p.TryGetValue("modes", out v) && v is IEnumerable && !(v is string))
                foreach (object it in (IEnumerable)v)
                {
                    var m = it as Dictionary<string, object>;
                    if (m != null && !string.IsNullOrEmpty(GetString(m, "name", null))) outl.Add(m);
                }
            if (outl.Count == 0)
            {
                outl.Add(new Dictionary<string, object> { { "name", "ETAZ_PUNA_ISPUNA" }, { "fillEnabled", true }, { "transparencyPercent", 0 }, { "edgesVisible", true } });
                outl.Add(new Dictionary<string, object> { { "name", "ETAZ_TRANSPARENTNO" }, { "fillEnabled", true }, { "transparencyPercent", 70 }, { "edgesVisible", true } });
                outl.Add(new Dictionary<string, object> { { "name", "ETAZ_BEZ_ISPUNE" }, { "fillEnabled", false }, { "transparencyPercent", 0 }, { "edgesVisible", true } });
            }
            return outl;
        }

        private static List<object> ModesEcho(List<Dictionary<string, object>> modes)
        {
            var l = new List<object>();
            foreach (var m in modes)
                l.Add(new Dictionary<string, object>
                {
                    { "name", GetString(m, "name", null) },
                    { "fillEnabled", ParseBoolV(m, "fillEnabled", true) },
                    { "transparencyPercent", ParseIntV(m, "transparencyPercent", 0) },
                    { "edgesVisible", ParseBoolV(m, "edgesVisible", true) }
                });
            return l;
        }

        private static int CountCreated(List<object> ci)
        {
            int n = 0;
            foreach (object o in ci) { var d = o as Dictionary<string, object>; if (d != null && d.ContainsKey("created") && d["created"] is bool && (bool)d["created"]) n++; }
            return n;
        }
    }
}
