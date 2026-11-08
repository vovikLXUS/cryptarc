using Avalonia.Controls;
using CryptArc.App.Converters;

namespace CryptArc.App.Views;

/// <summary>The "Analyse" page.</summary>
public partial class AnalyseView : UserControl
{
    /// <summary>Creates the view; the shell's data template supplies the view model.</summary>
    public AnalyseView()
    {
        InitializeComponent();
        Legend.ItemsSource = EntropyToBrushConverter.Legend;
    }
}
