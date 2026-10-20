using System.IO;
using System.Windows;
using System.Windows.Controls;
using ReTrace.Core;

namespace ReTrace.App;

public partial class MainWindow
{
    private string[] ReadMediaTypes() => MediaScopeBox.SelectedIndex switch
    {
        1 => ["mp4", "mov", "avi"],
        2 => ["jpeg", "png", "mp4", "mov", "avi"],
        _ => ["jpeg", "png"]
    };

    private string[] ReadDeepMediaTypes()
    {
        if (DeepScopeBox.SelectedIndex == 1) return ["all"];
        if (DeepScopeBox.SelectedIndex == 2) return ["pdf", "zip"];
        if (DeepScopeBox.SelectedIndex is not (3 or 4)) return ["jpg", "png"];
        var types = new List<string>();
        if (DeepScopeBox.SelectedIndex == 4) types.AddRange(["jpg", "png"]);
        types.AddRange(["mov", "avi", "mkv", "wmv", "mpg"]);
        if (DeepMdatCheck.IsChecked == true) types.Add("mov/mdat");
        return types.ToArray();
    }

    private void MediaScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (MediaScopeBox.SelectedIndex > 0 && MaximumFileMiBBox.Text.Trim() == "64") MaximumFileMiBBox.Text = "256";
        UpdateMediaProfileUI();
    }

    private void DeepScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (DeepScopeBox.SelectedIndex is 3 or 4 && MaximumFileMiBBox.Text.Trim() == "64") MaximumFileMiBBox.Text = "256";
        UpdateMediaProfileUI();
    }

    private void VideoPreset_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        MaximumFileMiBBox.Text = "256";
        FolderContainerMiBBox.Text = "512";
        FolderMaximumMiBBox.Text = "2048";
        MaximumOutputGiBBox.Text = "8";
        TimeoutMinutesBox.Text = "120";
        ProfileExpander.IsExpanded = true;
        StatusLabel.Text = "Video-Profil gesetzt: 256 MiB pro Fund, 512 MiB pro Ordnerdatei, 2 GiB Ordnerbudget.";
        ProgressDetail.Text = "PhotoRec: 8 GiB überwachte Ausgabe, 120 Minuten. Eine erreichte Grenze bleibt als unvollständig erkennbar.";
    }

    private void UpdateMediaProfileUI()
    {
        if (!ready || NativeScopePanel is null) return;
        var folder = SourceMode.SelectedIndex == 3;
        var recycle = SourceMode.SelectedIndex == 2;
        var deep = EngineBox.SelectedIndex == 1 && !folder;
        NativeScopePanel.Visibility = !deep ? Visibility.Visible : Visibility.Collapsed;
        DeepScopePanel.Visibility = deep ? Visibility.Visible : Visibility.Collapsed;
        DeepVideoPanel.Visibility = deep && DeepScopeBox.SelectedIndex is 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
        var video = deep ? DeepScopeBox.SelectedIndex is 1 or 3 or 4 : MediaScopeBox.SelectedIndex > 0;
        VideoPresetButton.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        if (!deep && !recycle)
            EngineDescription.Text = MediaScopeBox.SelectedIndex == 0 ? "Prüft JPEG-/PNG-Strukturen direkt in den vorhandenen Bytes. Vorschauen und Fragmente bleiben an ihren Quellbereich gebunden."
                : "Prüft MP4/MOV-Boxen und AVI-Strukturen. MKV/WebM und weitere Familien findest du im PhotoRec-Profil. Struktur vollständig bedeutet keine garantierte Abspielbarkeit.";
        ScanButton.Content = recycle ? "Papierkorb lesen" : folder ? "Ordner durchsuchen" : deep ? "Tiefensuche starten" : MediaScopeBox.SelectedIndex == 0 ? "Bilder suchen" : "Medien suchen";
        FragmentsCheck.Content = "Auch beschädigte Funde und Fragmente behalten";
    }

    private static bool IsVideoKind(string kind) => kind.ToLowerInvariant() is "mp4" or "mov" or "avi" or "mkv" or "webm" or "wmv" or "asf" or "mpg" or "mpeg" or "3gp" or "3g2";

    private async Task VerifyVideoWorkbenchAsync(string proof)
    {
        string source = ArgumentValue("--source") ?? throw new InvalidDataException("--source ist für den Video-Prüflauf erforderlich.");
        bool folder = IsLocalFolderArgument(source);
        bool deep = arguments.Contains("--video-deep", StringComparer.Ordinal);
        string? originalImageHash = folder ? null : await HashFileAsync(source);
        MediaScopeBox.SelectedIndex = arguments.Contains("--all-media", StringComparer.Ordinal) ? 2 : 1;
        VideoPreset_Click(this, new RoutedEventArgs());
        if (folder)
        {
            FolderSourceBox.Text = Path.GetFullPath(source);
            SourceMode.SelectedIndex = 3;
            await ScanFolderAsync();
        }
        else
        {
            SourceBox.Text = Path.GetFullPath(source);
            SourceMode.SelectedIndex = 0;
            EngineBox.SelectedIndex = deep ? 1 : 0;
            DeepScopeBox.SelectedIndex = 3;
            DeepMdatCheck.IsChecked = arguments.Contains("--video-mdat", StringComparer.Ordinal);
            await ScanAsync(deep ? Path.GetFullPath(proof) + ".work" : null);
        }
        var rows = visible.Where(row => IsVideoKind(row.Candidate.Kind)).Take(8).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Der echte Suchkern hat im gewählten Profil keine Videofunde geliefert.");
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var bound = row.SourceReport ?? report ?? throw new InvalidDataException("Der Video-Fund hat keine Quellbindung.");
            if (row.Candidate.StoredPath is null) sourceHashes[bound.Source.Path] = await HashFileAsync(bound.Source.Path);
        }
        if (!folder) sourceHashes[Path.GetFullPath(source)] = originalImageHash!;
        CandidatesGrid.SelectedItems.Clear();
        foreach (var row in rows) CandidatesGrid.SelectedItems.Add(row);
        ExportReport? export;
        var destination = Path.GetFullPath(proof) + ".restored";
        if (folder) export = await RestoreFolderSelectionAsync(rows.Select(row => row.HitId).ToArray(), destination);
        else
        {
            export = null;
            var current = report ?? throw new InvalidDataException("Der Video-Suchbericht fehlt.");
            await RunJobAsync("Ausgewählte Videofunde werden als geprüfte Kopien gesichert…", async (progress, token) => export = await RecoveryExporter.ExportAsync(current, rows.Select(row => row.Candidate.Id), destination, progress, token));
        }
        if (export is null || export.Files.Count != rows.Length) throw new InvalidDataException("Nicht alle ausgewählten Videofunde wurden gesichert.");
        foreach (var file in export.Files)
            if (await HashFileAsync(file.Path) != file.Sha256 || !rows.Any(row => row.Candidate.Sha256 == file.Sha256)) throw new InvalidDataException("Ein Video-Export stimmt nicht mit seinen ausgewählten Fundbytes überein.");
        foreach (var (path, hash) in sourceHashes)
            if (await HashFileAsync(path) != hash) throw new InvalidDataException("Eine Videoquelle wurde verändert.");
        CandidatesGrid.SelectedItem = rows[0];
        await PreviewAsync(rows[0].Candidate, rows[0].SourceReport);
        if (PreviewImage.Source is not null || !PreviewPlaceholder.Text.Contains("JPEG und PNG", StringComparison.Ordinal)) throw new InvalidDataException("Videos wurden unzulässig zur Bildvorschau freigegeben.");
        string reportPath = Path.GetFullPath(proof) + ".report.json";
        if (folder) await FolderRecovery.SaveReportAsync(folderReport!, reportPath);
        else await SaveCaseAtPathAsync(reportPath);
        outputDirectory = export.Destination;
        OpenOutputButton.Visibility = Visibility.Visible;
        StatusLabel.Text = "Video-Prüflauf bestanden · vorhandene Bytes ausgewählt und bytegenau gesichert · keine Abspielgarantie abgeleitet";
        ProgressDetail.Text = "Format, Zustand, Quellbereich und SHA-256 bleiben prüfbar. Keine automatische Wiedergabe, Reparatur oder Fragmentverknüpfung.";
        WriteNewJson(proof, new { schema = "retrace.desktop-video-verification.v1", source_kind = folder ? "folder" : "image", engine = folder ? "folder-media" : report!.Engine, status = folder ? folderReport!.Status : report!.Status, media_types = ReadMediaTypes(), photorec_types = deep ? ReadDeepMediaTypes() : null, candidates = rows.Select(row => new { id = row.HitId, row.Candidate.Kind, row.Candidate.State, row.Candidate.Length, row.Candidate.Offset, row.Candidate.Sha256, row.Candidate.Mapping }), export, report_path = reportPath, source_unchanged = true, automatic_playback = false, video_image_preview_rejected = true, per_candidate_bytes = ReadNativeOptions().MaximumFileBytes });
    }
}
