using System.Windows;

namespace SourceUnpack.App.Views;

public partial class WizardWindow : Window
{
    public string GameDirectory { get; private set; } = string.Empty;
    public string OutputDirectory { get; private set; } = string.Empty;

    public WizardWindow()
    {
        InitializeComponent();
        
        // Default output path
        OutputDirBox.Text = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SourceUnpack_Output");
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select Game Directory" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            GameDirBox.Text = dialog.SelectedPath;
        }
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select Output Directory" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputDirBox.Text = dialog.SelectedPath;
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        GameDirectory = GameDirBox.Text;
        OutputDirectory = OutputDirBox.Text;
        DialogResult = true;
        Close();
    }
}
