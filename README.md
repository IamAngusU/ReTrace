# ReTrace

Eigenständige Windows-App zum Wiederfinden und Sichern von Datei- und Bilddaten,
auch nachdem Dateien aus dem Papierkorb entfernt wurden. Native C#/WPF-Oberfläche
mit Dark-/Lightmode, lokalem Suchkern, mitgeliefertem PhotoRec, begrenzter
Bildvorschau, Mehrfachauswahl und geprüfter Kopier-Rettung. Kein TraceLedger,
Python, Konto, Internetdienst oder Kali-Start zur Benutzung erforderlich.

**Erste Vorschauversion: 0.1.0-alpha.1.** Eine gefundene Signatur ist noch keine
nachgewiesen gelöschte oder vollständige Datei. ReTrace unterscheidet vollständige
unterstützte Dateistrukturen, teilweise/beschädigte Bilder und ungeprüfte Funde.
Lesbare Vorschauen sind ein zusätzlicher Decoder-Test, kein Beweis für das
vollständige ursprüngliche Bild. Fehlende Bytes werden nicht erfunden.

## Benutzen

1. `ReTrace.exe` aus dem vollständigen portablen Ordner öffnen.
2. Ein vorhandenes Abbild auswählen. Alternativ **Laufwerk als Abbild sichern**:
   das gewählte Laufwerk wird ausschließlich gelesen; das neue Abbild muss auf
   einen anderen erkannten Datenträger passen. Administratorrechte werden nur
   für diesen gewählten Zugriff benötigt. Der Administrator-Button startet die
   eigene App nach einem ausdrücklichen Klick mit Windows-UAC neu.
3. **Bildsuche** untersucht JPEG-/PNG-Strukturen mit echtem Bytefortschritt.
   **Tiefensuche** verwendet das enthaltene, gepinnte Windows-PhotoRec 7.2.
   Standardmäßig werden Bilder gesucht; **Alle PhotoRec-Dateitypen** erweitert
   die Signatursuche unter anderem auf Dokumente und Archive. Allgemeine Funde
   können Fehlkandidaten sein und bleiben ungeprüft.
4. Ganze oder teilweise lesbare Bilder ansehen, filtern und auswählen.
5. **Auswahl sichern** erzeugt einen neuen Rettungsordner. Jede aufgeführte Kopie
   wird mit SHA-256 geprüft. Vorhandene Dateien oder Ordner werden nicht ersetzt.

Möglichst wenig auf das betroffene Laufwerk schreiben. Ein zweiter Buchstabe
auf derselben Platte ist kein zweiter Datenträger. Für die Aufnahme prüft
ReTrace einzelne Disk-Extents und unterstützte direkte Speichertypen; unbekannte,
virtuelle, RAID- und Storage-Spaces-Konstellationen werden nicht automatisch
freigegeben. Das ist keine Erkennung beliebiger physischer Speichertopologien.
Aktive Laufwerke können sich während des Lesens verändern. Die Aufnahme ist
kein VSS-/Dateisystem-Snapshot und repariert oder sperrt das Quelllaufwerk nicht.

## Was diese Version kann

- Bilder und Fragmente aus regulären Raw-Abbilddateien bis **16 TiB** suchen;
  ein normaler Ordner enthält keine freigegebenen Sektoren gelöschter Dateien.
- JPEG-Marker sowie PNG-Struktur/CRCs prüfen. Der integrierte Suchlauf arbeitet
  mit Streaming und Dateisperren; Quelldatei-ID, Größe und Änderungszeit bleiben
  an die Auswahl gebunden. Er erkennt keine beliebigen Dateifragment-Ketten.
- PhotoRec verwirft beschädigte Funddateien bei aktivierter Fragmentoption nicht
  automatisch. Alle nummerierten `recup.*`-Ordner werden beobachtet. Originale
  Namen und Verzeichnisse bleiben beim Carving meist unbekannt.
- Bildvorschau nur für unterstützte Bilddaten, mit Dateigrößen-/Pixelgrenzen und
  begrenzter Decoder-Auflösung. Dokumente und Programme werden nicht ausgeführt.
- Kopien aus Quellbereichen oder aus dem privaten PhotoRec-Lauf sichern; ein
  gespeicherter PhotoRec-Bestand lässt sich auch ohne ursprüngliche Abbilddatei
  erneut öffnen und exportieren, wenn die aufbewahrten Funddateien die
  SHA-256-Prüfung bestehen.
- Laufwerksabbilder als neue Datei mit Protokoll anlegen und den geschriebenen
  Bestand unabhängig erneut per SHA-256 prüfen. Abbruch oder Lesefehler behalten
  einen ausdrücklich unvollständigen Bestand mit bekannter Präfixlänge.

## Untersuchungen später fortsetzen

**Untersuchung speichern** legt einen neuen, begrenzten JSON-Bericht an.
**Öffnen** liest ihn nach einem Neustart wieder ein; **Sammlung** verwaltet
gespeicherte Fälle unter dem eigenen Benutzerprofil. Es wird dabei kein
Suchlauf wiederholt. Dateibindung und Fundbereiche werden geprüft, gespeicherte
PhotoRec-Funddateien zusätzlich vollständig gehasht. Für native Fundbereiche
erfolgt die erneute SHA-256-Prüfung vor dem Sichern der gewählten Bytes.

Fehlt die Quelle oder wurde sie geändert, bleibt der Bericht lesbar und die
Rettung wird gesperrt. **Schließen** leert die aktuelle Ansicht. Das Löschen
aus der Sammlung entfernt ausschließlich diese Metadaten; Abbilder, Funddateien
und bereits gerettete Kopien werden dadurch nicht gelöscht.

## Hauptrepo und CI

Die Produktquelle liegt bei [IamAngusU/ReTrace](https://github.com/IamAngusU/ReTrace).
[angusu-de/ReTrace](https://github.com/angusu-de/ReTrace) ist der separate
CI-Mirror desselben Maintainers. Der Windows-Workflow läuft ausschließlich
im Mirror. Beide erhalten denselben geprüften Arbeitsstand; der Mirror ist
kein eigenes Hauptrepo.

ReTrace bleibt eigenständig lauffähig. TraceLedger kann die registrierte native
App als Wiederherstellungskomponente starten und deren Untersuchungsberichte
als Belege importieren. Ein importierter Bericht ist eine dokumentierte
Aussage des Rettungslaufs, keine neue Prüfung der ursprünglichen Datenträger.

## Grenzen

Überschriebene oder per SSD-TRIM verworfene Inhalte, fehlende Verschlüsselungs-
schlüssel und beschädigte Zuordnungsdaten können Rettung verhindern. Carving
findet auch vorhandene oder eingebettete Kopien; der Löschstatus bleibt ohne
zusätzliche Dateisystembelege unbekannt. Ein teilweises JPEG kann lesbare Pixel,
nachfolgende fremde Bytes oder nur einen Fragmentanfang enthalten. ReTrace gibt
keinen erfundenen Prozentsatz „geretteter Originaldaten“ aus.

Integriert: maximal 10.000 Signaturprüfungen, 64 MiB pro vollständigem Kandidaten,
4 MiB pro Fragment und ein begrenztes aggregiertes Prüfvolumen. PhotoRec:
standardmäßig 1 Stunde Laufzeit, 10.000 Funddateien, 64 MiB je Fund und 4 GiB
Gesamtausgabe; diese Grenzen lassen sich in der Oberfläche ausdrücklich
anpassen. Fotos, Dokumente und alle unterstützten Typen sind getrennte Profile. Externe Ausgabegrenzen
werden beobachtet und können vor dem Prozessstopp kurz überschritten werden.
Für PhotoRec wird ein neuer, selbst gewählter Arbeitsordner benötigt; er enthält
Werkzeugkopien, Funddaten und den Bericht. Abbruch löscht diese Daten nicht.
Ein beendetes Profil garantiert weder alle Fundstellen noch Originalität.

## Entwickeln und prüfen

Windows 10/11 x64 und .NET-10-SDK; keine NuGet-Abhängigkeiten von Drittanbietern.

```powershell
dotnet run --project tests/ReTrace.Tests/ReTrace.Tests.csproj -c Release
dotnet build src/ReTrace.App/ReTrace.App.csproj -c Release
python scripts/fetch_components.py .tools
python scripts/publish.py D:\ReTrace\neuer-build --assets .tools
```

Das Build-Skript benötigt Python 3.11+; die fertige App nicht. Die beiden
Originalarchive im Komponentenordner müssen die Pins aus
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) erfüllen. Keine Binärarchive,
Datenträgerabbilder oder privaten Funddaten gehören in das Git-Repository.

Validierung: [docs/validation.md](docs/validation.md).

## Herkunft

ReTrace entstand aus dem Wiederherstellungs-Workflow von TraceLedger, ist aber
ein eigener C#-Suchkern und ein eigenes natives Produkt. Die Designrichtung
folgt dessen MIT-lizenziertem Desktop. PhotoRec/TestDisk bleibt ein unabhängiges
Open-Source-Werkzeug mit eigener GPL-Lizenz; siehe die Drittanbieterhinweise.
