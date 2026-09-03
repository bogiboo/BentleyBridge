/*----------------------------------------------------------------------------------------+
| InterfaceStateReader - operacija READ_INTERFACE_STATE za BRIDGE_RUN (BEN-UI-001).        |
|                                                                                        |
| STROGO READ-ONLY. Cita trenutno stanje OpenCities sucelja i pogleda PRIJE bilo kakve    |
| izmjene seeda, DGN-a ili korisnickih postavki. Ne poziva se nijedna Set / Write / Save  |
| mutirajuca metoda. Nema 'Save Settings'. Nema stvaranja Display Styleova.                |
|                                                                                        |
| Nacelo R5 (DECISIONS.md): sto se ne moze pouzdano ocitati potvrdenim Bentley API-jem    |
| oznacava se stringom koji pocinje s "NEPOZNATO" + razlog. Bez nagadanja.                 |
|                                                                                        |
| Lokalno potvrdeni izvori API-ja:                                                        |
|  - Bentley.MstnPlatformNET.Session (ustation.dll)  - reflektirano; entrypoint kao u     |
|    SDK primjeru DescribeElementExample.cs:66-78 (Session.Instance.GetActiveDgnFile()).  |
|  - Bentley.DgnPlatformNET.{ConfigurationManager,DisplayStyleManager,ViewGroup,          |
|    ViewGroupCollection,ViewInformation} (Bentley.DgnPlatformNET.dll) - reflektirano.     |
|  - ViewInformation.IsStandardViewRotation / StandardView.Top: SDK primjer               |
|    View\ViewInfoExample\ViewInfoExample.cpp:40-44.                                       |
|  - ViewFlags (grid, fill, ...): isti primjer, ViewInfoExample.cpp:56-104.                |
|  - _USTN_WORKSPACENAME / _USTN_WORKSETNAME / *ROOT / *DESCR: Bentleyjev shipani          |
|    config\msconfig.cfg (MapUltimate) linije 187-421.                                     |
+----------------------------------------------------------------------------------------*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace BridgeRun
{
    internal static class InterfaceStateReader
    {
        private const string SessionTypeName = "Bentley.MstnPlatformNET.Session";
        private const string ConfigMgrTypeName = "Bentley.DgnPlatformNET.ConfigurationManager";
        private const string DisplayStyleMgrTypeName = "Bentley.DgnPlatformNET.DisplayStyleManager";
        private const string SessionSymbolsTypeName = "Bentley.Internal.MstnPlatformNET.SessionSymbols";

        // Ciljni Display Styleovi (BEN-UI-001, potvrdjena koncepcija) - samo se PROVJERAVA postoje li.
        private static readonly string[] TargetDisplayStyles =
        {
            "ETAZ_PUNA_ISPUNA", "ETAZ_TRANSPARENTNO", "ETAZ_BEZ_ISPUNE"
        };

        // Bentley config varijable koje razrjesavaju WorkSpace/WorkSet kontekst.
        private static readonly string[] WorkspaceVars =
        {
            "MS", "_USTN_WORKSPACENAME", "_USTN_WORKSETNAME",
            "_USTN_WORKSPACEROOT", "_USTN_WORKSETROOT", "_USTN_WORKSETDESCR",
            "_USTN_WORKSPACESROOT", "_USTN_WORKSETSROOT",
            "_USTN_WORKSPACECFG", "_USTN_WORKSETCFG",
            "_USTN_HOMEPREFS", "_USTN_PREFNAMEBASE"
        };

        internal static Dictionary<string, object> Build(Dictionary<string, object> parameters, out string summary)
        {
            var inv = new Dictionary<string, object>();
            int[] views = ParseViews(parameters);
            bool includeDisplayStyles = ParseBool(parameters, "includeDisplayStyles", true);
            bool includeWorkspace = ParseBool(parameters, "includeWorkspaceContext", true);
            bool includeUiPanels = ParseBool(parameters, "includeUiPanels", true);

            inv["_meta"] = new Dictionary<string, object>
            {
                { "operation", "READ_INTERFACE_STATE" },
                { "readOnly", true },
                { "touchedDgn", false },
                { "savedSettings", false },
                { "rule", "R5 - bez nagadanja; nepotvrdeno = NEPOZNATO" },
                { "requestedViews", Box(views) },
                { "includeDisplayStyles", includeDisplayStyles },
                { "includeWorkspaceContext", includeWorkspace },
                { "includeUiPanels", includeUiPanels },
                { "generatedAt", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) }
            };

            object session = GetSessionInstance();

            inv["host"] = ReadHost(session);
            if (includeWorkspace) inv["workspace"] = ReadWorkspace(session);

            object activeFile = TryCall(session, "GetActiveDgnFile");
            object activeModel = TryCall(session, "GetActiveDgnModel");

            inv["activeFile"] = ReadActiveFile(session, activeFile);
            inv["activeModel"] = ReadActiveModel(session, activeModel);
            inv["activeViewport"] = ReadActiveViewport();
            inv["views"] = ReadViews(activeFile, views);
            if (includeDisplayStyles) inv["displayStyles"] = ReadDisplayStyles(activeFile);
            if (includeUiPanels) inv["uiPanels"] = ReadUiPanels(session);

            inv["classification"] = Classification();

            summary = BuildSummary(inv, views);
            return inv;
        }

        // ---------------------------------------------------------------- host

        private static Dictionary<string, object> ReadHost(object session)
        {
            var d = new Dictionary<string, object>();
            try
            {
                Process p = Process.GetCurrentProcess();
                d["processName"] = Nepoznato(() => p.ProcessName);
                try
                {
                    ProcessModule mm = p.MainModule;
                    d["mainModulePath"] = mm.FileName;
                    FileVersionInfo fvi = mm.FileVersionInfo;
                    d["productName"] = EmptyToNull(fvi.ProductName);
                    d["productVersion"] = EmptyToNull(fvi.ProductVersion);
                    d["fileVersion"] = EmptyToNull(fvi.FileVersion);
                    d["fileDescription"] = EmptyToNull(fvi.FileDescription);
                    d["companyName"] = EmptyToNull(fvi.CompanyName);
                }
                catch (Exception ex) { d["mainModule"] = "NEPOZNATO: " + Ex(ex); }
            }
            catch (Exception ex) { d["process"] = "NEPOZNATO: " + Ex(ex); }

            d["is64BitProcess"] = Environment.Is64BitProcess;
            d["clrVersion"] = Nepoznato(() => Environment.Version.ToString());
            d["osVersion"] = Nepoznato(() => Environment.OSVersion.ToString());
            d["session.Product"] = DumpShallow(TryGet(session, "Product"));
            d["session.GetActiveWorkflowName"] = TryCall(session, "GetActiveWorkflowName");
            d["source"] = "System.Diagnostics.Process.MainModule.FileVersionInfo (host EXE) + Bentley.MstnPlatformNET.Session";
            return d;
        }

        // ------------------------------------------------------------ workspace

        private static Dictionary<string, object> ReadWorkspace(object session)
        {
            var d = new Dictionary<string, object>();
            var vars = new Dictionary<string, object>();
            Type cfg = FindType(ConfigMgrTypeName);
            if (cfg == null)
            {
                d["configVariables"] = "NEPOZNATO: tip " + ConfigMgrTypeName + " nije pronadjen";
            }
            else
            {
                MethodInfo getVar = cfg.GetMethod("GetVariable", new[] { typeof(string) });
                MethodInfo isDef = cfg.GetMethod("IsVariableDefined", new[] { typeof(string) });
                foreach (string name in WorkspaceVars)
                {
                    try
                    {
                        bool defined = isDef != null && Convert.ToBoolean(isDef.Invoke(null, new object[] { name }));
                        if (!defined)
                        {
                            vars[name] = "NEPOZNATO: config varijabla nije definirana";
                            continue;
                        }
                        object val = getVar != null ? getVar.Invoke(null, new object[] { name }) : null;
                        vars[name] = EmptyToNull(val as string) ?? val;
                    }
                    catch (Exception ex) { vars[name] = "NEPOZNATO: " + Ex(ex); }
                }
                d["configVariables"] = vars;
            }

            d["isActiveWorkSetNoWorkSet"] = TryStaticCall(SessionTypeName, "IsActiveWorkSetNoWorkSet");
            d["isActiveWorkSetAssociatedToConnectProject"] =
                TryStaticCall(SessionTypeName, "IsActiveWorkSetAssociatedToCONNECTProjectAndDGNMode");
            d["source"] = "Bentley.DgnPlatformNET.ConfigurationManager.GetVariable; imena varijabli iz MapUltimate config\\msconfig.cfg";
            d["classification"] = "WorkSpace/WorkSet konfiguracija (CFG) - nije svojstvo DGN-a";
            return d;
        }

        // ----------------------------------------------------------- active file

        private static Dictionary<string, object> ReadActiveFile(object session, object activeFile)
        {
            var d = new Dictionary<string, object>();
            d["getActiveFileName"] = TryCall(session, "GetActiveFileName");
            d["getMasterFileName"] = TryCall(session, "GetMasterFileName");
            if (activeFile == null)
            {
                d["dgnFile"] = "NEPOZNATO: Session.GetActiveDgnFile() je null (nema aktivnog DGN-a?)";
                return d;
            }
            d["fileName"] = TryCall(activeFile, "GetFileName");
            d["isReadOnly"] = TryGet(activeFile, "IsReadOnly");
            d["isIModel"] = TryGet(activeFile, "IsIModel");
            d["hasPendingChanges"] = TryGet(activeFile, "HasPendingChanges");
            d["defaultModelId"] = SafeVal(TryGet(activeFile, "DefaultModelId"));
            d["lastActiveModelId"] = SafeVal(TryGet(activeFile, "LastActiveModelId"));
            d["lastSaveTimeUtc"] = SafeVal(TryGet(activeFile, "LastSaveTimeUtc"));
            d["version"] = ReadFileVersion(activeFile);
            d["classification"] = "Identitet DGN datoteke (DGN/seed). Ocitano bez izmjene.";
            return d;
        }

        private static object ReadFileVersion(object activeFile)
        {
            try
            {
                MethodInfo m = activeFile.GetType().GetMethod("GetVersion");
                if (m == null) return "NEPOZNATO: DgnFile.GetVersion nije pronadjen";
                object[] args = { null, null, null };
                m.Invoke(activeFile, args);
                return new Dictionary<string, object>
                {
                    { "format", SafeVal(args[0]) },
                    { "majorVersion", SafeVal(args[1]) },
                    { "minorVersion", SafeVal(args[2]) }
                };
            }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        // ---------------------------------------------------------- active model

        private static Dictionary<string, object> ReadActiveModel(object session, object activeModel)
        {
            var d = new Dictionary<string, object>();
            d["session.IsMasterFile3D"] = TryCall(session, "IsMasterFile3D");
            d["session.IsActiveModelReadOnly"] = TryCall(session, "IsActiveModelReadOnly");
            if (activeModel == null)
            {
                d["dgnModel"] = "NEPOZNATO: Session.GetActiveDgnModel() je null";
                return d;
            }
            d["modelName"] = TryGet(activeModel, "ModelName");
            d["modelId"] = SafeVal(TryCall(activeModel, "GetModelId"));
            d["is3d"] = TryGet(activeModel, "Is3d");
            d["treatAs3d"] = TryGet(activeModel, "TreatAs3d");
            d["is2dOr3d"] = Describe2dOr3d(TryGet(activeModel, "Is3d"));
            d["modelType"] = SafeVal(TryGet(activeModel, "ModelType"));
            d["isReadOnly"] = TryGet(activeModel, "IsReadOnly");
            d["isDictionaryModel"] = TryGet(activeModel, "IsDictionaryModel");
            d["classification"] = "Identitet i dimenzionalnost aktivnog modela (DGN/seed). Ocitano bez izmjene.";
            return d;
        }

        private static object Describe2dOr3d(object is3d)
        {
            if (is3d is bool) return ((bool)is3d) ? "3D" : "2D";
            return "NEPOZNATO: DgnModel.Is3d nije procitan";
        }

        // -------------------------------------------------------- active viewport

        private static Dictionary<string, object> ReadActiveViewport()
        {
            var d = new Dictionary<string, object>();
            try
            {
                Type st = FindType(SessionTypeName);
                MethodInfo m = st != null ? st.GetMethod("GetActiveViewport", BindingFlags.Public | BindingFlags.Static) : null;
                if (m == null) { d["error"] = "NEPOZNATO: Session.GetActiveViewport nije pronadjen"; return d; }
                object vp = m.Invoke(null, null);
                if (vp == null) { d["viewport"] = "NEPOZNATO: nema aktivnog viewporta (nijedan pogled nije aktivan/otvoren?)"; return d; }
                d["viewNumber"] = SafeVal(TryGet(vp, "ViewNumber"));
                d["isActive"] = TryGet(vp, "IsActive");
                d["isGridOn"] = TryGet(vp, "IsGridOn");
                d["isCameraOn"] = TryGet(vp, "IsCameraOn");
                d["is3dModel"] = TryGet(vp, "Is3dModel");
                d["isSheetView"] = TryGet(vp, "IsSheetView");
                d["backgroundColorRaw"] = SafeVal(TryGet(vp, "BackgroundColor"));
                d["viewName"] = TryCall(vp, "GetViewName");
                d["note"] = "Aktivni pogled = Session.GetActiveViewport().ViewNumber (0-bazirano kao mdlView).";
            }
            catch (Exception ex) { d["error"] = "NEPOZNATO: " + Ex(ex); }
            return d;
        }

        // ---------------------------------------------------------------- views

        private static object ReadViews(object activeFile, int[] views)
        {
            var list = new List<object>();
            object viewGroup = null;
            string vgSource;
            try
            {
                if (activeFile == null) throw new Exception("activeFile je null");
                object vgc = activeFile.GetType().GetMethod("GetViewGroups").Invoke(activeFile, null);
                viewGroup = vgc.GetType().GetMethod("GetActive").Invoke(vgc, null);
                vgSource = viewGroup != null
                    ? "DgnFile.GetViewGroups().GetActive()"
                    : "NEPOZNATO: aktivni ViewGroup je null";
            }
            catch (Exception ex) { vgSource = "NEPOZNATO: " + Ex(ex); }

            MethodInfo isViewDisplayed = FindStaticMethod(SessionSymbolsTypeName, "IsViewDisplayed", typeof(int));

            foreach (int viewNo in views)
            {
                var v = new Dictionary<string, object>();
                v["label"] = "View " + viewNo;
                v["requestedNumber"] = viewNo;
                int zeroBased = viewNo - 1;
                v["zeroBasedIndex"] = zeroBased;

                // Je li pogled otvoren/vidljiv (Bentley.Internal.* - NIJE sluzbeno podrzan API)
                if (isViewDisplayed != null && zeroBased >= 0)
                {
                    try
                    {
                        object r = isViewDisplayed.Invoke(null, new object[] { zeroBased });
                        v["isOpenOrDisplayed"] = r;
                        v["isOpenOrDisplayed_source"] =
                            "Bentley.Internal.MstnPlatformNET.SessionSymbols.IsViewDisplayed(int) - " +
                            "ekvivalent nativnom mdlView_isActive; INTERNAL API, oznaceno PRETPOSTAVKA";
                    }
                    catch (Exception ex) { v["isOpenOrDisplayed"] = "NEPOZNATO: " + Ex(ex); }
                }
                else
                {
                    v["isOpenOrDisplayed"] = "NEPOZNATO: nema potvrdjenog javnog API-ja za 'pogled otvoren/vidljiv' po broju";
                }

                object viewInfo = null;
                if (viewGroup != null)
                {
                    try
                    {
                        MethodInfo gvi = viewGroup.GetType().GetMethod("GetViewInformation", new[] { typeof(int) });
                        // SDK koristi 0-bazirani indeks (ViewInfoExample.cpp:352). Saljemo zeroBased i biljezimo stvarni ViewNumber.
                        viewInfo = gvi.Invoke(viewGroup, new object[] { zeroBased });
                    }
                    catch (Exception ex) { v["viewInformation"] = "NEPOZNATO: " + Ex(ex); }
                }

                if (viewInfo != null)
                {
                    v["viewInfo.ViewNumber"] = SafeVal(TryGet(viewInfo, "ViewNumber"));
                    v["heldByNamedView"] = TryGet(viewInfo, "IsNamed");
                    v["treatViewAs3D"] = TryCall(viewInfo, "TreatViewAs3D");
                    v["rootModelId"] = SafeVal(TryGet(viewInfo, "RootModelId"));

                    // Orijentacija / rotacija + je li standardni 'Top'
                    object rot = TryGet(viewInfo, "Rotation");
                    v["rotationMatrix"] = DumpShallow(rot);
                    string std = StandardViewName(viewInfo, rot);
                    v["standardViewRotation"] = std;
                    v["isTop"] = (std == "Top");

                    // ViewFlags: grid, fill i ostali View Attributes
                    object vf = TryGet(viewInfo, "ViewFlags");
                    var flags = DumpAllProps(vf);
                    v["viewFlags"] = flags;
                    v["grid"] = PickFlag(flags, "Grid");
                    v["fillDisplay"] = PickFlag(flags, "Fill");
                    v["camera"] = PickFlag(flags, "Camera");
                    v["lineWeights"] = PickFlag(flags, "LineWeights");
                    v["levelSymbologyOverrides"] = PickFlag(flags, "LevelSymbology");
                    v["transparency"] = PickFlag(flags, "Transparency");
                    v["patterns"] = PickFlag(flags, "Patterns");
                    v["constructions"] = PickFlag(flags, "Constructs");
                    v["renderMode"] = PickFlagRaw(flags, "RenderMode");
                    v["backgroundFlag"] = PickFlag(flags, "Background");
                    v["overrideBackgroundFlag"] = PickFlag(flags, "OverrideBackground");

                    // Radna boja pozadine + izvor
                    object bg = TryCall(viewInfo, "GetBackgroundColor");
                    v["backgroundColor"] = DumpShallow(bg);
                    v["backgroundColorSource"] = BackgroundSource(flags);

                    // Display Style dodijeljen pogledu
                    object ds = TryCall(viewInfo, "GetDisplayStyle");
                    if (ds == null)
                    {
                        v["displayStyleName"] = "NEPOZNATO: ViewInformation.GetDisplayStyle() je null";
                    }
                    else
                    {
                        v["displayStyleName"] = TryGet(ds, "Name");
                        v["displayStyle.isFromFile"] = TryGet(ds, "IsFromFile");
                        v["displayStyle.isFromHardCodedDefault"] = TryGet(ds, "IsFromHardCodedDefault");
                        v["displayStyle.displayMode"] = SafeVal(TryGet(ds, "DisplayMode"));
                    }
                }
                else if (!v.ContainsKey("viewInformation"))
                {
                    v["viewInformation"] = "NEPOZNATO: " + vgSource;
                }

                list.Add(v);
            }

            return new Dictionary<string, object>
            {
                { "viewGroupSource", vgSource },
                { "indexingNote", "SDK ViewInfoExample.cpp koristi 0-bazirani indeks pogleda; 'View N' u zahtjevu = indeks N-1. " +
                                  "Za svaki pogled prilozen je i stvarni ViewInformation.ViewNumber radi provjere." },
                { "viewAttributesSource", "Bentley.DgnPlatformNET.ViewInformation.ViewFlags / GetBackgroundColor / GetDisplayStyle" },
                { "standardViewSource", "ViewInformation.IsStandardViewRotation(rotation, true) -> StandardView (ViewInfoExample.cpp:40)" },
                { "items", list }
            };
        }

        private static string StandardViewName(object viewInfo, object rot)
        {
            try
            {
                if (rot == null) return "NEPOZNATO: ViewInformation.Rotation je null";
                MethodInfo m = viewInfo.GetType().GetMethod(
                    "IsStandardViewRotation", BindingFlags.Public | BindingFlags.Static);
                if (m == null) return "NEPOZNATO: IsStandardViewRotation nije pronadjen";
                object sv = m.Invoke(null, new object[] { rot, true });
                return sv != null ? sv.ToString() : "NEPOZNATO: null rezultat";
            }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        private static object BackgroundSource(Dictionary<string, object> flags)
        {
            object ovr = PickFlagRaw(flags, "OverrideBackground");
            object bgf = PickFlagRaw(flags, "Background");
            if (ovr is bool && (bool)ovr)
                return "Override pogleda (ViewFlags.OverrideBackground = true) - boja je View postavka";
            if (bgf is bool && (bool)bgf)
                return "ViewFlags.Background = true (prikaz pozadinske slike/boje aktivan); izvor boje: Display Style ili DGN - NEPOZNATO bez dubljeg citanja";
            return "Vjerojatno Display Style ili DGN default - NEPOZNATO tocan izvor bez dubljeg citanja (ovaj zadatak ne ide dublje)";
        }

        // --------------------------------------------------------- display styles

        private static Dictionary<string, object> ReadDisplayStyles(object activeFile)
        {
            var d = new Dictionary<string, object>();
            var probe = new Dictionary<string, object>();
            Type dsm = FindType(DisplayStyleMgrTypeName);
            MethodInfo exists = null;
            if (dsm != null)
            {
                foreach (MethodInfo mi in dsm.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "DoesDisplayStyleExistInFile" && mi.GetParameters().Length == 2)
                    {
                        exists = mi;
                        break;
                    }
                }
            }

            foreach (string name in TargetDisplayStyles)
            {
                if (exists == null || activeFile == null)
                {
                    probe[name] = "NEPOZNATO: DisplayStyleManager.DoesDisplayStyleExistInFile nedostupan ili nema aktivnog DGN-a";
                    continue;
                }
                try
                {
                    object r = exists.Invoke(null, new object[] { name, activeFile });
                    probe[name] = r;
                }
                catch (Exception ex) { probe[name] = "NEPOZNATO: " + Ex(ex); }
            }

            d["targetStylesExistInActiveFile"] = probe;
            d["targetStylesNote"] = "BEN-UI-001: stilovi se NE stvaraju i NE mijenjaju; ovo je samo provjera postoje li vec.";
            d["fullEnumeration"] =
                "NEPOZNATO: u Bentley.DgnPlatformNET nema potvrdjenog javnog enumeratora imenovanih Display Styleova. " +
                "Postoji Bentley.Internal.MstnPlatformNET.DisplayStyleEnumerator / DisplayStyleList (ustation.dll), " +
                "ali INTERNAL API nije potvrdjen kao stabilan pa se ne koristi u ovom read-only zadatku.";
            d["nativeBentleyWayToCreateLater"] =
                "Potvrdjeno lokalno (reflektirano): DisplayStyle.Clone(DgnFile,newName), " +
                "DisplayStyleManager.WriteDisplayStyleToFile(DisplayStyle,DgnFile), " +
                "DisplayStyleManager.CopyDisplayStyleToFile(name,srcDgnFile,dstDgnFile), " +
                "DisplayStyleManager.ApplyDisplayStyleToView(DisplayStyle,ViewInformation), " +
                "DisplayStyleManager.RenameDisplayStyleInFile(old,new,DgnFile). " +
                "SDK C++ pandan: examples\\Visualization\\DisplayStyleExample. Sve je za BUDUCI zadatak, ne sada.";
            d["source"] = "Bentley.DgnPlatformNET.DisplayStyleManager (reflektirano iz Bentley.DgnPlatformNET.dll)";
            d["classification"] = "Imenovani Display Styleovi zive u DGN-u / DGNLib-u (biblioteka stilova), ne u korisnickim preferencijama.";
            return d;
        }

        // -------------------------------------------------------------- ui panels

        private static Dictionary<string, object> ReadUiPanels(object session)
        {
            var d = new Dictionary<string, object>();
            var ribbonFlags = new Dictionary<string, object>();
            string[] props =
            {
                "IsTaskNavigationInRibbon", "AutoGenerateRibbonKeyTips", "ShowRibbonTaskPickerWithLabel",
                "ShowMainInTaskNavigation", "ShowNavToolsInTaskNavDialog", "UsingPositionMappingInRibbon",
                "ShouldOpenMinimizedRibbonOnMouseHit", "IsReadyForUIProcessing"
            };
            foreach (string p in props) ribbonFlags[p] = TryGet(session, p);
            d["sessionRibbonFlags"] = ribbonFlags;

            d["ribbonLayoutAndDockState"] =
                "NEPOZNATO: nema potvrdjenog javnog API-ja (Bentley.MstnPlatformNET) za layout Ribbona i " +
                "stanje otvorenih/dockiranih panela. Bentley.MicroStation.Ribbon.dll postoji ali nije analiziran u ovom read-only zadatku.";
            d["navigatorPanel"] =
                "NEPOZNATO: panel koji zahtjev zove 'Navigator' nije razrijesen nijednim potvrdjenim API-jem ni konfiguracijskim izvorom.";
            d["openDockedPanels"] =
                "NEPOZNATO: popis otvorenih/dockiranih panela nije dostupan potvrdjenim API-jem u opsegu ovog zadatka.";
            d["source"] = "Bentley.MstnPlatformNET.Session (samo pojedinacne ribbon zastavice)";
            d["classification"] = "Layout Ribbona/panela = korisnicke preferencije GUI-ja i/ili WorkSpace konfiguracija, nije svojstvo DGN-a.";
            return d;
        }

        // ------------------------------------------------------------ classification

        private static Dictionary<string, object> Classification()
        {
            return new Dictionary<string, object>
            {
                { "host naziv/verzija", "Instalacija host aplikacije (EXE / Program Files)" },
                { "_USTN_WORKSPACENAME / _USTN_WORKSETNAME / *ROOT / *DESCR", "WorkSpace/WorkSet konfiguracija (CFG lanac)" },
                { "aktivna DGN datoteka i model, 2D/3D", "Svojstvo DGN-a / seeda" },
                { "pogled otvoren/vidljiv, aktivni pogled", "Stanje sesije/UI-a (sprema se u DGN ViewGroup preko 'Save Settings')" },
                { "orijentacija/rotacija pogleda, Top", "View postavka (ViewInfo u DGN ViewGroup)" },
                { "grid ukljucen/iskljucen po pogledu", "View postavka (ViewFlags), per-pogled" },
                { "prikaz ispune i ostali View Attributes", "View postavka (ViewFlags), per-pogled" },
                { "Display Style dodijeljen pogledu", "View postavka referencira imenovani stil iz DGN/DGNLib" },
                { "dostupni imenovani Display Styleovi", "DGN / DGNLib (biblioteka stilova)" },
                { "radna boja pozadine", "View override (ako je OverrideBackground) inace Display Style/DGN" },
                { "Ribbon / dockirani paneli / Navigator", "Korisnicke preferencije GUI-ja i/ili WorkSpace konfiguracija" }
            };
        }

        // ------------------------------------------------------------------ summary

        private static string BuildSummary(Dictionary<string, object> inv, int[] views)
        {
            int nepoznato = CountNepoznato(inv);
            int viewsRead = 0;
            try
            {
                var vv = inv["views"] as Dictionary<string, object>;
                var items = vv != null ? vv["items"] as List<object> : null;
                if (items != null) viewsRead = items.Count;
            }
            catch { }
            return "Pogleda obradjeno: " + viewsRead + "/" + views.Length +
                   "; NEPOZNATO stavki: " + nepoznato + ".";
        }

        private static int CountNepoznato(object o)
        {
            int n = 0;
            if (o is string)
            {
                if (((string)o).StartsWith("NEPOZNATO")) n++;
            }
            else if (o is IDictionary)
            {
                foreach (object val in ((IDictionary)o).Values) n += CountNepoznato(val);
            }
            else if (o is IEnumerable && !(o is string))
            {
                foreach (object item in (IEnumerable)o) n += CountNepoznato(item);
            }
            return n;
        }

        // ------------------------------------------------------- reflection helpers

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

        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = a.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo FindStaticMethod(string typeName, string method, params Type[] argTypes)
        {
            try
            {
                Type t = FindType(typeName);
                if (t == null) return null;
                return t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, argTypes, null);
            }
            catch { return null; }
        }

        private static object TryGet(object target, string member)
        {
            if (target == null) return "NEPOZNATO: cilj je null (" + member + ")";
            try
            {
                Type t = target.GetType();
                PropertyInfo pi = t.GetProperty(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (pi != null) return pi.GetValue(pi.GetGetMethod(true).IsStatic ? null : target, null);
                FieldInfo fi = t.GetField(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                if (fi != null) return fi.GetValue(fi.IsStatic ? null : target);
                return "NEPOZNATO: clan '" + member + "' nije pronadjen na " + t.FullName;
            }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        private static object TryCall(object target, string method)
        {
            if (target == null) return "NEPOZNATO: cilj je null (" + method + ")";
            try
            {
                MethodInfo mi = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) return "NEPOZNATO: metoda '" + method + "()' nije pronadjena na " + target.GetType().FullName;
                return mi.Invoke(target, null);
            }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        private static object TryStaticCall(string typeName, string method)
        {
            try
            {
                Type t = FindType(typeName);
                if (t == null) return "NEPOZNATO: tip " + typeName + " nije pronadjen";
                MethodInfo mi = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mi == null) return "NEPOZNATO: staticka metoda '" + method + "()' nije pronadjena na " + typeName;
                return mi.Invoke(null, null);
            }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        /// <summary>Sve javne instancne property vrijednosti objekta u dictionary (npr. ViewFlags).</summary>
        private static Dictionary<string, object> DumpAllProps(object o)
        {
            var d = new Dictionary<string, object>();
            if (o == null) { d["_error"] = "NEPOZNATO: objekt je null"; return d; }
            d["_type"] = o.GetType().FullName;
            foreach (PropertyInfo pi in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                try { d[pi.Name] = SafeVal(pi.GetValue(o, null)); }
                catch (Exception ex) { d[pi.Name] = "NEPOZNATO: " + Ex(ex); }
            }
            return d;
        }

        /// <summary>Plitki ispis strukture (polja + property-ji) kroz SafeVal - za DMatrix3d, RgbColorDef, ProductID.</summary>
        private static object DumpShallow(object o)
        {
            if (o == null) return "NEPOZNATO: null";
            Type t = o.GetType();
            if (t.IsPrimitive || o is string || o is decimal || t.IsEnum) return SafeVal(o);
            var d = new Dictionary<string, object> { { "_type", t.FullName }, { "_toString", SafeString(o) } };
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { d[fi.Name] = SafeVal(fi.GetValue(o)); }
                catch (Exception ex) { d[fi.Name] = "NEPOZNATO: " + Ex(ex); }
            }
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                try { d[pi.Name] = SafeVal(pi.GetValue(o, null)); }
                catch (Exception ex) { d[pi.Name] = "NEPOZNATO: " + Ex(ex); }
            }
            return d;
        }

        private static object SafeVal(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal) return v;
            if (t.IsEnum) return v.ToString();
            if (v is DateTime) return ((DateTime)v).ToString("o", CultureInfo.InvariantCulture);
            // Nested vrijednosni tip (npr. DVec3d unutar DMatrix3d): jedan plitki sloj brojeva.
            if (t.IsValueType)
            {
                var d = new Dictionary<string, object> { { "_type", t.FullName } };
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        object fv = fi.GetValue(v);
                        d[fi.Name] = (fv != null && (fv.GetType().IsPrimitive || fv is string)) ? fv : SafeString(fv);
                    }
                    catch { d[fi.Name] = "NEPOZNATO"; }
                }
                if (d.Count == 1) d["_toString"] = SafeString(v);
                return d;
            }
            return SafeString(v);
        }

        private static string SafeString(object o)
        {
            try { return o != null ? o.ToString() : null; }
            catch { return "NEPOZNATO: ToString() bacio iznimku"; }
        }

        private static object PickFlag(Dictionary<string, object> flags, string name)
        {
            object v = PickFlagRaw(flags, name);
            if (v is bool) return ((bool)v) ? "ON" : "OFF";
            return v;
        }

        private static object PickFlagRaw(Dictionary<string, object> flags, string name)
        {
            if (flags == null) return "NEPOZNATO: ViewFlags nije procitan";
            object v;
            if (flags.TryGetValue(name, out v)) return v;
            return "NEPOZNATO: ViewFlags." + name + " nije pronadjen";
        }

        private static object Nepoznato(Func<object> f)
        {
            try { return f() ?? "NEPOZNATO: null"; }
            catch (Exception ex) { return "NEPOZNATO: " + Ex(ex); }
        }

        private static string Ex(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string EmptyToNull(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ---------------------------------------------------------- param parsing

        private static int[] ParseViews(Dictionary<string, object> p)
        {
            var def = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            if (p == null) return def;
            object v;
            if (!p.TryGetValue("views", out v) || v == null) return def;
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
            return result.Count > 0 ? result.ToArray() : def;
        }

        private static bool ParseBool(Dictionary<string, object> p, string key, bool fallback)
        {
            if (p == null) return fallback;
            object v;
            if (!p.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) ? b : fallback;
        }

        private static List<object> Box(int[] a)
        {
            var l = new List<object>();
            foreach (int i in a) l.Add(i);
            return l;
        }
    }
}
