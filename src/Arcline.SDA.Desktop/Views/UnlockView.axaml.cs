using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Arcline.SDA.Desktop.Views;

public sealed partial class UnlockView : UserControl
{
    public UnlockView() => InitializeComponent();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PassBox.Focus();
    }
}
