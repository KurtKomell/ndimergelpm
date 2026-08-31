using System.Windows;

namespace NdiMerger.App;

public partial class BrowserSourceDialog : Window
{
    public string Url { get; private set; } = "https://";
    public int WidthPx { get; private set; } = 1920;
    public int HeightPx { get; private set; } = 1080;

    public BrowserSourceDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url) || url is "https://" or "http://")
        {
            MessageBox.Show(this, "Please enter a URL.", "Browser Source",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(WidthBox.Text?.Trim(), out var w) || w < 1 || w > 7680)
        {
            MessageBox.Show(this, "Width must be between 1 and 7680.", "Browser Source",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(HeightBox.Text?.Trim(), out var h) || h < 1 || h > 4320)
        {
            MessageBox.Show(this, "Height must be between 1 and 4320.", "Browser Source",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Url = url;
        WidthPx = w;
        HeightPx = h;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
