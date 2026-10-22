using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Linkii.Player;

/// <summary>Premier lancement sans adresse (ni option, ni installeur) : on la demande une fois, elle est gardée pour l'instance.</summary>
internal sealed class ServerPrompt : Window
{
    private readonly TextBox box = new() { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6), FontSize = 14 };
    private readonly TextBlock error = new() { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private string? result;

    public ServerPrompt()
    {
        Title = "Linkii Player";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var ok = new Button { Content = "Continuer", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            result = InstanceStore.NormalizeServer(box.Text);
            if (result == null) { error.Text = "Adresse non valide (exemple : https://affichage.exemple.ch)."; error.Visibility = Visibility.Visible; return; }
            Close();
        };

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Adresse du serveur Linkii", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Elle est demandée une seule fois pour cette instance.", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(ok);
        Content = panel;
        Loaded += (_, _) => box.Focus();
    }

    public string? Ask() { ShowDialog(); return result; }
}
