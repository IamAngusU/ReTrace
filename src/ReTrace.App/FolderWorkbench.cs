using System.IO;
using System.Windows;
using Microsoft.Win32;
using ReTrace.Core;

namespace ReTrace.App;

public partial class MainWindow
{
    private FolderRecoveryReport? folderReport;

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = "Ordner mit Backups, Kopien oder exportierten Cache-Dateien auswählen", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { FolderSourceBox.Text = dialog.FolderName; SourceMode.SelectedIndex = 3; }
    }

    private FolderRecoveryOptions ReadFolderOptions() => new()
    {
        MaximumFiles = ReadProfileNumber(FolderMaximumFilesBox.Text, "Maximale Quelldateien", 1, 10000),
        MaximumSourceBytes = ReadProfileNumber(FolderMaximumMiBBox.Text, "Gesamtbudget in MiB", 1, 16384) * 1024L * 1024,
        MaximumContainerBytes = ReadProfileNumber(FolderContainerMiBBox.Text, "Größe je Quelldatei in MiB", 1, 1024) * 1024L * 1024,
        MaximumCandidates = ReadProfileNumber(MaximumCandidatesBox.Text, "Maximale Fundanzahl", 1, 100000),
        MaximumFileBytes = ReadProfileNumber(MaximumFileMiBBox.Text, "Maximale Fundgröße in MiB", 1, 1024) * 1024L * 1024,
        IncludeFragments = FragmentsCheck.IsChecked == true,
        MaximumDepth = 8,
        MaximumEntries = 10000
    };

    private async Task ScanFolderAsync()
    {
        if (busy) return;
        var source = FolderSourceBox.Text.Trim();
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source)) throw new InvalidDataException("Wähle zuerst einen vorhandenen Ordner mit absolutem Pfad.");
        var options = ReadFolderOptions();
        ClearFolderView();
        ResultsSummary.Text = "Vorhandene Dateien und eingebettete Bilddaten werden innerhalb des Suchprofils gelesen.";
        await RunJobAsync("Ordner wird nach vorhandenen Bilddaten durchsucht…", async (progress, token) => folderReport = await FolderRecovery.ScanAsync(source, options, progress, token));
        if (folderReport is null) return;
        KindFilter.ItemsSource = new[] { "Alle Formate" }.Concat(folderReport.Containers.SelectMany(c => c.Recovery.Candidates).Select(c => c.Kind.ToUpperInvariant()).Distinct().Order());
        KindFilter.SelectedIndex = 0;
        StateFilter.SelectedIndex = 0;
        ApplyFolderView();
        StatusLabel.Text = "Ordnersuche: " + ReportStatusLabel(folderReport.Status) + ". Wähle vorhandene Bilddaten zum Sichern aus.";
        ProgressDetail.Text = folderReport.Diagnostics.Count > 0 ? string.Join(" · ", folderReport.Diagnostics.Take(2)) : "Begrenztes Suchprofil · Löschstatus und frühere Originalvollständigkeit bleiben unbekannt.";
    }

    private void ApplyFolderView()
    {
        var count = folderReport?.Containers.Sum(c => c.Recovery.Candidates.Count) ?? 0;
        ResultsTitle.Text = folderReport is null ? "Bilddaten in Ordnern" : $"{count:N0} Bildfunde aus Dateien";
        ResultsSummary.Text = folderReport is null ? "Wähle einen Ordner. Versteckte reguläre Dateien werden ebenfalls berücksichtigt." : $"{folderReport.ScannedFiles:N0} Dateien · {FormatBytes(folderReport.ScannedBytes)} gelesen · {ReportStatusLabel(folderReport.Status)} · keine vollständige Originalrekonstruktion";
        RefreshFolderCandidates();
        UpdateControls();
    }

    private void RefreshFolderCandidates()
    {
        var kind = KindFilter.SelectedItem as string ?? "Alle Formate";
        var state = StateFilter.SelectedIndex;
        visible.Clear();
        if (folderReport is not null)
        {
            foreach (var container in folderReport.Containers)
            foreach (var candidate in container.Recovery.Candidates)
            {
                var row = new CandidateRow(candidate, container.Recovery, container.Id + ":" + candidate.Id);
                if (kind != "Alle Formate" && !candidate.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) || state > 0 && row.StateGroup != state) continue;
                visible.Add(row);
            }
        }
        EmptyPanel.Visibility = visible.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Children.OfType<System.Windows.Controls.TextBlock>().First().Text = folderReport is null ? "Noch kein Ordner durchsucht." : "In diesem Profil keine passenden Bildfunde.";
        UpdateControls();
    }

    private void ClearFolderView()
    {
        folderReport = null;
        ++previewGeneration;
        previewCancellation?.Cancel();
        visible.Clear();
        PreviewImage.Source = null;
        PreviewTitle.Text = "Vorschau";
        PreviewStatus.Text = "Ein Fund kann auch ein Vorschaubild oder eine frühere Kopie sein.";
        PreviewPlaceholder.Text = "Bildfund für die Vorschau auswählen";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        CandidateDetails.Text = "";
        KindFilter.ItemsSource = new[] { "Alle Formate" };
        KindFilter.SelectedIndex = 0;
        StateFilter.SelectedIndex = 0;
        ApplyFolderView();
    }

    private async Task ExportFolderSelectionAsync()
    {
        if (folderReport is null || busy) return;
        var selected = CandidatesGrid.SelectedItems.Cast<CandidateRow>().Select(r => r.HitId).ToArray();
        if (selected.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "Ziel für einen neuen Rettungsordner wählen", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await RestoreFolderSelectionAsync(selected, NewDirectoryName(dialog.FolderName, "ReTrace-Ordnerrettung"));
    }

    private async Task<ExportReport?> RestoreFolderSelectionAsync(string[] ids, string destination)
    {
        var current = folderReport ?? throw new InvalidOperationException("Lies zuerst den ausgewählten Ordner.");
        ExportReport? result = null;
        await RunJobAsync("Ausgewählte Bilddaten werden als geprüfte Kopien gesichert…", async (progress, token) => result = await FolderRecovery.RestoreAsync(current, ids, destination, progress, token));
        if (result is null) return null;
        outputDirectory = result.Destination;
        OpenOutputButton.Visibility = Directory.Exists(outputDirectory) ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = $"{result.Files.Count:N0} Kopien gesichert · {ReportStatusLabel(result.Status)}";
        ProgressDetail.Text = result.Diagnostics.Count > 0 ? string.Join(" · ", result.Diagnostics.Take(2)) : "Quellcontainer und Fundhashes wurden geprüft. Vorhandene Originaldateien bleiben erhalten.";
        return result;
    }

    private async Task SaveFolderReportAsync()
    {
        if (folderReport is null || busy) return;
        var dialog = new SaveFileDialog { Title = "Ordnersuchbericht als neue JSON-Datei sichern", FileName = $"ReTrace-Ordnerbericht-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "ReTrace-Ordnersuchbericht|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var current = folderReport;
        await RunJobAsync("Ordnersuchbericht wird gesichert…", async (_, token) => await FolderRecovery.SaveReportAsync(current, Path.GetFullPath(dialog.FileName), token));
        StatusLabel.Text = "Ordnersuchbericht gesichert. Er kann als Fundmetadaten in TraceLedger übernommen werden.";
        ProgressDetail.Text = "Dieser Bericht ist kein portables Abbild oder eine in der Untersuchungsbibliothek wieder zu öffnende Bilduntersuchung.";
    }

    private async Task VerifyFolderWorkbenchAsync(string proof)
    {
        var source = ArgumentValue("--source") ?? throw new InvalidDataException("--source FOLDER ist für den Ordner-Prüflauf erforderlich.");
        FolderSourceBox.Text = Path.GetFullPath(source);
        SourceMode.SelectedIndex = 3;
        await ScanFolderAsync();
        if (folderReport is null || visible.Count == 0) throw new InvalidDataException("Die synthetische Ordnersuche hat keine Bilddaten geliefert.");
        var rows = visible.Take(3).ToArray();
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) sourceHashes[row.SourceReport!.Source.Path] = await HashFileAsync(row.SourceReport.Source.Path);
        CandidatesGrid.SelectedItems.Clear();
        foreach (var row in rows) CandidatesGrid.SelectedItems.Add(row);
        var selected = rows.Select(r => r.HitId).ToArray();
        var export = await RestoreFolderSelectionAsync(selected, Path.GetFullPath(proof) + ".restored") ?? throw new InvalidDataException("Die ausgewählten Bilddaten wurden nicht gesichert.");
        if (export.Files.Count != selected.Length) throw new InvalidDataException("Nicht alle ausgewählten Bildfunde wurden gesichert.");
        foreach (var file in export.Files)
            if (await HashFileAsync(file.Path) != file.Sha256 || !rows.Any(r => r.Candidate.Sha256 == file.Sha256)) throw new InvalidDataException("Ein Ordnerexport stimmt nicht mit seinen Fundbytes überein.");
        foreach (var (path, hash) in sourceHashes)
            if (await HashFileAsync(path) != hash) throw new InvalidDataException("Ein Quellcontainer wurde verändert.");
        await PreviewAsync(rows[0].Candidate, rows[0].SourceReport);
        var reportPath = Path.GetFullPath(proof) + ".report.json";
        await FolderRecovery.SaveReportAsync(folderReport, reportPath);
        StatusLabel.Text = "Ordner-Prüflauf bestanden · echte Suche, quellgebundene Vorschau und bytegenau gesicherte Kopien";
        WriteNewJson(proof, new { schema = "retrace.desktop-folder-verification.v1", folderReport.Status, folderReport.VisitedEntries, folderReport.ScannedFiles, folderReport.ScannedBytes, containers = folderReport.Containers.Count, candidate_count = visible.Count, selected, export, report_path = reportPath, preview_loaded = PreviewImage.Source is not null, sources_unchanged = true, profile = ReadFolderOptions() });
    }
}
