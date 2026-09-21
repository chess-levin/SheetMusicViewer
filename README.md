# Notenbetrachter (Prototyp)

Ein einfacher Windows-11-PDF-Betrachter für zwei aufeinanderfolgende Notenseiten.

## Starten

1. Das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installieren.
2. In diesem Ordner ausführen: `dotnet run --project .`.
3. Mit **PDF öffnen…** eine Noten-PDF laden.

Nach der ersten geöffneten Datei zeigt **PDF öffnen…** die bis zu acht zuletzt verwendeten PDFs zur direkten Auswahl an. Ist eine ausgewählte Datei nicht mehr vorhanden, wird sie aus der Liste entfernt und ein Hinweis direkt unter der Liste angezeigt.

## PDF-Grenzen

Eine PDF darf höchstens **100 MiB (104.857.600 Bytes)** groß sein. Die Größe wird geprüft, bevor die Datei für die Windows-PDF-API in den Arbeitsspeicher kopiert wird. Zu große Dateien werden abgelehnt; die Fehlermeldung erscheint unter der Liste der zuletzt geöffneten PDFs. Zusätzlich sind Dokumente auf 50 Seiten und die Darstellung auf 4.096 Pixel je Kante bzw. 12.000.000 Pixel begrenzt.

## Bedienung

| Taste | Aktion |
| --- | --- |
| `→` | Viewport eine Seite nach vorn (`1|2` → `2|3`) |
| `←` | Viewport eine Seite zurück |
| Mausrad oder `+` / `-` | Zoom; der Zielwert wird mittig als kurze Einblendung angezeigt |
| `F11` | randloser Vollbildmodus |
| `ESC` | Vollbildmodus verlassen |
| `ENTF` in „Zuletzt geöffnete PDFs“ | Entfernt den Listeneintrag, nicht die PDF-Datei |

Die Tasten und der letzte Zoomwert liegen nach dem ersten Zoom-Vorgang in `%AppData%\\SheetMusicViewer\\settings.json` und sind dort editierbar. Das versionierte [JSON Schema](settings.schema.json) beschreibt alle Felder, Standardwerte und erlaubten Werte. Tasten werden als WPF-`Key`-Namen wie `"Right"` oder `"Add"` geschrieben, nicht als Zahlen. Beim Laden wird die Konfiguration gegen das eingebettete Schema geprüft und danach normalisiert: Navigation akzeptiert Pfeil-, Pos1/Ende- sowie Bild-auf/Bild-ab-Tasten, Zoom `Add`, `Subtract`, `OemPlus` oder `OemMinus`. Zoom wird auf 0,5 bis 3,0 und die Liste auf acht eindeutige, nicht leere Einträge begrenzt. Beim Öffnen einer PDF und nach jedem Zoom-Schritt erscheint der aktuelle Ziel-Zoomwert kurz zwischen den Seiten; er verschwindet 0,5 Sekunden nach dem Rendering. Die PDF-Darstellung verwendet `Windows.Data.Pdf`, die Windows-11-System-API; die App bringt keinen eigenen PDF-Interpreter mit.

## Verteilbare EXE bauen

Die Projektdatei enthält zwei native MSBuild-Targets. Beide erzeugen eine einzelne Windows-x64-EXE ohne Installer. Voraussetzung zum Bauen ist das .NET-8-SDK.

```powershell
# Vollständig eigenständige Variante erstellen
dotnet build SheetMusicViewer.csproj -target:PublishStandalone

# Kleine Variante erstellen, die eine installierte .NET-Runtime voraussetzt
dotnet build SheetMusicViewer.csproj -target:PublishFrameworkDependent
```

Die Ausgabedateien liegen anschließend hier:

| Target | EXE | Voraussetzung |
| --- | --- | --- |
| `Standalone` | `dist\standalone\sheet-music-viewer.exe` | Keine – .NET-Runtime ist enthalten; größere Datei. |
| `FrameworkDependent` | `dist\framework-dependent\sheet-music-viewer-stripped.exe` | Auf dem Ziel-PC muss die [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) für Windows x64 installiert sein; kleinere Datei. |

Jedes Target erzeugt zusätzlich eine CycloneDX-SBOM als `sbom.json` sowie die Datei `THIRD-PARTY-NOTICES.txt` im jeweiligen Distributionsordner. Die Notice enthält die erforderlichen Copyright- und MIT-Lizenzhinweise für die mitgelieferten Open-Source-Komponenten. Die eigenständige Variante enthält zusätzlich `DOTNET-THIRD-PARTY-NOTICES.txt` für die eingebettete .NET-Runtime. Das hierfür verwendete lokale Tool und seine feste Version stehen in `.config\dotnet-tools.json`.
