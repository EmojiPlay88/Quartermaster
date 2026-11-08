using Avalonia.Headless.XUnit;

namespace Quartermaster.Desktop.Tests;

public class ShellTests
{
    [AvaloniaFact]
    public void Main_window_opens_headless()
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal("Quartermaster", window.Title);
        Assert.True(window.IsVisible);
        window.Close();
    }
}
