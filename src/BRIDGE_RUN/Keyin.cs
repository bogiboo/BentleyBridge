/*----------------------------------------------------------------------------------------+
| Key-in -> funkcija mapiranje. Obrazac iz SDK primjera                                    |
| C:\Program Files\Bentley\MicroStation2025SDK\examples\Elements\ManagedFenceExample\      |
|   Keyin.cs  +  commands.xml (KeyinHandler Function="ManagedFenceExample.Keyin.Cmd...").  |
+----------------------------------------------------------------------------------------*/

namespace BridgeRun
{
    public static class Keyin
    {
        /// <summary>
        /// Registrira se u commands.xml na "BRIDGE_RUN" (i alias "BRIDGE RUN").
        /// Poziva se samo na korisnikovu naredbu, nikad automatski.
        /// </summary>
        public static void Run(string unparsed)
        {
            BridgeRunner.Execute();
        }
    }
}
