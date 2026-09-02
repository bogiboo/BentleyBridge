# BentleyBridge

Generički managed C# AddIn za OpenCities Map Ultimate 2025. Bridge se učitava u Bentleyjev proces i na ručnu naredbu `BRIDGE_RUN` čita strogo definirane JSON naloge.

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
