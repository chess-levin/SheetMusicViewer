using Microsoft.Win32;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SheetMusicViewer;

public partial class RecentPdfsWindow : Window
{
    private readonly Action<string>? _removeRecentFile;
    private readonly DispatcherTimer _missingFileNoticeTimer;
    public string? SelectedPath { get; private set; }

    public RecentPdfsWindow(IEnumerable<string> recentFiles, Action<string>? removeRecentFile = null)
    {
        InitializeComponent();
        _removeRecentFile = removeRecentFile;
        _missingFileNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _missingFileNoticeTimer.Tick += MissingFileNoticeTimer_Tick;
        Closed += (_, _) => _missingFileNoticeTimer.Stop();
        RecentFilesList.ItemsSource = new ObservableCollection<RecentPdfItem>(recentFiles.Select(path => new RecentPdfItem(path)));
        RecentFilesList.SelectedIndex = 0;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RecentFilesList.SelectedItem is RecentPdfItem item)
        {
            if (!File.Exists(item.FullPath))
            {
                _removeRecentFile?.Invoke(item.FullPath);
                ((ObservableCollection<RecentPdfItem>)RecentFilesList.ItemsSource).Remove(item);
                ShowMissingFileNotice(item.FileName);
                RecentFilesList.SelectedIndex = RecentFilesList.Items.Count > 0 ? 0 : -1;
                return;
            }

            SelectedPath = item.FullPath;
            DialogResult = true;
        }
    }

    private void RecentFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected_Click(sender, e);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF-Dateien (*.pdf)|*.pdf", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        SelectedPath = dialog.FileName;
        DialogResult = true;
    }

    private void ShowMissingFileNotice(string fileName)
    {
        MissingFileNotice.Text = $"Die Datei „{fileName}“ wurde nicht gefunden und aus der Liste entfernt.";
        MissingFileNotice.Visibility = Visibility.Visible;
        _missingFileNoticeTimer.Stop();
        _missingFileNoticeTimer.Start();
    }

    private void MissingFileNoticeTimer_Tick(object? sender, EventArgs e)
    {
        _missingFileNoticeTimer.Stop();
        MissingFileNotice.Visibility = Visibility.Collapsed;
    }

    private sealed record RecentPdfItem(string FullPath)
    {
        public string FileName => Path.GetFileName(FullPath);
    }
}
