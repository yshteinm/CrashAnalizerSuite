using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace CrashAnalyzer.WpfApp;

public partial class MainWindow : Window
{
    private readonly WpfCrashAnalysisService _analysisService;
    private CancellationTokenSource? _askCancellation;
    private bool _isAsking;

    public MainWindow()
    {
        InitializeComponent();
        _analysisService = new WpfCrashAnalysisService();
    }

    private void OpenCrashDumpButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Crash Dump",
            Filter = "Dump files (*.dmp)|*.dmp|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        DmpStatusTextBlock.Text = $"DMP loaded: {dialog.FileName}";
        OutputTextBox.Text = _analysisService.AnalyzeDump(dialog.FileName);
    }

    private void OpenPdbButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Symbols (PDB)",
            Filter = "Program database (*.pdb)|*.pdb|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        PdbStatusTextBlock.Text = _analysisService.SetPdbPath(dialog.FileName);

        var refreshed = _analysisService.ReanalyzeLoadedDump();
        if (!string.IsNullOrWhiteSpace(refreshed))
        {
            OutputTextBox.Text = refreshed;
        }
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAsking)
        {
            _askCancellation?.Cancel();
            return;
        }

        await RunAskAsync();
    }

    private async void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        if (_isAsking)
        {
            _askCancellation?.Cancel();
            return;
        }

        await RunAskAsync();
    }

    private async Task RunAskAsync()
    {
        _isAsking = true;
        _askCancellation = new CancellationTokenSource();
        AskButton.Content = "Cancel";
        PromptAnswerTextBlock.Text = "Thinking...";
        PromptAnswerTextBlock.Foreground = SystemColors.ControlTextBrush;
        ThinkingProgressBar.Visibility = Visibility.Visible;
        await System.Windows.Threading.Dispatcher.Yield();

        try
        {
            var question = PromptTextBox.Text;
            PromptAnswerTextBlock.Text = await _analysisService.AskAsync(question, _askCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            PromptAnswerTextBlock.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            PromptAnswerTextBlock.Text = $"Prompt failed: {ex.Message}";
            PromptAnswerTextBlock.Foreground = Brushes.IndianRed;
        }
        finally
        {
            _askCancellation?.Dispose();
            _askCancellation = null;
            _isAsking = false;
            ThinkingProgressBar.Visibility = Visibility.Collapsed;
            AskButton.Content = "Ask";
        }
    }
}
