using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using ReTrace.Core;

namespace ReTrace.App;

public partial class MainWindow
{
    private bool caseCanRecover = true;
    private string caseVerification = "current_run";
    private string currentCasePath = "";

    private static int ReadProfileNumber(string input, string label, int minimum, int maximum)
    {
        if (!int.TryParse(input.Trim(), out var value) || value < minimum || value > maximum)
            throw new InvalidDataException($"{label}: Gib eine ganze Zahl von {minimum:N0} bis {maximum:N0} ein.");
        return value;
    }

    private RecoveryOptions ReadNativeOptions() => new()
    {
        MaximumCandidates = ReadProfileNumber(MaximumCandidatesBox.Text, "Maximale Fundanzahl", 1, 100000),
        MaximumFileBytes = ReadProfileNumber(MaximumFileMiBBox.Text, "Maximale Dateigröße in MiB", 1, 1024) * 1024L * 1024,
        IncludeFragments = FragmentsCheck.IsChecked == true
    };

    private PhotoRecOptions ReadPhotoRecOptions() => new()
    {
        MaximumFiles = ReadProfileNumber(MaximumCandidatesBox.Text, "Maximale Fundanzahl", 1, 100000),
        MaximumFileBytes = ReadProfileNumber(MaximumFileMiBBox.Text, "Maximale Dateigröße in MiB", 1, 1024) * 1024L * 1024,
        MaximumOutputBytes = ReadProfileNumber(MaximumOutputGiBBox.Text, "PhotoRec-Ausgabe in GiB", 1, 1024) * 1024L * 1024 * 1024,
        TimeoutSeconds = ReadProfileNumber(TimeoutMinutesBox.Text, "PhotoRec-Laufzeit in Minuten", 1, 10080) * 60,
        IncludeBroken = FragmentsCheck.IsChecked == true,
        Types = DeepScopeBox.SelectedIndex switch { 1 => ["all"], 2 => ["pdf", "zip"], _ => ["jpg", "png"] }
    };

    private void ClearCase_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ClearCurrentInvestigation();
        StatusLabel.Text = "Untersuchung geschlossen. Quelldaten und gesicherte Dateien bleiben erhalten.";
        ProgressDetail.Text = "Du kannst eine gespeicherte Untersuchung öffnen oder eine neue Suche starten.";
    }

    private void ClearCurrentInvestigation()
    {
        ++previewGeneration;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
        report = null;
        caseCanRecover = true;
        caseVerification = "current_run";
        currentCasePath = "";
        visible.Clear();
        KindFilter.ItemsSource = new[] { "Alle Formate" };
        KindFilter.SelectedIndex = 0;
        StateFilter.SelectedIndex = 0;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyPanel.Children.OfType<System.Windows.Controls.TextBlock>().First().Text = "Was noch da ist, zählt.";
        ResultsTitle.Text = "Deine Funde";
        ResultsSummary.Text = "Wähle ein Abbild oder öffne eine gespeicherte Untersuchung.";
        PreviewImage.Source = null;
        PreviewTitle.Text = "Vorschau";
        PreviewStatus.Text = "Eine Vorschau zeigt, was sich bereits lesen lässt.";
        PreviewPlaceholder.Text = "Bild für die Vorschau auswählen";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        CandidateDetails.Text = "";
        outputDirectory = "";
        OpenOutputButton.Visibility = Visibility.Collapsed;
        WorkProgress.Value = 0;
        UpdateControls();
    }

    private async void OpenCase_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Title = "Gespeicherte ReTrace-Untersuchung öffnen", Filter = "ReTrace-Untersuchung und frühere Suchberichte|*.json|Alle Dateien|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) await LoadCaseAsync(dialog.FileName);
    });

    private async Task LoadCaseAsync(string path)
    {
        if (busy) return;
        InvestigationCase? saved = null;
        await RunJobAsync("Gespeicherte Untersuchung wird geprüft…", async (progress, token) => saved = await InvestigationStore.LoadAsync(Path.GetFullPath(path), progress, token));
        if (saved is null) return;
        ClearCurrentInvestigation();
        caseCanRecover = saved.CanRecover;
        caseVerification = saved.Verification;
        currentCasePath = saved.Path;
        SourceBox.Text = saved.Report.Source.Path;
        SourceMode.SelectedIndex = 0;
        EngineBox.SelectedIndex = saved.Report.Engine.Contains("photorec", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ApplyReport(saved.Report, saved.CanRecover ? "Untersuchung geöffnet. Funde wurden ohne erneute Suche geladen." : "Untersuchung geöffnet. Quelldaten fehlen oder haben sich verändert; die Fundliste bleibt lesbar.");
        ResultsSummary.Text += " · " + VerificationLabel(saved.Verification);
        ProgressDetail.Text = saved.Warnings.Count > 0 ? string.Join(" · ", saved.Warnings.Take(2)) : saved.Path;
        UpdateControls();
    }

    private async Task SaveCaseAsAsync()
    {
        if (report is null || busy) return;
        var dialog = new SaveFileDialog { Title = "Untersuchung als neue Datei sichern", FileName = $"ReTrace-Untersuchung-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "ReTrace-Untersuchung|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        await SaveCaseAtPathAsync(dialog.FileName);
    }

    private async Task SaveCaseAtPathAsync(string path)
    {
        if (report is null || busy) return;
        InvestigationCase? saved = null;
        var current = report;
        await RunJobAsync("Untersuchung wird gesichert…", async (progress, token) => saved = await InvestigationStore.SaveAsync(current, Path.GetFullPath(path), progress, token));
        if (saved is null) return;
        currentCasePath = saved.Path;
        StatusLabel.Text = "Untersuchung gesichert. Du kannst sie später ohne erneute Suche öffnen.";
        ProgressDetail.Text = "Die Datei enthält Fundmetadaten. Das Quellabbild bzw. der PhotoRec-Arbeitsordner muss erhalten bleiben: " + saved.Path;
    }

    private async void SaveLibrary_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (report is null || busy) return;
        InvestigationCase? saved = null;
        var current = report;
        await RunJobAsync("Untersuchung wird in der Bibliothek gesichert…", async (progress, token) => saved = await InvestigationStore.SaveToLibraryAsync(current, progress: progress, cancellationToken: token));
        if (saved is null) return;
        currentCasePath = saved.Path;
        StatusLabel.Text = "Untersuchung gemerkt. Du findest sie unter „Gespeicherte…“.";
        ProgressDetail.Text = "Quelldaten und PhotoRec-Arbeitsordner müssen erhalten bleiben. " + saved.Path;
    });

    private async void Library_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (busy) return;
        var dialog = new InvestigationsWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } path) await LoadCaseAsync(path);
    });

    private static string VerificationLabel(string value) => value switch
    {
        "verified_metadata_only" => "Quellidentität geprüft",
        "verified_spool_hashes" => "Funddatei-Hashes geprüft",
        "source_unavailable" => "Quelle fehlt · nur Fundliste",
        "source_identity_changed" => "Quelle verändert · nur Fundliste",
        _ => value
    };

    private async Task VerifySessionWorkbenchAsync(string proof)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) throw new InvalidDataException("--source ist für den Untersuchungs-Prüflauf erforderlich.");
        var sourceHash = await HashFileAsync(SourceBox.Text);
        var deep = arguments.Contains("--session-deep", StringComparer.Ordinal);
        EngineBox.SelectedIndex = deep ? 1 : 0;
        DeepScopeBox.SelectedIndex = deep ? 1 : 0;
        // Exercise the same bounded UI profiles used by the scan buttons.
        MaximumCandidatesBox.Text = "1234";
        MaximumFileMiBBox.Text = "64";
        MaximumOutputGiBBox.Text = "1";
        TimeoutMinutesBox.Text = "5";
        if (ReadNativeOptions().MaximumCandidates != 1234 || ReadPhotoRecOptions().TimeoutSeconds != 300 || ReadPhotoRecOptions().MaximumOutputBytes != 1024L * 1024 * 1024)
            throw new InvalidDataException("Die Suchprofil-Eingaben werden nicht an den Kern weitergegeben.");
        MaximumCandidatesBox.Text = "0";
        var invalidRejected = false;
        try { _ = ReadNativeOptions(); } catch (InvalidDataException) { invalidRejected = true; }
        MaximumCandidatesBox.Text = "1234";
        if (!invalidRejected) throw new InvalidDataException("Das ungültige Suchprofil wurde nicht zurückgewiesen.");
        DeepScopeBox.SelectedIndex = 2;
        if (!ReadPhotoRecOptions().Types.SequenceEqual(new[] { "pdf", "zip" })) throw new InvalidDataException("Das Dokumentprofil ist nicht auf PDF/ZIP begrenzt.");
        DeepScopeBox.SelectedIndex = deep ? 1 : 0;
        await ScanAsync(deep ? Path.GetFullPath(proof) + ".work" : null);
        if (report is null || report.Candidates.Count == 0) throw new InvalidDataException("Der Suchkern hat keine synthetischen Testfunde geliefert.");
        var before = report.Candidates.Select(c => new { c.Id, c.Sha256, c.Offset, c.Length, c.Mapping, c.StoredPath }).ToArray();
        var candidate = report.Candidates.FirstOrDefault(c => c.Kind is "png" or "jpg" or "jpeg") ?? throw new InvalidDataException("Kein Bildfund für die Vorschau vorhanden.");
        var path = Path.GetFullPath(proof) + ".case.json";
        await SaveCaseAtPathAsync(path);
        var savedHash = await HashFileAsync(path);
        ClearCase_Click(this, new RoutedEventArgs());
        if (report is not null || visible.Count != 0 || PreviewImage.Source is not null) throw new InvalidDataException("Die geschlossene Untersuchung hat Daten in der Ansicht zurückgelassen.");
        await LoadCaseAsync(path);
        if (report is null || !caseCanRecover || before.Length != report.Candidates.Count || !before.SequenceEqual(report.Candidates.Select(c => new { c.Id, c.Sha256, c.Offset, c.Length, c.Mapping, c.StoredPath })))
            throw new InvalidDataException("Die Untersuchung wurde nicht unverändert und wiederherstellbar geladen.");
        CandidatesGrid.SelectedItem = visible.First(row => row.Candidate.Id == candidate.Id);
        await PreviewAsync(report.Candidates.First(c => c.Id == candidate.Id));
        if (PreviewImage.Source is null) throw new InvalidDataException("Die Vorschau nach dem Wiederöffnen ist fehlgeschlagen.");
        var export = await RecoveryExporter.ExportAsync(report, [candidate.Id], Path.GetFullPath(proof) + ".restored");
        if (export.Files.Count != 1 || export.Files[0].Sha256 != candidate.Sha256 || await HashFileAsync(export.Files[0].Path) != candidate.Sha256)
            throw new InvalidDataException("Der Export aus der wiedergeöffneten Untersuchung ist nicht bytegenau.");
        if (savedHash != await HashFileAsync(path) || sourceHash != await HashFileAsync(report.Source.Path)) throw new InvalidDataException("Untersuchung oder Quelle wurden beim Wiederöffnen verändert.");
        outputDirectory = export.Destination;
        OpenOutputButton.Visibility = Visibility.Visible;
        StatusLabel.Text = "Untersuchungs-Prüflauf bestanden · gesichert, geschlossen, ohne erneute Suche geöffnet und bytegenau exportiert";
        ProgressDetail.Text = VerificationLabel(caseVerification) + " · Suchprofil und Eingabeprüfung getestet · nur synthetische Testdaten";
        WriteNewJson(proof, new { schema = "retrace.desktop-session-verification.v1", engine = report.Engine, report.Status, case_path = path, case_sha256 = savedHash, source_sha256 = sourceHash, verification = caseVerification, candidate_count = report.Candidates.Count, roundtrip_metadata_equal = true, preview_loaded = PreviewImage.Source is not null, source_unchanged = true, saved_case_unchanged = true, invalid_profile_rejected = invalidRejected, documents_profile_checked = true, native_maximum_candidates = ReadNativeOptions().MaximumCandidates, photorec_timeout_seconds = ReadPhotoRecOptions().TimeoutSeconds, photorec_output_bytes = ReadPhotoRecOptions().MaximumOutputBytes, export });
    }

    private async Task VerifyDocumentsProfileAsync(string proof)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) throw new InvalidDataException("--source ist für den Dokumentprofil-Prüflauf erforderlich.");
        var sourceHash = await HashFileAsync(SourceBox.Text);
        EngineBox.SelectedIndex = 1;
        DeepScopeBox.SelectedIndex = 2;
        ProfileExpander.IsExpanded = true;
        MaximumOutputGiBBox.Text = "1";
        TimeoutMinutesBox.Text = "5";
        await ScanAsync(Path.GetFullPath(proof) + ".work");
        if (report is null || report.Status != "complete_within_profile" || report.Candidates.Any(c => c.Kind is not ("pdf" or "zip")))
            throw new InvalidDataException("Das echte PhotoRec-Dokumentprofil hat unerwartete Formate geliefert oder wurde nicht abgeschlossen.");
        string[] expected = ["35747f6c514a0d3e31b94599bcb40b0b37ddfefc2d4ae971ba0497e6a7399567", "b2386ae255d4502e4e2fa5c11205c8dc6d5fbf69e348c461684be898a6176bb7"];
        var selected = expected.Select(hash => report.Candidates.FirstOrDefault(c => c.Sha256 == hash) ?? throw new InvalidDataException("Eine bekannte synthetische PDF/ZIP-Datei wurde nicht gefunden.")).ToArray();
        CandidatesGrid.SelectedItems.Clear();
        foreach (var candidate in selected) CandidatesGrid.SelectedItems.Add(visible.First(r => r.Candidate.Id == candidate.Id));
        await PreviewAsync(selected[0]);
        if (PreviewImage.Source is not null || !PreviewPlaceholder.Text.Contains("JPEG und PNG", StringComparison.Ordinal)) throw new InvalidDataException("Eine Dokumentdatei wurde für die Bildvorschau freigegeben.");
        var export = await RecoveryExporter.ExportAsync(report, selected.Select(c => c.Id), Path.GetFullPath(proof) + ".restored");
        if (export.Files.Count != 2) throw new InvalidDataException("Die Dokumentauswahl wurde nicht vollständig gesichert.");
        foreach (var file in export.Files)
            if (await HashFileAsync(file.Path) != file.Sha256 || !expected.Contains(file.Sha256)) throw new InvalidDataException("Eine Dokumentkopie ist nicht bytegenau.");
        if (sourceHash != await HashFileAsync(report.Source.Path)) throw new InvalidDataException("Die Quelle wurde verändert.");
        outputDirectory = export.Destination;
        OpenOutputButton.Visibility = Visibility.Visible;
        StatusLabel.Text = "Dokumentprofil geprüft · nur PDF und ZIP gesucht und beide Originaldateien bytegenau gesichert";
        ProgressDetail.Text = "Echter PhotoRec-Lauf mit den UI-Profileinstellungen · Dokumente bleiben ohne Formatprüfung als ungeprüft gekennzeichnet";
        WriteNewJson(proof, new { schema = "retrace.desktop-documents-verification.v1", engine = report.Engine, report.Status, candidate_count = report.Candidates.Count, types = ReadPhotoRecOptions().Types, source_sha256 = sourceHash, source_unchanged = true, unsupported_preview_rejected = true, export });
    }
}
