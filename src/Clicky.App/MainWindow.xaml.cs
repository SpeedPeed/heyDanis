using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clicky.Companion;
using Microsoft.Win32;

namespace Clicky.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<KnowledgeFileItem> _knowledgeFiles = new();
    private readonly CompanionViewModel _viewModel;

    public event EventHandler? SettingsRequested;
    public event EventHandler<bool>? GamerModeChanged;

    public MainWindow(CompanionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        KnowledgeFilesList.ItemsSource = _knowledgeFiles;
        KnowledgePathBox.Text = KnowledgeContextProvider.KnowledgeRoot;
        MasterPromptBox.Text = CompanionManager.CompanionSystemPrompt;
        RefreshKnowledgeFiles();
    }

    public void ShowApplicationWindow()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();
        Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current is { ShutdownMode: ShutdownMode.OnExplicitShutdown } app &&
            !app.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void QuitButton_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();

    private void GamerModeToggle_Changed(object sender, RoutedEventArgs e)
        => GamerModeChanged?.Invoke(this, _viewModel.GamerModeEnabled);

    private void AddKnowledgeFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add knowledge file",
            Filter = "Markdown and text files (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|All files (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        Directory.CreateDirectory(KnowledgeContextProvider.KnowledgeRoot);

        foreach (var sourcePath in dialog.FileNames)
        {
            var fileName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(KnowledgeContextProvider.KnowledgeRoot, fileName);
            destinationPath = GetAvailablePath(destinationPath);
            File.Copy(sourcePath, destinationPath);
        }

        RefreshKnowledgeFiles();
    }

    private void OpenKnowledgeFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(KnowledgeContextProvider.KnowledgeRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = KnowledgeContextProvider.KnowledgeRoot,
            UseShellExecute = true,
        });
    }

    private void RefreshKnowledge_Click(object sender, RoutedEventArgs e)
        => RefreshKnowledgeFiles();

    private void RemoveKnowledgeFile_Click(object sender, RoutedEventArgs e)
    {
        if (KnowledgeFilesList.SelectedItem is not KnowledgeFileItem item)
            return;

        var result = MessageBox.Show(
            this,
            $"Remove {item.Name} from Clicky's local knowledge store?",
            "Remove Knowledge File",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove file", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshKnowledgeFiles();
    }

    private void KnowledgeFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KnowledgeFilesList.SelectedItem is not KnowledgeFileItem item)
        {
            SelectedKnowledgeTitle.Text = "Select a knowledge file";
            KnowledgePreviewBox.Text = "";
            return;
        }

        SelectedKnowledgeTitle.Text = item.Name;
        try
        {
            KnowledgePreviewBox.Text = File.ReadAllText(item.Path);
        }
        catch (Exception ex)
        {
            KnowledgePreviewBox.Text = $"Could not read file: {ex.Message}";
        }
    }

    private void RefreshKnowledgeFiles()
    {
        _knowledgeFiles.Clear();

        foreach (var path in KnowledgeContextProvider.EnumerateKnowledgeFiles())
        {
            _knowledgeFiles.Add(new KnowledgeFileItem(
                Path.GetFileName(path),
                path));
        }

        KnowledgeSummaryText.Text = _knowledgeFiles.Count == 0
            ? "No local knowledge files yet. Add markdown or text files for games, guides, matchups, or long-session reference material."
            : $"{_knowledgeFiles.Count} local knowledge file(s) will be appended to Clicky's prompt on each turn.";

        if (_knowledgeFiles.Count == 0)
        {
            SelectedKnowledgeTitle.Text = "Select a knowledge file";
            KnowledgePreviewBox.Text = "";
        }
    }

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}-{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private sealed record KnowledgeFileItem(string Name, string Path);
}
