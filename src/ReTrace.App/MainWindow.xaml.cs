using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ReTrace.Core;

namespace ReTrace.App;

public partial class MainWindow : Window
{
    private readonly string[] arguments;
    private readonly ObservableCollection<CandidateRow> visible = [];
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RecoveryReport? report;
    private CancellationTokenSource? activeCancellation;
    private CancellationTokenSource? previewCancellation;
    private RecoveryProgress? lastProgress;
    private string outputDirectory = "";
    private bool busy, ready, light, closingRequested, closingAllowed, verifyCancelAfterFirstChunk;
    private long previewGeneration;
    private long jobGeneration;

    public MainWindow(string[] arguments)
    {
        this.arguments = arguments;
        InitializeComponent();
        CandidatesGrid.ItemsSource = visible;
        RecycleGrid.ItemsSource = recycleRows;
        KindFilter.ItemsSource = new[] { "Alle Formate" };
        KindFilter.SelectedIndex = 0;
        StateFilter.ItemsSource = new[] { "Alle Zustände", "Struktur vollständig", "Teilweise", "Fragment", "Ungeprüft / unbekannt" };
        StateFilter.SelectedIndex = 0;
        SourceMode.SelectedIndex = ArgumentValue("--source") is not null || ArgumentValue("--case") is not null ? 0 : 2;
        EngineBox.SelectedIndex = 0;
        MediaScopeBox.SelectedIndex = 0;
        DeepScopeBox.SelectedIndex = 0;
        ready = true;
        using (var identity = WindowsIdentity.GetCurrent()) ElevateButton.Visibility = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? Visibility.Collapsed : Visibility.Visible;
        if (arguments.Contains("--light", StringComparer.Ordinal)) ApplyTheme(true);
        if (ArgumentValue("--source") is { } source)
        {
            SourceBox.Text = source;
            if (IsLocalFolderArgument(source))
            {
                FolderSourceBox.Text = Path.GetFullPath(source);
                SourceMode.SelectedIndex = 3;
            }
        }
        timer.Tick += (_, _) => RenderProgress();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => PickSource_Click(this, new RoutedEventArgs())));
        Loaded += Window_Loaded;
        UpdateControls();
        UpdateMediaProfileUI();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (arguments.Contains("--capture", StringComparer.Ordinal)) SourceMode.SelectedIndex = 1;
            if (ArgumentValue("--verify-video-workbench") is { } videoProof) await VerifyVideoWorkbenchAsync(videoProof);
            else if (ArgumentValue("--verify-source-handoff") is { } handoffProof) VerifySourceHandoff(handoffProof);
            else if (ArgumentValue("--verify-recycle-workbench") is { } recycleProof) await VerifyRecycleWorkbenchAsync(recycleProof);
            else if (ArgumentValue("--verify-folder-workbench") is { } folderProof) await VerifyFolderWorkbenchAsync(folderProof);
            else if (ArgumentValue("--verify-documents-workbench") is { } documentsProof) await VerifyDocumentsProfileAsync(documentsProof);
            else if (ArgumentValue("--verify-session-workbench") is { } sessionProof) await VerifySessionWorkbenchAsync(sessionProof);
            else if (ArgumentValue("--case") is { } savedCase) await LoadCaseAsync(savedCase);
            else if (ArgumentValue("--verify-cancel") is { } cancelProof) await VerifyCancellationAsync(cancelProof);
            else if (ArgumentValue("--verify-deep-workbench") is { } deepProof) await VerifyDeepWorkbenchAsync(deepProof);
            else if (ArgumentValue("--verify-workbench") is { } proof) await VerifyWorkbenchAsync(proof);
            else if (arguments.Contains("--scan", StringComparer.Ordinal)) { if (SourceMode.SelectedIndex == 3) await ScanFolderAsync(); else await ScanAsync(); }
            if (ArgumentValue("--render-preview") is { } png)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CaptureWindow(png);
                closingAllowed = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
            if (ArgumentValue("--verify-source-handoff") is { } handoffProof) WriteNewJson(handoffProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-workbench") is { } proof) WriteNewJson(proof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-cancel") is { } cancelProof) WriteNewJson(cancelProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-deep-workbench") is { } deepProof) WriteNewJson(deepProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-session-workbench") is { } sessionProof) WriteNewJson(sessionProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-documents-workbench") is { } documentsProof) WriteNewJson(documentsProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-recycle-workbench") is { } recycleProof) WriteNewJson(recycleProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-folder-workbench") is { } folderProof) WriteNewJson(folderProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--verify-video-workbench") is { } videoProof) WriteNewJson(videoProof + ".error.json", new { error = ex.ToString() });
            if (ArgumentValue("--render-preview") is { } png)
            {
                try { CaptureWindow(png); } catch (IOException) { }
                Environment.ExitCode = 1;
                closingAllowed = true;
                Close();
            }
        }
    }

    private string? ArgumentValue(string key)
    {
        var index = Array.IndexOf(arguments, key);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private static bool IsLocalFolderArgument(string source)
    {
        try { return Directory.Exists(PathSafety.CheckPath(source, mustExist: false)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private void VerifySourceHandoff(string proof)
    {
        string source = ArgumentValue("--source") ?? throw new InvalidDataException("An explicit --source is required.");
        bool folder = IsLocalFolderArgument(source);
        string selected = folder ? FolderSourceBox.Text : SourceBox.Text;
        if (SourceMode.SelectedIndex != (folder ? 3 : 0) || Path.GetFullPath(selected) != Path.GetFullPath(source) ||
            busy || report is not null || folderReport is not null || recycleReport is not null)
            throw new InvalidDataException("Source handoff did not select the correct profile without scanning.");
        WriteNewJson(proof, new { schema = "retrace.source-handoff-verification.v1", source_kind = folder ? "folder" : "image",
            source_selected = true, scan_started = false, mode = SourceMode.SelectedIndex });
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => SourceMode.SelectedIndex switch { 2 => ScanRecycleAsync(), 3 => ScanFolderAsync(), _ => ScanAsync() });

    private async Task ScanAsync(string? explicitDeepWork = null)
    {
        if (busy) return;
        var source = SourceBox.Text.Trim();
        if (!Path.IsPathFullyQualified(source) || !File.Exists(source)) throw new InvalidDataException("Wähle zuerst eine vorhandene Abbilddatei mit absolutem Pfad.");
        string? work = explicitDeepWork;
        var deep = EngineBox.SelectedIndex == 1;
        var nativeOptions = ReadNativeOptions();
        var deepOptions = deep ? ReadPhotoRecOptions() : null;
        if (deep && work is null)
        {
            var folder = new OpenFolderDialog { Title = "PhotoRec-Arbeitsordner: Ziel auf einem anderen Datenträger wählen", Multiselect = false };
            if (folder.ShowDialog(this) != true) return;
            work = NewDirectoryName(folder.FolderName, "ReTrace-Tiefensuche");
        }
        ClearCurrentInvestigation();
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyPanel.Children.OfType<TextBlock>().First().Text = "Die Suche läuft.";
        ResultsSummary.Text = "Funde werden nach Abschluss oder Abbruch zur Prüfung angezeigt.";
        PreviewImage.Source = null;
        PreviewPlaceholder.Text = "Die Vorschau erscheint nach der Suche.";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        CandidateDetails.Text = "";
        OpenOutputButton.Visibility = Visibility.Collapsed;
        await RunJobAsync("Mediendaten werden gesucht…", async (progress, token) =>
        {
            if (deep)
                report = await PhotoRecEngine.RunAsync(source, work!, deepOptions, progress, token);
            else
                report = await MediaCarver.ScanAsync(source, nativeOptions, ReadMediaTypes(), progress, token);
        });
        if (report is null) return;
        ApplyReport(report);
    }

    private void ApplyReport(RecoveryReport nextReport, string? loadedMessage = null)
    {
        report = nextReport;
        KindFilter.ItemsSource = new[] { "Alle Formate" }.Concat(report.Candidates.Select(c => c.Kind.ToUpperInvariant()).Distinct().Order());
        KindFilter.SelectedIndex = 0;
        StateFilter.SelectedIndex = 0;
        RefreshCandidates();
        ResultsTitle.Text = $"{report.Candidates.Count:N0} Funde";
        ResultsSummary.Text = $"{FormatBytes(report.ScannedBytes)} untersucht · {EngineLabel(report.Engine)} · {ReportStatusLabel(report.Status)}";
        StatusLabel.Text = loadedMessage ?? (report.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "Suche abgebrochen. Bereits erfasste Funde bleiben prüfbar." : "Suche beendet. Wähle Funde aus, die du sichern möchtest.");
        ProgressDetail.Text = report.Diagnostics.Count > 0 ? string.Join(" · ", report.Diagnostics.Take(2)) : "Die Fundliste ist auf das gewählte Suchprofil begrenzt. Keine Garantie auf Vollständigkeit.";
        WorkProgress.IsIndeterminate = false;
        WorkProgress.Value = report.Source.Length > 0 ? Math.Clamp(100.0 * report.ScannedBytes / report.Source.Length, 0, 100) : 0;
        if (visible.Count > 0) CandidatesGrid.SelectedIndex = 0;
        UpdateControls();
    }

    private async Task RunJobAsync(string label, Func<IProgress<RecoveryProgress>, CancellationToken, Task> action)
    {
        if (busy) throw new InvalidOperationException("Warte, bis der laufende Vorgang abgeschlossen ist.");
        busy = true;
        clock.Restart();
        lastProgress = null;
        activeCancellation = new CancellationTokenSource();
        long thisJob = ++jobGeneration;
        StatusLabel.Text = label;
        ProgressDetail.Text = "Vorgang wird vorbereitet…";
        WorkProgress.Value = 0;
        WorkProgress.IsIndeterminate = true;
        UpdateControls();
        timer.Start();
        try
        {
            var progress = new Progress<RecoveryProgress>(value =>
            {
                if (!busy || thisJob != jobGeneration) return;
                lastProgress = value;
                RenderProgress();
                if (verifyCancelAfterFirstChunk && value.Stage == "scanning" && value.BytesDone > 0 && activeCancellation?.IsCancellationRequested == false) Cancel_Click(this, new RoutedEventArgs());
            });
            await action(progress, activeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Vorgang abgebrochen.";
            ProgressDetail.Text = "Die Quelle wurde nicht verändert. Ein begonnenes Abbild kann als unvollständig zurückbleiben.";
        }
        finally
        {
            timer.Stop();
            clock.Stop();
            busy = false;
            WorkProgress.IsIndeterminate = false;
            activeCancellation.Dispose();
            activeCancellation = null;
            UpdateControls();
            if (closingRequested) { closingAllowed = true; Close(); }
        }
    }

    private void RenderProgress()
    {
        if (lastProgress is not { } p)
        {
            ProgressDetail.Text = $"Vorbereitung · {clock.Elapsed.TotalSeconds:N0} s";
            return;
        }
        if (p.Stage.Equals("deep_scan", StringComparison.OrdinalIgnoreCase))
        {
            WorkProgress.IsIndeterminate = true;
            StatusLabel.Text = "PhotoRec durchsucht das Abbild…";
            ProgressDetail.Text = $"{p.Candidates:N0} bisherige Funde · {FormatBytes(p.BytesDone)} Funddaten" + (p.BytesTotal > 0 ? $" · Grenze {FormatBytes(p.BytesTotal)}" : "") + $" · {clock.Elapsed.TotalSeconds:N0} s · Scanfortschritt nicht verfügbar";
            return;
        }
        if (p.Stage is "recycle_inventory" or "folder_inventory")
        {
            WorkProgress.IsIndeterminate = true;
            StatusLabel.Text = p.Stage == "recycle_inventory" ? "Papierkorb-Einträge werden gelesen…" : "Ordnerdateien werden erfasst…";
            ProgressDetail.Text = $"{p.BytesDone:N0} Einträge erfasst · {clock.Elapsed.TotalSeconds:N0} s · Aufzählung ohne vollständigen Gesamtzähler";
            return;
        }
        var ratio = p.BytesTotal > 0 ? Math.Clamp(100.0 * p.BytesDone / p.BytesTotal, 0, 100) : 0;
        WorkProgress.IsIndeterminate = p.BytesTotal <= 0;
        WorkProgress.Value = ratio;
        StatusLabel.Text = StageLabel(p.Stage);
        ProgressDetail.Text = p.Stage == "assessing_output"
            ? $"{p.BytesDone:N0} von {p.BytesTotal:N0} Funddateien geprüft · {clock.Elapsed.TotalSeconds:N0} s"
            : $"{FormatBytes(p.BytesDone)}" + (p.BytesTotal > 0 ? $" von {FormatBytes(p.BytesTotal)} · {ratio:N1} %" : "") + $" · {p.Candidates:N0} Funde · {clock.Elapsed.TotalSeconds:N0} s";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        activeCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusLabel.Text = "Abbruch angefordert. Laufende Schreibvorgänge werden beendet…";
    }

    private void PickSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Datenträgerabbild auswählen", CheckFileExists = true, Multiselect = false, Filter = "Datenträgerabbilder|*.img;*.dd;*.raw;*.bin;*.iso|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) { SourceBox.Text = dialog.FileName; SourceMode.SelectedIndex = 0; }
    }

    private async void SourceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ImagePanel is null || DrivePanel is null) return;
        var drive = SourceMode.SelectedIndex == 1;
        var recycle = SourceMode.SelectedIndex == 2;
        var folder = SourceMode.SelectedIndex == 3;
        ImagePanel.Visibility = SourceMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrivePanel.Visibility = drive ? Visibility.Visible : Visibility.Collapsed;
        if (RecycleSourcePanel is not null) RecycleSourcePanel.Visibility = recycle ? Visibility.Visible : Visibility.Collapsed;
        if (FolderSourcePanel is not null) FolderSourcePanel.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        if (ImageSearchSettings is not null) ImageSearchSettings.Visibility = recycle ? Visibility.Collapsed : Visibility.Visible;
        if (ImageResultsPanel is not null) ImageResultsPanel.Visibility = recycle ? Visibility.Collapsed : Visibility.Visible;
        if (RecycleResultsPanel is not null) RecycleResultsPanel.Visibility = recycle ? Visibility.Visible : Visibility.Collapsed;
        if (EngineBox is not null) EngineBox.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        if (FolderLimitsPanel is not null) FolderLimitsPanel.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        if (SearchModeLabel is not null) SearchModeLabel.Text = folder ? "Medien in vorhandenen Dateien" : "Suchmodus";
        if (DeepScopePanel is not null) DeepScopePanel.Visibility = !folder && EngineBox?.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (DeepLimitsPanel is not null) DeepLimitsPanel.Visibility = !folder && EngineBox?.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (folder && EngineDescription is not null) EngineDescription.Text = "Integrierte JPEG-/PNG-Suche mit Quellpfad, Dateioffset und geprüften Kopien. Ein Fund belegt vorhandene Bytes; das vollständige frühere Original ist damit nicht nachgewiesen.";
        else if (!recycle && EngineDescription is not null) EngineDescription.Text = EngineBox?.SelectedIndex == 1 ? "PhotoRec sucht weitere Dateitypen und kann beschädigte Dateien behalten. Benötigt einen eigenen Arbeitsordner für gefundene Kopien." : "Sucht Bildsignaturen direkt im Abbild. Erkennt vollständige Strukturen und kennzeichnet beschädigte oder abgeschnittene Funde.";
        if (ready && !recycle) { if (folder) ApplyFolderView(); else if (report is not null) ApplyReport(report); else RefreshCandidates(); }
        if (ScanButton is not null) ScanButton.Content = recycle ? "Papierkorb lesen" : folder ? "Ordner durchsuchen" : EngineBox?.SelectedIndex == 1 ? "Tiefensuche starten" : "Bilder suchen";
        if (ready && drive && VolumesBox.Items.Count == 0) await GuardAsync(RefreshVolumesAsync);
        UpdateControls();
        UpdateMediaProfileUI();
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EngineDescription is null) return;
        EngineDescription.Text = EngineBox.SelectedIndex == 1
            ? "PhotoRec sucht weitere Dateitypen und kann beschädigte Dateien behalten. Benötigt einen eigenen Arbeitsordner für gefundene Kopien."
            : "Sucht Bildsignaturen direkt im Abbild. Erkennt vollständige Strukturen und kennzeichnet beschädigte oder abgeschnittene Funde.";
        if (DeepScopePanel is not null) DeepScopePanel.Visibility = EngineBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (DeepLimitsPanel is not null) DeepLimitsPanel.Visibility = EngineBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (ScanButton is not null) ScanButton.Content = SourceMode.SelectedIndex == 2 ? "Papierkorb lesen" : SourceMode.SelectedIndex == 3 ? "Ordner durchsuchen" : EngineBox.SelectedIndex == 1 ? "Tiefensuche starten" : "Bilder suchen";
        UpdateMediaProfileUI();
    }

    private async void RefreshVolumes_Click(object sender, RoutedEventArgs e) => await GuardAsync(RefreshVolumesAsync);

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Der eigene Programmpfad ist nicht verfügbar.");
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("Der eigene Programmpfad ist nicht gültig.");
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--capture");
            if (light) start.ArgumentList.Add("--light");
            using var process = Process.Start(start);
            if (process is null) throw new IOException("Die neue ReTrace-Instanz konnte nicht gestartet werden.");
            closingAllowed = true;
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusLabel.Text = "Administratorstart abgebrochen. Du kannst weiter mit Abbilddateien arbeiten.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { ShowFailure(ex); }
    }

    private async Task RefreshVolumesAsync()
    {
        if (busy) return;
        var volumes = await Task.Run(VolumeAcquisition.ListVolumes);
        VolumesBox.ItemsSource = volumes.Select(v => new VolumeRow(v.Id, $"{v.Label} · {FormatBytes(v.Length)} · {v.FileSystem}" + (v.IsSystem ? " · System" : ""))).ToArray();
        VolumesBox.SelectedIndex = volumes.Count > 0 ? 0 : -1;
        if (volumes.Count == 0) StatusLabel.Text = "Keine unterstützten lokalen Laufwerke gefunden.";
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await GuardAsync(CaptureAsync);
    private async Task CaptureAsync()
    {
        if (VolumesBox.SelectedItem is not VolumeRow row) throw new InvalidDataException("Wähle das betroffene Laufwerk aus.");
        var dialog = new SaveFileDialog { Title = "Neues Abbild auf einem anderen physischen Datenträger sichern", FileName = $"ReTrace-Abbild-{DateTime.Now:yyyyMMdd-HHmmss}.img", Filter = "Rohes Datenträgerabbild|*.img", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName) || Directory.Exists(dialog.FileName)) throw new IOException("Das Ziel existiert bereits. Wähle einen neuen Dateinamen.");
        CaptureReport? result = null;
        await RunJobAsync("Laufwerk wird als Abbild gesichert…", async (progress, token) => result = await VolumeAcquisition.CaptureAsync(row.Id, dialog.FileName, progress, token));
        if (result is null) return;
        StatusLabel.Text = $"Abbild: {ReportStatusLabel(result.Status)} · {FormatBytes(result.BytesCopied)}";
        ProgressDetail.Text = string.Join(" · ", result.Diagnostics.Prepend(result.Consistency).Take(3));
        if (result.Status.Contains("complete", StringComparison.OrdinalIgnoreCase) && !result.Status.Contains("incomplete", StringComparison.OrdinalIgnoreCase))
        {
            SourceBox.Text = result.Destination;
            SourceMode.SelectedIndex = 0;
            StatusLabel.Text = "Abbild gesichert. Du kannst jetzt darin nach Fotos, Videos und weiteren Daten suchen.";
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (ready) RefreshCandidates(); }
    private void RefreshCandidates()
    {
        if (SourceMode.SelectedIndex == 3) { RefreshFolderCandidates(); return; }
        if (report is null) return;
        var kind = KindFilter.SelectedItem as string ?? "Alle Formate";
        var state = StateFilter.SelectedIndex;
        visible.Clear();
        foreach (var candidate in report.Candidates)
        {
            if (kind != "Alle Formate" && !candidate.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            var row = new CandidateRow(candidate);
            if (state > 0 && row.StateGroup != state) continue;
            visible.Add(row);
        }
        EmptyPanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible.Count == 0) EmptyPanel.Children.OfType<TextBlock>().First().Text = report.Candidates.Count > 0 ? "Kein Fund passt zum Filter." : "In diesem Profil keine passenden Mediendaten gefunden.";
        UpdateControls();
    }

    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateControls();
        if (CandidatesGrid.SelectedItem is CandidateRow row) await PreviewAsync(row.Candidate, row.SourceReport);
    }

    private async Task PreviewAsync(RecoveryCandidate candidate, RecoveryReport? candidateSource = null)
    {
        var sourceReport = candidateSource ?? report;
        if (sourceReport is null) return;
        var generation = ++previewGeneration;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var token = previewCancellation.Token;
        PreviewImage.Source = null;
        PreviewPlaceholder.Text = "Vorschau wird geprüft…";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewTitle.Text = CandidateRow.NameFor(candidate);
        PreviewStatus.Text = CandidateRow.LabelFor(candidate.State) + ". " + (candidate.DeletionStatus == "unknown" ? "Löschstatus unbekannt." : "Löschstatus: " + candidate.DeletionStatus);
        if (IsVideoKind(candidate.Kind)) PreviewStatus.Text += " Video-Container: Struktur und Kopierbarkeit sind keine Abspielgarantie.";
        CandidateDetails.Text = $"Format  {candidate.Kind.ToUpperInvariant()}\nGröße   {FormatBytes(candidate.Length)}\n" + (candidate.StoredPath is null ? $"Quelloffset  {candidate.Offset:N0}\nQuelle  {sourceReport.Source.Path}" : $"Rekonstruktion\n{candidate.StoredPath}\nQuelloffset  nicht verifiziert") + $"\nZuordnung  {candidate.Mapping}\n\n{candidate.Validation}\n\nSHA-256\n{candidate.Sha256}" + (candidate.Diagnostics.Count > 0 ? "\n\n" + string.Join("\n", candidate.Diagnostics) : "");
        if (!caseCanRecover && candidateSource is null)
        {
            PreviewPlaceholder.Text = "Die gespeicherte Quelle ist nicht verfügbar oder hat sich verändert. Die Fundliste bleibt lesbar; öffne eine passende, unveränderte Quelle erneut.";
            return;
        }
        try
        {
            var preview = await Task.Run(() => BuildPreview(sourceReport, candidate, token), token);
            if (generation != previewGeneration) return;
            PreviewImage.Source = preview;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            if (CandidateRow.GroupFor(candidate.State) > 1) PreviewStatus.Text += " Der Bilddecoder kann fehlende Bereiche als Platzhalter darstellen. Gesichert werden nur die vorhandenen Bytes.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            if (generation != previewGeneration) return;
            PreviewPlaceholder.Text = ex is InvalidDataException ? ex.Message : "Für diese Fundbytes ist keine sichere Bildvorschau verfügbar. Die unveränderten Bytes können trotzdem gesichert werden.";
        }
    }

    private static BitmapSource BuildPreview(RecoveryReport sourceReport, RecoveryCandidate candidate, CancellationToken token)
    {
        if (candidate.Kind.ToLowerInvariant() is not ("jpeg" or "jpg" or "png")) throw new InvalidDataException(IsVideoKind(candidate.Kind) ? "Die sichere Bildvorschau unterstützt JPEG und PNG. Video-Container werden nicht automatisch geöffnet oder abgespielt. Du kannst die vorhandenen Bytes als geprüfte Kopie sichern." : "Die sichere Bildvorschau unterstützt JPEG und PNG. Andere Dateitypen können als unveränderte Kopie gesichert werden.");
        if (candidate.Length <= 0 || candidate.Length > 64L * 1024 * 1024) throw new InvalidDataException("Die Vorschau ist auf Bilddateien bis 64 MiB begrenzt.");
        var path = candidate.StoredPath ?? sourceReport.Source.Path;
        using var source = PathSafety.OpenRead(path);
        if (candidate.StoredPath is null) PathSafety.VerifyIdentity(sourceReport.Source, PathSafety.Identity(path, source));
        var offset = candidate.StoredPath is null ? candidate.Offset : 0;
        if (offset < 0 || offset > source.Length || candidate.Length > source.Length - offset) throw new InvalidDataException("Der Fundbereich ist nicht vollständig lesbar.");
        source.Position = offset;
        var bytes = new byte[checked((int)candidate.Length)];
        source.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die Fundbytes stimmen nicht mehr mit dem Suchbericht überein.");
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var first = decoder.Frames[0];
        var width = first.PixelWidth;
        var height = first.PixelHeight;
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 24_000_000) throw new InvalidDataException("Die Vorschau ist auf 24 Megapixel und 32.768 Pixel pro Seite begrenzt.");
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        var previewScale = Math.Min(1.0, Math.Min(1400.0 / width, 1000.0 / height));
        image.DecodePixelWidth = Math.Max(1, (int)(width * previewScale));
        image.DecodePixelHeight = Math.Max(1, (int)(height * previewScale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        return image;
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (SourceMode.SelectedIndex == 3) { await ExportFolderSelectionAsync(); return; }
        if (report is null) return;
        var selected = CandidatesGrid.SelectedItems.Cast<CandidateRow>().Select(r => r.Candidate.Id).ToArray();
        if (selected.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "Zielordner wählen — ReTrace erstellt darin einen neuen Rettungsordner", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var destination = NewDirectoryName(dialog.FolderName, "ReTrace-Rettung");
        ExportReport? exported = null;
        await RunJobAsync("Ausgewählte Dateien werden als Kopien gesichert…", async (progress, token) => exported = await RecoveryExporter.ExportAsync(report, selected, destination, progress, token));
        if (exported is null) return;
        outputDirectory = exported.Destination;
        OpenOutputButton.Visibility = Directory.Exists(outputDirectory) ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = $"{exported.Files.Count:N0} Kopien gesichert · {ReportStatusLabel(exported.Status)}";
        ProgressDetail.Text = exported.Diagnostics.Count > 0 ? string.Join(" · ", exported.Diagnostics.Take(2)) : exported.Destination;
    });

    private async void SaveReport_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => SourceMode.SelectedIndex == 3 ? SaveFolderReportAsync() : SaveCaseAsAsync());

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(outputDirectory)) Process.Start(new ProcessStartInfo { FileName = outputDirectory, UseShellExecute = true });
    }

    private void Candidate_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The preview stays inside ReTrace; a double click never opens untrusted files externally.
        e.Handled = true;
    }

    private void UpdateControls()
    {
        if (!ready) return;
        SourceMode.IsEnabled = !busy;
        ImagePanel.IsEnabled = !busy;
        DrivePanel.IsEnabled = !busy;
        FolderSourcePanel.IsEnabled = !busy;
        EngineBox.IsEnabled = !busy;
        DeepScopeBox.IsEnabled = !busy;
        MediaScopeBox.IsEnabled = !busy;
        DeepMdatCheck.IsEnabled = !busy;
        VideoPresetButton.IsEnabled = !busy;
        ProfileExpander.IsEnabled = !busy;
        FragmentsCheck.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && SourceMode.SelectedIndex != 1;
        CancelButton.IsEnabled = busy && activeCancellation?.IsCancellationRequested == false;
        CaptureButton.IsEnabled = !busy;
        var count = CandidatesGrid.SelectedItems.Count;
        SelectionSummary.Text = count == 0 ? "Noch keine Funde ausgewählt" : $"{count:N0} Funde ausgewählt · {FormatBytes(CandidatesGrid.SelectedItems.Cast<CandidateRow>().Sum(r => r.Candidate.Length))}";
        var folder = SourceMode.SelectedIndex == 3;
        ExportButton.IsEnabled = !busy && count > 0 && (folder ? folderReport is not null : caseCanRecover && report is not null);
        ReportButton.IsEnabled = !busy && (folder ? folderReport is not null : report is not null);
        ReportButton.ToolTip = folder ? "Ordnersuchbericht für TraceLedger sichern; keine in der Bildbibliothek wieder zu öffnende Untersuchung" : "Als später wieder zu öffnende Untersuchung sichern";
        SaveLibraryButton.IsEnabled = !busy && !folder && report is not null;
        ClearCaseButton.IsEnabled = !busy && (folder ? folderReport is not null : report is not null);
        OpenCaseButton.IsEnabled = !busy;
        LibraryButton.IsEnabled = !busy;
        UpdateRecycleControls();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { ShowFailure(ex); }
    }

    private void ShowFailure(Exception ex)
    {
        StatusLabel.Text = "Der Vorgang konnte nicht abgeschlossen werden.";
        ProgressDetail.Text = ex.Message;
        WorkProgress.IsIndeterminate = false;
        UpdateControls();
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ApplyTheme(!light);
    private void ApplyTheme(bool enabled)
    {
        light = enabled;
        var colors = enabled
            ? new Dictionary<string, string> { ["Paper"] = "#F1F3F7", ["Surface"] = "#FFFFFF", ["Raised"] = "#E8ECF3", ["Input"] = "#F7F8FB", ["Ink"] = "#171B26", ["Muted"] = "#535D70", ["Line"] = "#D5DBE5", ["Accent"] = "#BD6228", ["AccentInk"] = "#FFFFFF", ["Selected"] = "#FAE5D6", ["Good"] = "#376549", ["Danger"] = "#A93E42" }
            : new Dictionary<string, string> { ["Paper"] = "#080A11", ["Surface"] = "#11131C", ["Raised"] = "#191D28", ["Input"] = "#0C0E16", ["Ink"] = "#E1E1E8", ["Muted"] = "#A0A6B5", ["Line"] = "#303441", ["Accent"] = "#FFB779", ["AccentInk"] = "#241A12", ["Selected"] = "#302821", ["Good"] = "#9ABCA4", ["Danger"] = "#ECA3A3" };
        foreach (var (key, value) in colors) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        ThemeButton.Content = light ? "Dunkler Modus" : "Heller Modus";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closingAllowed || !busy) { previewCancellation?.Cancel(); return; }
        e.Cancel = true;
        closingRequested = true;
        activeCancellation?.Cancel();
        StatusLabel.Text = "Vorgang wird beendet. ReTrace schließt nach dem sicheren Abbruch.";
    }

    private static string NewDirectoryName(string parent, string prefix) => Path.Combine(parent, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(prefix.Length + 1 + 15 + 1 + 8)]);
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        var value = (double)Math.Max(0, bytes);
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value.ToString(index == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[index]}";
    }
    private static string EngineLabel(string engine) => engine.Contains("photorec", StringComparison.OrdinalIgnoreCase) ? "PhotoRec" : engine is "native-media-carver" or "native-video-carver" ? "Integrierte Mediensuche" : "Integrierte Bildsuche";
    private static string StageLabel(string stage) => stage.ToLowerInvariant() switch
    {
        "scan" or "scanning" => "Mediendaten werden gesucht",
        "hash" or "hashing" or "hashing_source" => "Quelldaten werden geprüft",
        "preparing_engine" => "PhotoRec wird geprüft und vorbereitet",
        "assessing_output" => "Funddateien werden geprüft",
        "verifying_saved_output" => "Gespeicherte Funddateien werden per SHA-256 geprüft",
        "investigation_loaded" => "Gespeicherte Untersuchung geladen",
        "investigation_saved" => "Untersuchung gesichert",
        "saving_investigation" => "Untersuchung wird gesichert",
        "recycle_scan" or "scanning_recycle" => "Windows-Papierkorb wird gelesen",
        "recycle_restore" or "restoring_recycle" => "Papierkorb-Datei wird als geprüfte Kopie gesichert",
        "recycle_copying" => "Papierkorb-Datei wird als neue Kopie gesichert",
        "folder_scanning" => "Vorhandene Dateien werden nach Mediendaten durchsucht",
        "export" or "exporting" or "recovering" => "Kopien werden gesichert",
        "capture" or "capturing" => "Abbild wird gesichert",
        "verify_capture" => "Gesichertes Abbild wird erneut geprüft",
        "photorec" or "deep_scan" => "PhotoRec durchsucht das Abbild",
        "finished" or "complete" or "complete_within_profile" => "Vorgang abgeschlossen",
        var state when state.Contains("cancel") => "Vorgang abgebrochen",
        var state when state.Contains("partial") || state.Contains("incomplete") => "Vorgang mit unvollständigem Ergebnis beendet",
        _ => "Vorgang läuft"
    };
    private static string ReportStatusLabel(string status) => status.ToLowerInvariant() switch { "complete" or "complete_within_profile" or "completed" => "im Suchprofil abgeschlossen", var value when value.Contains("cancel") => "abgebrochen", var value when value.Contains("partial") || value.Contains("incomplete") => "begrenzt / unvollständig", _ => status };

    private static void WriteNewJson(string path, object value)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(true);
    }

    private async Task VerifyDeepWorkbenchAsync(string proof)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) throw new InvalidDataException("--source ist für den PhotoRec-Prüflauf erforderlich.");
        var sourceHash = await HashFileAsync(SourceBox.Text);
        EngineBox.SelectedIndex = 1;
        DeepScopeBox.SelectedIndex = 1;
        FragmentsCheck.IsChecked = true;
        await ScanAsync(Path.GetFullPath(proof) + ".work");
        if (report is null || !report.Engine.Contains("photorec", StringComparison.OrdinalIgnoreCase) || report.Status != "complete_within_profile") throw new InvalidDataException("Die echte PhotoRec-Tiefensuche wurde nicht erfolgreich abgeschlossen.");
        // These SHA-256 pins describe the documented synthetic QA fixture, never user files.
        string[] expectedWholeHashes = [
            "5a7232bae338989ed1aa1053a814427d65fbb098b65ce6cbe264b3f1d2717b0f",
            "5b3ccf28a82f9cd648a502b0f077cb30dc9b82ac826b675f7192bd84864afe63",
            "35747f6c514a0d3e31b94599bcb40b0b37ddfefc2d4ae971ba0497e6a7399567",
            "b2386ae255d4502e4e2fa5c11205c8dc6d5fbf69e348c461684be898a6176bb7"
        ];
        var whole = expectedWholeHashes.Select(hash => report.Candidates.FirstOrDefault(c => c.Sha256 == hash) ?? throw new InvalidDataException("Eine bekannte vollständige Testdatei wurde nicht bytegenau gefunden: " + hash)).ToArray();
        var partial = report.Candidates.FirstOrDefault(c => c.State == "partial" && c.Kind is "jpg" or "jpeg") ?? throw new InvalidDataException("Der abgeschnittene JPEG-Testfund fehlt.");
        if (partial.StoredPath is null || partial.Length < 2000) throw new InvalidDataException("Der JPEG-Teilfund enthält nicht den erwarteten Originalbereich.");
        using (var stream = PathSafety.OpenRead(partial.StoredPath))
        {
            var prefix = new byte[2000];
            stream.ReadExactly(prefix);
            if (Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant() != "ac4aa4f12fd686d0d56ee4a348a8d9311bd863321f3c07765c4432de8fd8b6ce") throw new InvalidDataException("Die vorhandenen 2.000 JPEG-Originalbytes wurden nicht bytegenau erhalten.");
        }
        var document = whole.First(c => c.Kind == "pdf");
        if (CandidateRow.LabelFor(document.State) != "Ungeprüft") throw new InvalidDataException("PhotoRec-Dokumente ohne unabhängige Formatprüfung müssen als ungeprüft erscheinen.");
        await PreviewAsync(document);
        if (PreviewImage.Source is not null || !PreviewPlaceholder.Text.Contains("JPEG und PNG", StringComparison.Ordinal)) throw new InvalidDataException("Der nicht freigegebene Vorschau-Dateityp wurde nicht frühzeitig zurückgewiesen.");
        KindFilter.SelectedItem = "PNG";
        if (visible.Count == 0 || visible.Any(r => r.Candidate.Kind != "png")) throw new InvalidDataException("Der PhotoRec-Formatfilter arbeitet nicht korrekt.");
        KindFilter.SelectedIndex = 0;
        StateFilter.SelectedIndex = 2;
        if (visible.Count == 0 || visible.Any(r => r.StateGroup != 2)) throw new InvalidDataException("Der PhotoRec-Zustandsfilter arbeitet nicht korrekt.");
        StateFilter.SelectedIndex = 0;
        var selected = whole.Append(partial).ToArray();
        CandidatesGrid.SelectedItems.Clear();
        foreach (var row in visible.Where(r => selected.Any(c => c.Id == r.Candidate.Id))) CandidatesGrid.SelectedItems.Add(row);
        var selectedIds = CandidatesGrid.SelectedItems.Cast<CandidateRow>().Select(r => r.Candidate.Id).ToArray();
        var exported = await RecoveryExporter.ExportAsync(report, selectedIds, Path.GetFullPath(proof) + ".restored");
        if (exported.Files.Count != selected.Length) throw new InvalidDataException("Nicht alle ausgewählten PhotoRec-Testfunde wurden exportiert.");
        foreach (var file in exported.Files)
            if (!file.Sha256.Equals(await HashFileAsync(file.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ein PhotoRec-Export stimmt nicht mit seinem Hash überein.");
        if (sourceHash != await HashFileAsync(SourceBox.Text)) throw new InvalidDataException("Das PhotoRec-Quellabbild wurde verändert.");
        await PreviewAsync(partial);
        bool partialPreviewLoaded = PreviewImage.Source is not null;
        var preferred = whole.First(c => c.Kind == "png");
        CandidatesGrid.SelectedItem = visible.First(r => r.Candidate.Id == preferred.Id);
        await PreviewAsync(preferred);
        if (PreviewImage.Source is null) throw new InvalidDataException("Die echte PNG-Vorschau des PhotoRec-Funds ist fehlgeschlagen.");
        outputDirectory = exported.Destination;
        OpenOutputButton.Visibility = Visibility.Visible;
        StatusLabel.Text = "PhotoRec-UI-Prüflauf bestanden · 4 Originaldateien und JPEG-Teilfund bytegenau gesichert";
        ProgressDetail.Text = "Alle PhotoRec-Dateitypen · ungeprüfte Dokumente gekennzeichnet · nur synthetische Testdaten";
        WriteNewJson(proof, new { schema = "retrace.desktop-deep-verification.v1", source = report.Source, source_sha256 = sourceHash, engine = report.Engine, status = report.Status, scope = "all", candidates = report.Candidates.Count, known_whole_sha256 = expectedWholeHashes, partial = new { partial.Id, partial.Length, partial.State, first_2000_original_bytes_verified = true }, selected = selectedIds, export = exported, preview_loaded = PreviewImage.Source is not null, partial_preview_loaded = partialPreviewLoaded, unsupported_preview_rejected = true, nonimage_state_label = CandidateRow.LabelFor(document.State), source_unchanged = true, component_default_bundle = true, kind_filter_checked = true, state_filter_checked = true });
    }

    private async Task VerifyCancellationAsync(string proof)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) throw new InvalidDataException("--source ist für den Abbruch-Prüflauf erforderlich.");
        var hash = await HashFileAsync(SourceBox.Text);
        verifyCancelAfterFirstChunk = true;
        try { await ScanAsync(); }
        finally { verifyCancelAfterFirstChunk = false; }
        if (report is null || !report.Status.Contains("cancel", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die UI-Abbruchaktion hat den laufenden Suchkern nicht abgebrochen.");
        if (hash != await HashFileAsync(SourceBox.Text)) throw new InvalidDataException("Die Quelle wurde beim Abbruch verändert.");
        WriteNewJson(proof, new { schema = "retrace.desktop-cancel-verification.v1", report.Status, report.ScannedBytes, report.Source.Length, retained_candidates = report.Candidates.Count, source_sha256 = hash, source_unchanged = true, busy_after = busy, cancellation_action = "Cancel_Click_after_first_scanning_progress" });
        StatusLabel.Text = "UI-Abbruch geprüft. Bereits erfasste Funde bleiben verfügbar.";
    }

    private async Task VerifyWorkbenchAsync(string proof)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) throw new InvalidDataException("--source ist für den ReTrace-Prüflauf erforderlich.");
        var sourceHash = await HashFileAsync(SourceBox.Text);
        EngineBox.SelectedIndex = 0;
        await ScanAsync();
        if (report is null || report.Candidates.Count == 0) throw new InvalidDataException("Der reale Kern hat keine synthetischen Testfunde geliefert.");
        var allCount = visible.Count;
        KindFilter.SelectedItem = report.Candidates[0].Kind.ToUpperInvariant();
        if (visible.Count == 0 || visible.Any(r => !r.Candidate.Kind.Equals(report.Candidates[0].Kind, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Der Formatfilter arbeitet nicht korrekt.");
        KindFilter.SelectedIndex = 0;
        var native = report.Candidates.FirstOrDefault(c => CandidateRow.GroupFor(c.State) == 1);
        var partial = report.Candidates.FirstOrDefault(c => c.State == "partial") ?? report.Candidates.FirstOrDefault(c => CandidateRow.GroupFor(c.State) is 2 or 3);
        CandidatesGrid.SelectedItems.Clear();
        foreach (var row in visible.Where(r => r.Candidate.Id == native?.Id || r.Candidate.Id == partial?.Id)) CandidatesGrid.SelectedItems.Add(row);
        if (CandidatesGrid.SelectedItems.Count == 0) CandidatesGrid.SelectAll();
        var ids = CandidatesGrid.SelectedItems.Cast<CandidateRow>().Select(r => r.Candidate.Id).ToArray();
        var destination = Path.GetFullPath(proof) + ".restored";
        var exported = await RecoveryExporter.ExportAsync(report, ids, destination);
        foreach (var file in exported.Files)
            if (!file.Sha256.Equals(await HashFileAsync(file.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die exportierten Bytes stimmen nicht mit ihrem Hash überein.");
        if (!sourceHash.Equals(await HashFileAsync(SourceBox.Text), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die Quelle wurde während des UI-Prüflaufs verändert.");
        bool partialPreviewLoaded = false;
        string partialPreviewMessage = "";
        if (partial is not null)
        {
            StateFilter.SelectedIndex = CandidateRow.GroupFor(partial.State);
            if (visible.Count == 0 || visible.Any(r => r.StateGroup != CandidateRow.GroupFor(partial.State))) throw new InvalidDataException("Der Zustandsfilter arbeitet nicht korrekt.");
            CandidatesGrid.SelectedItem = visible.First(r => r.Candidate.Id == partial.Id);
            await PreviewAsync(partial);
            partialPreviewLoaded = PreviewImage.Source is not null;
            partialPreviewMessage = PreviewPlaceholder.Visibility == Visibility.Visible ? PreviewPlaceholder.Text : PreviewStatus.Text;
            StateFilter.SelectedIndex = 0;
        }
        var preferred = native ?? report.Candidates[0];
        CandidatesGrid.SelectedItem = visible.First(r => r.Candidate.Id == preferred.Id);
        await PreviewAsync(preferred);
        outputDirectory = destination;
        OpenOutputButton.Visibility = Visibility.Visible;
        StatusLabel.Text = $"UI-Prüflauf bestanden · {exported.Files.Count} Kopien bytegenau gesichert";
        ProgressDetail.Text = "Echter Suchkern, Filter, Auswahl, Vorschau und Export · nur synthetische Testdaten";
        WriteNewJson(proof, new { schema = "retrace.desktop-verification.v1", source = report.Source, source_sha256 = sourceHash, report.Status, candidate_count = report.Candidates.Count, all_visible = allCount, structural = native?.Id, partial = partial?.Id, selected = ids, export = exported, preview_loaded = PreviewImage.Source is not null, partial_preview_loaded = partialPreviewLoaded, partial_preview_message = partialPreviewMessage, source_unchanged = true, kind_filter_checked = true, state_filter_checked = partial is not null, engine = report.Engine, theme = light ? "light" : "dark" });
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private void CaptureWindow(string path)
    {
        UpdateLayout();
        var target = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private sealed record VolumeRow(string Id, string Label);
    private sealed class CandidateRow(RecoveryCandidate candidate, RecoveryReport? sourceReport = null, string? hitId = null)
    {
        public RecoveryCandidate Candidate { get; } = candidate;
        public RecoveryReport? SourceReport { get; } = sourceReport;
        public string HitId { get; } = hitId ?? candidate.Id;
        public string DisplayName => NameFor(Candidate) + (SourceReport is null ? "" : " · " + Path.GetFileName(SourceReport.Source.Path));
        public string StateLabel => LabelFor(Candidate.State);
        public string SizeLabel => FormatBytes(Candidate.Length);
        public int StateGroup => GroupFor(Candidate.State);
        public static string NameFor(RecoveryCandidate c) => string.IsNullOrWhiteSpace(c.Name) ? $"{c.Kind.ToUpperInvariant()} · {c.Id}" : c.Name;
        public static int GroupFor(string state) => state.ToLowerInvariant() switch { "intact" or "complete" or "structurally_complete" or "structural" or "structural_complete" => 1, "partial" or "truncated" or "damaged" or "incomplete" => 2, "fragment" or "header_fragment" => 3, _ => 4 };
        public static string LabelFor(string state) => state.ToLowerInvariant() switch { var value when value.StartsWith("unverified", StringComparison.Ordinal) => "Ungeprüft", "damaged" => "Beschädigt", _ => GroupFor(state) switch { 1 => "Struktur vollständig", 2 => "Teilweise", 3 => "Fragment", _ => "Unbekannt" } };
    }
}
