using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReTrace.Core;

namespace ReTrace.App;

internal sealed class InvestigationsWindow : Window
{
    private readonly ListBox entries = new() { MinHeight = 180, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button open = new() { Content = "Untersuchung öffnen", IsEnabled = false };
    private readonly Button remove = new() { Content = "Eintrag entfernen", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button refresh = new() { Content = "Aktualisieren", Margin = new Thickness(0, 0, 8, 0) };
    private bool working;
    public string? SelectedPath { get; private set; }

    public InvestigationsWindow()
    {
        Title = "Gespeicherte ReTrace-Untersuchungen";
        Width = 780;
        Height = 570;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        entries.SetResourceReference(Control.ForegroundProperty, "Ink");
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new TextBlock { Text = "Deine Untersuchungen", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var explanation = new TextBlock { Text = "Gespeicherte Fundlisten öffnen ohne erneute Suche. Beim Öffnen werden Quelle bzw. Funddateien geprüft.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        heading.Children.Add(explanation);
        grid.Children.Add(heading);
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = entries };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        Grid.SetRow(border, 1);
        grid.Children.Add(border);
        Grid.SetRow(status, 2);
        grid.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(refresh);
        buttons.Children.Add(remove);
        open.SetResourceReference(StyleProperty, "Primary");
        buttons.Children.Add(open);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;
        entries.SelectionChanged += (_, _) => UpdateButtons();
        entries.MouseDoubleClick += (_, _) => OpenSelected();
        open.Click += (_, _) => OpenSelected();
        refresh.Click += async (_, _) => await RefreshAsync();
        remove.Click += async (_, _) => await RemoveAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }

    private async Task RefreshAsync()
    {
        if (working) return;
        working = true;
        UpdateButtons();
        try
        {
            var saved = await InvestigationStore.ListAsync();
            entries.Items.Clear();
            foreach (var summary in saved)
            {
                var detail = new StackPanel { Margin = new Thickness(5, 7, 5, 7) };
                detail.Children.Add(new TextBlock { Text = summary.Error.Length > 0 ? "Beschädigte Untersuchung · " + Path.GetFileName(summary.Path) : Path.GetFileName(summary.SourcePath), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var line = new TextBlock { Text = summary.Error.Length > 0 ? summary.Error : $"{summary.CandidateCount:N0} Funde · {summary.Engine} · {summary.Status} · {summary.SavedUtc.ToLocalTime():g}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                detail.Children.Add(line);
                var row = new ListBoxItem { Content = detail, Tag = summary, ToolTip = summary.SourcePath + "\n" + summary.Path };
                row.SetResourceReference(Control.ForegroundProperty, "Ink");
                entries.Items.Add(row);
            }
            status.Text = saved.Count == 0 ? "Noch keine Untersuchungen gemerkt. Klicke nach einer Suche auf „Merken“." : "„Eintrag entfernen“ löscht nur die gespeicherte Untersuchung. Quelldaten und gerettete Dateien bleiben erhalten.";
            if (entries.Items.Count > 0) entries.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { status.Text = "Die Bibliothek konnte nicht gelesen werden: " + ex.Message; }
        finally { working = false; UpdateButtons(); }
    }

    private async Task RemoveAsync()
    {
        if (working || (entries.SelectedItem as ListBoxItem)?.Tag is not InvestigationSummary summary) return;
        working = true;
        UpdateButtons();
        try
        {
            await InvestigationStore.DeleteFromLibraryAsync(summary.CaseId);
            entries.Items.Remove(entries.SelectedItem);
            status.Text = "Untersuchungseintrag entfernt. Quelldaten und gerettete Dateien bleiben erhalten.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { status.Text = "Der Eintrag konnte nicht entfernt werden: " + ex.Message; }
        finally { working = false; UpdateButtons(); }
    }

    private void OpenSelected()
    {
        if (working || (entries.SelectedItem as ListBoxItem)?.Tag is not InvestigationSummary summary) return;
        SelectedPath = summary.Path;
        DialogResult = true;
    }

    private void UpdateButtons()
    {
        entries.IsEnabled = !working;
        refresh.IsEnabled = !working;
        open.IsEnabled = !working && entries.SelectedItem is not null;
        remove.IsEnabled = !working && entries.SelectedItem is not null;
    }
}
