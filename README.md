# BentleyBridge

Generički managed C# AddIn za OpenCities Map Ultimate 2025. Bridge se učitava u Bentleyjev proces i na ručnu naredbu `BRIDGE_RUN` čita strogo definirane JSON naloge.

## Granica sustava

`BRIDGE_RUN.dll` je pomoćni infrastrukturni alat. Nije aplikacija Etažiranje, nije njezin poslovni modul i ne smije sadržavati poslovna pravila Etažiranja. Aplikacija ostaje u zasebnom repozitoriju `bogiboo/Etaziranje`; Bridge smije primati samo generičke, provjerene Bentley operacije kroz JSON.

## Kanonske lokalne putanje

- AddIn: `C:\AITools\BentleyBridge\runtime\current\BRIDGE_RUN.dll`
- Hash AddIna: `C:\AITools\BentleyBridge\runtime\current\BRIDGE_RUN.dll.sha256`
- Ulazni nalog: `C:\AITools\BentleyBridge\runtime\inbox\current-task.json`
- Rezultati: `C:\AITools\BentleyBridge\runtime\results\`
- Logovi: `C:\AITools\BentleyBridge\runtime\logs\`

`BRIDGE_RUN.dll` i njegov SHA-256 zapis verzioniraju se u repozitoriju. Ostali izvedbeni sadržaji mape `runtime` ostaju lokalni i izvan Gita.

## Načelo rada

1. Claude ili ChatGPT pripremi izvorni kod ili JSON nalog.
2. Build skripta izgradi i isporuči `BRIDGE_RUN.dll` izravno u `runtime\current`.
3. WorkSpace `ETAZIRANJE_RAZVOJ` uključuje `config\BentleyBridge.cfg`.
4. Korisnik u OpenCitiesu pokrene `BRIDGE_RUN`.
5. Bridge provjeri i izvrši samo poznatu operaciju te zapiše rezultat.

Prvi razvojni korak je Hello World bez pristupa DGN-u. Vrijednost `BRIDGE_RUN` u `MS_DGNAPPS` i CommandTableu mora se potvrditi prvim kontroliranim testom.

## Operacije

| `operation` | Mutira DGN | Opis |
|---|---|---|
| `SHOW_MESSAGE` | ne | prikaz poruke (Message Center + dijalog) |
| `READ_INTERFACE_STATE` | ne | read-only inventura sučelja i pogleda (idempotentno ponovljivo) |
| `APPLY_INTERFACE_BASELINE` | da | primjena osnovnog View/UI profila na aktivni `Test_2.dgn` |

`APPLY_INTERFACE_BASELINE` (BEN-UI-002) mijenja **samo** View/UI postavke: rotacija View 1 na `Top`, grid OFF u View 1–8, bijela radna pozadina RGB 255,255,255 u View 1–8, otvoren samo View 1, `Save Settings`. Ne dira geometriju, modele, razine, reference, rastere, elemente, georeferenciranje, Display Styleove ni CFG. Prije izmjene: stroga provjera WorkSpace/WorkSet/imena datoteke/read-only + vremenski označen backup u `runtime\backups\BEN-UI-002\` s SHA-256 prije/poslije. Bez automatskog rollbacka; djelomične promjene i iznimke se zapisuju u rezultat. Idempotentno po `taskId`.
