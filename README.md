# BentleyBridge

GeneriÄŤki managed C# AddIn za OpenCities Map Ultimate 2025. Bridge se uÄŤitava u Bentleyjev proces i na ruÄŤnu naredbu `ETAZ RUN` ÄŤita strogo definirane JSON naloge.

## Stalne lokalne putanje

- AddIn: `C:\AITools\BentleyBridge\runtime\current\BentleyBridge.dll`
- Ulazni nalog: `C:\AITools\BentleyBridge\runtime\inbox\current-task.json`
- Rezultati: `C:\AITools\BentleyBridge\runtime\results\`
- Logovi: `C:\AITools\BentleyBridge\runtime\logs\`

Izvedbeni sadrĹľaj mape `runtime` ne sprema se u Git. Izvorni kod, JSON shema, primjeri, konfiguracija i build skripte jesu verzionirani.

## NaÄŤelo rada

1. Claude ili ChatGPT pripremi izvorni kod ili JSON nalog.
2. Build skripta isporuÄŤi DLL izravno u `runtime\current`.
3. WorkSpace `ETAZIRANJE_RAZVOJ` ukljuÄŤuje `config\BentleyBridge.cfg`.
4. Korisnik u OpenCitiesu pokrene `ETAZ RUN`.
5. Bridge provjeri i izvrĹˇi samo poznatu operaciju te zapiĹˇe rezultat.

Prvi razvojni korak je Hello World bez pristupa DGN-u. Identifikator u `MS_DGNAPPS` mora se potvrditi prvim kontroliranim testom.