using System;
using System.ComponentModel;
using Avalonia.Controls;
using CryptArc.App.Converters;
using CryptArc.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;

namespace CryptArc.App.Views;

/// <summary>
/// The "Analyse" page. The byte histogram is drawn here from the view model's counts;
/// ScottPlot is imperative and stays out of the view model.
/// </summary>
public partial class AnalyseView : UserControl
{
    private static readonly Color Text = Color.FromHex(ThemePalette.PrimaryHex);
    private static readonly Color Other = Color.FromHex(ThemePalette.OtherByteHex);
    private static readonly Color Neutral = Color.FromHex(ThemePalette.SecondaryTextHex);

    private AnalysePageViewModel? _viewModel;

    /// <summary>Creates the view; the shell's data template supplies the view model.</summary>
    public AnalyseView()
    {
        InitializeComponent();
        Legend.ItemsSource = EntropyToBrushConverter.Legend;

        // A fixed picture, like the other charts: no pan or zoom.
        HistogramChart.UserInputProcessor.Disable();
        StyleChart();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as AnalysePageViewModel;

        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Redraw();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysePageViewModel.Histogram)
            or nameof(AnalysePageViewModel.HistogramAxisTitle))
            Redraw();
    }

    private void StyleChart()
    {
        Plot plot = HistogramChart.Plot;
        plot.FigureBackground.Color = Colors.Transparent;
        plot.DataBackground.Color = Colors.Transparent;
        plot.Axes.Color(Neutral);
        plot.Grid.MajorLineColor = Neutral.WithAlpha(0.2);
        plot.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Left.FrameLineStyle.Width = 0;
        plot.Axes.Left.MinorTickStyle.Length = 0;
        plot.Axes.Bottom.MinorTickStyle.Length = 0;

        // Hex ticks at the boundaries that matter: control bytes, printable ASCII, high bytes.
        plot.Axes.Bottom.SetTicks([0, 0x20, 0x7F, 0xC0, 0xFF], ["00", "20", "7F", "C0", "FF"]);
    }

    private void Redraw()
    {
        Plot plot = HistogramChart.Plot;
        plot.Clear();

        long[] counts = _viewModel?.Histogram ?? [];
        if (counts.Length == 0)
        {
            HistogramChart.Refresh();
            return;
        }

        long total = 0;
        foreach (long count in counts)
            total += count;

        // Share of the file in percent, so files of any size read the same way.
        double[] shares = new double[counts.Length];
        for (int value = 0; value < counts.Length; value++)
            shares[value] = total == 0 ? 0 : counts[value] * 100.0 / total;

        BarPlot bars = plot.Add.Bars(shares);
        for (int value = 0; value < counts.Length; value++)
        {
            Bar bar = bars.Bars[value];
            bar.FillColor = IsTextByte(value) ? Text : Other;
            bar.LineWidth = 0;
            bar.Size = 1;
        }

        plot.Axes.Left.Label.Text = _viewModel?.HistogramAxisTitle ?? string.Empty;
        plot.Axes.Left.Label.FontSize = 12;
        plot.Axes.Left.Label.ForeColor = Neutral;
        plot.Axes.Margins(bottom: 0, top: 0.1);
        HistogramChart.Refresh();
    }

    // The same definition DataAnalyzer uses for its text share.
    private static bool IsTextByte(int value) =>
        value is >= 0x20 and <= 0x7E or '\t' or '\n' or '\r';
}
