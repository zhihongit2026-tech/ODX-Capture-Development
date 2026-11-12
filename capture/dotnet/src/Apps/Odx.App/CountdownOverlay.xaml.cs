using System.Windows;

namespace Odx.App;

/// <summary>The countdown, recording timer, "END" and processing phases, all excluded from capture (<see cref="Overlay"/>).</summary>
public partial class CountdownOverlay : Window
{
    public CountdownOverlay() => InitializeComponent();

    public int Seconds
    {
        set => Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Reminder
    {
        set
        {
            ReminderText.Text = value;
            ReminderText.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string Text
    {
        set
        {
            Number.Text = value;
            Number.FontSize = value.Length <= 2 ? 110 : value.Length <= 10 ? 64 : 44;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Overlay.Apply(this);
    }
}
