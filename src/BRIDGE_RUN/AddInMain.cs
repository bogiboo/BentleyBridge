/*----------------------------------------------------------------------------------------+
| BRIDGE_RUN - genericki pomocni Bentley AddIn (BentleyBridge).                            |
| NIJE aplikacija Etaziranje niti njezin poslovni modul. Bez poslovnih pravila Etaziranja.|
|                                                                                        |
| Obrazac entrypointa preuzet iz sluzbenog SDK primjera:                                  |
|   C:\Users\bozidar\BentleyDev\DescribeElementExample\DescribeElementExample.cs:18-19    |
|   [Bentley.MstnPlatformNET.AddInAttribute(MdlTaskID = "...")]                           |
|   public sealed class ... : Bentley.MstnPlatformNET.AddIn                               |
+----------------------------------------------------------------------------------------*/

namespace BridgeRun
{
    /// <summary>
    /// Ucitava se automatski kad WorkSpace ETAZIRANJE_RAZVOJ preko
    /// config\BentleyBridge.cfg postavi MS_DGNAPPS > BRIDGE_RUN.
    /// Pri ucitavanju NE izvrsava nijedan nalog. Izvrsenje pocinje tek key-in naredbom.
    /// </summary>
    [Bentley.MstnPlatformNET.AddInAttribute(MdlTaskID = "BRIDGE_RUN")]
    public sealed class AddInMain : Bentley.MstnPlatformNET.AddIn
    {
        private static AddInMain s_instance;

        public AddInMain(System.IntPtr mdlDesc) : base(mdlDesc)
        {
            s_instance = this;
        }

        /// <summary>Obavezna metoda AddIn klase. Namjerno ne radi nista.</summary>
        protected override int Run(string[] commandLine)
        {
            return 0;
        }

        internal static AddInMain Instance()
        {
            return s_instance;
        }
    }
}
