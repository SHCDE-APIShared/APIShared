# Geprüfte native Funktionsnamen

Stand: 25.10.2026. Die maßgeblichen Einträge stehen in `functions.json`.
Dies sind portable Wissensmetadaten für Offline-Analyse und Ghidra, keine
Laufzeit-Aufruf- oder Adressauflösungs-API. Die C#-Feldkataloge bleiben unverändert.

Native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
VA = `0x180000000` + RVA. Kennungen identifizieren Funktionen; neue DLL-Versionen
benötigen eine neue geprüfte Bindung. Namen sind rekonstruierte Rollenbezeichnungen,
keine wiedergefundenen Original-PDB-Namen. Namenssicherheit und ABI-Sicherheit bleiben
getrennt. Aus einer bestätigten Rolle folgt keine Freigabe zum direkten Aufruf.

| Kennung | RVA | Name | Aussage |
|---|---|---|---|
| `nkfn-000001` | `0xCEB10` | `c_game_ai_get_buy_price` | Vorhandener bestätigter Kaufpreis-Helfer; erneut geprüft. |
| `nkfn-000002` | `0xCEB90` | `c_game_ai_get_sell_price` | Vorhandener bestätigter Verkaufspreis-Helfer; erneut geprüft. |
| `nkfn-000003` | `0xC07C0` | `c_game_building_get_type_height_offset` | Neu: typabhängiger Höhenaufschlag, kein absoluter Gebäudehöhenwert. |
| `nkfn-000004` | `0x184FD0` | `c_game_unit_update_standing_height` | Neu: aktualisiert Höhenzustand, kein nebenwirkungsfreier Getter. |

## Erster Durchgang: vollständiger statischer Rollen-Audit

Die Preis-Helfer lesen ihre jeweilige Int32-Tabellenspalte bei Manager+1817B8
bzw. +1817BC, indiziert durch den dritten Parameter mit Schrittweite acht.
Sie dividieren vor der Multiplikation durch fünf, mit Rundung gegen null, und
geben das mit der Menge multiplizierte Int32-Ergebnis zurück. Der Spielerparameter
wird vom Helfer nicht verwendet. Überlauf und negative Rohwerte werden nicht
geklemmt. Die vorhandenen Curated-Einträge, Aufrufstellen und ABI-Belege bleiben erhalten.

C07C0 ist ein 112-Byte-Blatt ohne Aufrufe und Speicher-Schreibzugriffe. Es liest
das Typ-WORD bei Gebäudemanager + buildingId*32C + 12E. Die vollständige Switch-
Abbildung lautet: 40→64, 41→92, 42→190, 45/46→128, 69→118, 74→296,
75→148, 76→180, 77/78→192; alle anderen Typwörter liefern null. Das ist der
Höhenaufschlag zum separat gespeicherten Gebäude-Bezugspunkt. Die zwanzig direkten
Aufrufer umfassen Renderer, Tile-/Pathing-Helfer und den Standhöhen-Updater.
Es gibt keine eigene Gültigkeitsprüfung für Manager oder ID. Die genauen nativen
Quellsprachentypen des Rückgabewerts werden nicht als neue ABI behauptet.

184FD0 aktualisiert einen Unit-Slot bei unitId*490. Manager+712/+714 entsprechen
den öffentlichen GameUnit-Feldern +B6/+B8 nach Abzug des Präfixes 65C. Die Funktion
setzt zunächst die Korrektur +714 und das zusätzliche WORD +6DC zurück. Sie liest
den aktuellen Tile-Index +72C, dessen Properties und Gebäude-ID. Alle Pfade wurden
geprüft: gewöhnlicher Boden und Rückkehr aus einem Übergangszustand, niedrige
Zugbrücke, Treppe +12, gebäudegestützte Mauer +40, abgesenkter Sonderzustand mit
der vorzeichenbehafteten Schwelle 98, einfaches Gebäude +2 und erhöhte Gebäude.
Priorität ist Treppe, Mauer, einfaches Gebäude, erhöhtes Gebäude. Ein maskiertes
Flag ohne eigenen Zweig behält die zurückgesetzte Korrektur bei.

Der erhöhte Pfad verwendet C07C0, sofern Zustandsflag und Einheitentyp dies zulassen,
und C0700 für einen zusätzlichen typabhängigen Wert. C0700 liest ein DWORD bei
Gebäudemanager+id*32C+10C und liefert nur bei dessen Nichtnullwert einen begrenzten
negativen Typaufschlag. Anschließend wird der Gebäude-Bezugspunkt +148 übernommen.
Am gemeinsamen Ende kommt ein separates vorzeichenbehaftetes WORD aus der global
ausgewählten Unit hinzu. Diese globale Auswahl ist nicht mit dem Parameter-ID-
Wert gleichzusetzen. Der Übergangsmerker +A5B kann gesetzt oder gelöscht werden.
Alle anderen Bytes des Unit-Managers bleiben durch diese Funktion unverändert.
Die beiden direkten Aufrufer 180A80 und 182B00 führen sie nach Platzierung bzw.
Bewegungs-/Tick-Normalisierung aus; spätere Bewegungs- oder Einheitentyp-Pfade
können die Höhenwerte erneut ändern. Deshalb ist der Name ausdrücklich ein Updater.

## Zweiter Durchgang: unabhängige Prüfung der Originalbytes

Die vollständigen Funktionsbytes wurden erneut gegen den aktuellen DLL-Hash und
die gespeicherten Funktionshashes geprüft und unabhängig disassembliert. Ein
Offline-Prüfer führt kopierte Originalinstruktionen einschließlich ihrer Sprung-
tabellen in eigenen Speicherpuffern aus. Er verändert weder das Spiel noch dessen
geladene DLL und installiert keine Hooks.

115.546 Fälle prüfen sämtliche Typ-WORDs, vorzeichenbehaftete Preisarithmetik,
alle Kombinationen der relevanten Standhöhen-Bits, beide Zustandsflags, Gebäude-
Sonderfälle, die Schwelle 97/98, zwei Unit-IDs und die separate globale Addition.
Beim Standhöhen-Updater wird jeweils der gesamte private Managerpuffer verglichen,
damit zusätzliche Schreibzugriffe auffallen. Ergebnis: keine Abweichung.

Diese Ausführung bestätigt die begrenzten Maschinenverträge und Namen. Sie ist
kein Spiel-/Multiplayer-Test und beweist weder die Gültigkeit fremder Pointer
noch die semantische Bedeutung sämtlicher gelesener Hilfsfelder. Prüfprogramm
und kompakter Ergebnisnachweis werden mit den Einträgen per SHA-256 referenziert.
