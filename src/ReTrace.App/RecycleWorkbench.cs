using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReTrace.Core;

namespace ReTrace.App;

public partial class MainWindow
{
    private readonly ObservableCollection<RecycleRow> recycleRows = [];
    private RecycleReport? recycleReport;

    private async Task ScanRecycleAsync(string? copiedRoot = null)
    {
        if (busy) return;
        RecycleClearView();
        RecycleSourceLabel.Text = copiedRoot is null ? "Quelle: aktueller Windows-Benutzer" : "Explizite Papierkorb-Kopie: " + copiedRoot;
        RecycleSummary.Text = "Vorhandene Einträge und ihre Nutzdatenbindung werden gelesen…";
        await RunJobAsync("Windows-Papierkorb wird gelesen…", async (progress, token) => recycleReport = await RecycleBinRecovery.ScanAsync(copiedRoot, progress, token));
        if (recycleReport is null) return;
        foreach (var entry in recycleReport.Entries) recycleRows.Add(new RecycleRow(entry));
        RecycleTitle.Text = $"{recycleRows.Count:N0} Papierkorb-Einträge";
        RecycleSummary.Text = $"{recycleRows.Count(r => r.Entry.CanRecover):N0} Dateien als Kopie rettbar · {ReportStatusLabel(recycleReport.Status)} · nur noch vorhandene Papierkorb-Daten";
        RecycleEmpty.Visibility = recycleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleEmpty.Text = "Im gewählten Profil keine Einträge gefunden.\nNach dem Leeren: Datenträgerabbild oder vorhandene Backups prüfen.";
        StatusLabel.Text = "Papierkorb gelesen. Wähle eine noch vorhandene Datei zum Kopieren aus.";
        ProgressDetail.Text = recycleReport.Diagnostics.Count > 0 ? string.Join(" · ", recycleReport.Diagnostics.Take(2)) : "Ursprüngliche Pfade und Löschzeiten sind Angaben aus den Papierkorb-Metadaten. Der Papierkorb bleibt erhalten.";
        UpdateRecycleControls();
    }

    private async void CopiedRecycle_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = "Explizite Kopie eines Papierkorb-SID-Ordners mit $I-/ $R-Dateien auswählen", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await ScanRecycleAsync(dialog.FolderName);
    });

    private void RecycleSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RecycleGrid.SelectedItem is not RecycleRow row) { UpdateRecycleControls(); return; }
        RecycleDetails.Text = $"{row.Name}\nUrsprünglicher Pfad  {row.OriginalPath}\nLöschzeit  {row.DeletedLabel}\nMetadaten-Größe  {(row.Entry.DeclaredBytes is { } declared ? FormatBytes(declared) : "unbekannt")}\nNutzdaten-Größe  {(row.Entry.ContentBytes is { } content ? FormatBytes(content) : "nicht verfügbar")}\nZustand  {row.StateLabel} · {row.Entry.PayloadKind}\nNutzdaten-Hash  {(row.Entry.PayloadHashVerified ? row.Entry.PayloadIdentity!.Sha256 : "wird bei ausgewählter Rettung geprüft")}\nMetadaten-SHA-256  {row.Entry.MetadataSha256}\nBeleg-ID  {row.Entry.Id}" + (row.Entry.Diagnostics.Count > 0 ? "\nDiagnose  " + string.Join(" · ", row.Entry.Diagnostics) : "");
        UpdateRecycleControls();
    }

    private void UpdateRecycleControls()
    {
        if (RecycleGrid is null) return;
        RecycleGrid.IsEnabled = !busy;
        CopiedRecycleButton.IsEnabled = !busy;
        RecycleClearButton.IsEnabled = !busy && recycleReport is not null;
        RecycleReportButton.IsEnabled = !busy && recycleReport is not null;
        var row = RecycleGrid.SelectedItem as RecycleRow;
        RecycleRestoreButton.IsEnabled = !busy && recycleReport is not null && row?.Entry.CanRecover == true;
        RecycleSelectionSummary.Text = row is null ? "Noch kein Eintrag ausgewählt" : row.Entry.CanRecover ? $"{row.Name} · {row.SizeLabel}" : $"{row.Name} · {row.StateLabel}";
    }

    private async void RecycleRestore_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (busy || RecycleGrid.SelectedItem is not RecycleRow row || !row.Entry.CanRecover) return;
        var dialog = new OpenFolderDialog { Title = "Ziel für eine neue geprüfte Kopie wählen — Papierkorb bleibt erhalten", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await RestoreRecycleSelectionAsync(row.Entry.Id, NewDirectoryName(dialog.FolderName, "ReTrace-Papierkorbrettung"));
    });

    private async Task<ExportReport?> RestoreRecycleSelectionAsync(string entryId, string destination)
    {
        var current = recycleReport ?? throw new InvalidOperationException("Lies zuerst den Papierkorb.");
        var entry = current.Entries.SingleOrDefault(e => e.Id == entryId) ?? throw new InvalidDataException("Der ausgewählte Papierkorb-Eintrag ist nicht in dieser Bestandsaufnahme enthalten.");
        if (!entry.CanRecover) throw new InvalidDataException("Dieser Eintrag enthält keine durch das Profil rettbare reguläre Datei.");
        ExportReport? result = null;
        await RunJobAsync("Papierkorb-Datei wird als neue Kopie gesichert…", async (progress, token) => result = await RecycleBinRecovery.RestoreAsync(current, entryId, destination, progress, token));
        if (result is null) return null;
        outputDirectory = result.Destination;
        OpenOutputButton.Visibility = Directory.Exists(outputDirectory) ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = $"{result.Files.Count:N0} Papierkorb-Kopien gesichert · {ReportStatusLabel(result.Status)}";
        ProgressDetail.Text = result.Diagnostics.Count > 0 ? string.Join(" · ", result.Diagnostics.Take(2)) : "Quellbytes, Kopie und SHA-256 geprüft. Der ursprüngliche Papierkorb-Eintrag bleibt erhalten.";
        return result;
    }

    private async void RecycleReport_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (recycleReport is null || busy) return;
        var dialog = new SaveFileDialog { Title = "Papierkorb-Bestandsaufnahme als neue JSON-Datei sichern", FileName = $"ReTrace-Papierkorb-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "ReTrace-Papierkorbbericht|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var current = recycleReport;
        await RunJobAsync("Papierkorbbericht wird gesichert…", async (_, token) => await RecycleBinRecovery.SaveReportAsync(current, dialog.FileName, token));
        StatusLabel.Text = "Papierkorbbericht gesichert. Die Datei enthält Pfade, Metadaten und Nutzdatenbindungen.";
    });

    private void RecycleClear_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        RecycleClearView();
        StatusLabel.Text = "Papierkorb-Liste geleert. Dateien und Papierkorb-Einträge bleiben erhalten.";
    }

    private void RecycleClearView()
    {
        recycleReport = null;
        recycleRows.Clear();
        RecycleTitle.Text = "Dein Windows-Papierkorb";
        RecycleSummary.Text = "Lies den Papierkorb, um noch vorhandene Einträge und ihre ursprünglichen Pfade zu sehen.";
        RecycleDetails.Text = "Eintrag auswählen: Herkunft, Metadatengröße, Nutzdaten und Diagnose erscheinen hier.";
        RecycleEmpty.Text = "Noch kein Papierkorb gelesen.\nKlicke auf „Papierkorb lesen“.";
        RecycleEmpty.Visibility = Visibility.Visible;
        UpdateRecycleControls();
    }

    private async Task VerifyRecycleWorkbenchAsync(string proof)
    {
        var entryId = ArgumentValue("--recycle-entry") ?? throw new InvalidDataException("--recycle-entry ID ist erforderlich; der Prüflauf darf ausschließlich den explizit ausgewählten Testeintrag retten.");
        SourceMode.SelectedIndex = 2;
        await ScanRecycleAsync(ArgumentValue("--recycle-root"));
        var entry = recycleReport?.Entries.SingleOrDefault(e => e.Id == entryId) ?? throw new InvalidDataException("Der ausdrücklich ausgewählte Testeintrag fehlt im Papierkorb.");
        if (!entry.CanRecover || entry.PayloadIdentity is null) throw new InvalidDataException("Der ausgewählte Testeintrag ist nicht als reguläre Datei rettbar.");
        var payloadHash = await HashFileAsync(entry.PayloadIdentity.Path);
        var metadataHash = entry.MetadataIdentity is not null ? await HashFileAsync(entry.MetadataIdentity.Path) : null;
        // QA screenshots display only the exact synthetic fixture selected by the caller.
        // Unrelated entries from a real Windows bin never enter the exported UI proof.
        recycleRows.Clear();
        recycleRows.Add(new RecycleRow(entry));
        RecycleGrid.SelectedIndex = 0;
        var export = await RestoreRecycleSelectionAsync(entryId, Path.GetFullPath(proof) + ".restored") ?? throw new InvalidDataException("Die ausgewählte Datei wurde nicht als Kopie gesichert.");
        if (export.Files.Count != 1 || export.Files[0].Sha256 != payloadHash || await HashFileAsync(export.Files[0].Path) != payloadHash)
            throw new InvalidDataException("Die geprüfte Kopie stimmt nicht mit den vorhandenen Papierkorb-Daten überein.");
        if (await HashFileAsync(entry.PayloadIdentity.Path) != payloadHash || metadataHash is not null && await HashFileAsync(entry.MetadataIdentity!.Path) != metadataHash)
            throw new InvalidDataException("Papierkorb-Nutzdaten oder Metadaten wurden beim Sichern verändert.");
        RecycleTitle.Text = "Ausgewählter Papierkorb-Testeintrag";
        RecycleSummary.Text = "Nur der explizit ausgewählte Testeintrag wurde als neue Kopie gerettet. Weitere Papierkorb-Dateien bleiben unberührt.";
        StatusLabel.Text = "Papierkorb-Prüflauf bestanden · echte Bestandsaufnahme und bytegenaue Kopie · Quelle erhalten";
        WriteNewJson(proof, new { schema = "retrace.desktop-recycle-verification.v1", scope = recycleReport!.Scope, inventory_status = recycleReport.Status, selected_entry_id = entry.Id, selected_name = entry.Name, payload_sha256 = payloadHash, metadata_sha256 = metadataHash, export, selected_payload_unchanged = true, selected_metadata_unchanged = true, unrelated_entries_not_exported = true, shared_restore_action = true });
    }

    private sealed class RecycleRow(RecycleEntry entry)
    {
        public RecycleEntry Entry { get; } = entry;
        public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.PayloadName ?? Entry.MetadataName ?? Entry.Id : Entry.Name;
        public string OriginalPath => string.IsNullOrWhiteSpace(Entry.OriginalPath) ? "Nicht in Metadaten vorhanden" : Entry.OriginalPath;
        public string DeletedLabel => Entry.DeletedUtc?.ToLocalTime().ToString("g") ?? "Unbekannt";
        public string SizeLabel => Entry.ContentBytes is { } length ? FormatBytes(length) : Entry.DeclaredBytes is { } declared ? FormatBytes(declared) + " angegeben" : "Unbekannt";
        public string StateLabel => Entry.IsDirectory ? "Ordner · nicht unterstützt" : Entry.CanRecover ? Entry.State == "paired" ? "Datei vorhanden" : "Datei mit Diagnose" : Entry.PayloadKind == "missing" ? "Nutzdaten fehlen" : "Nicht rettbar im Profil";
    }
}
