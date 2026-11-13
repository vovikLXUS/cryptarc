using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using CryptArc.App.Localization;
using CryptArc.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;

namespace CryptArc.App.Views;

/// <summary>
/// The "Naive vs optimised" page. The speedup chart is drawn here from the view
/// model's rows; ScottPlot is imperative and stays out of the view model.
/// </summary>
public partial class NaiveVsOptimisedView : UserControl
{
    private static readonly Color Faster = Color.FromHex(ThemePalette.PrimaryHex);
    private static readonly Color Slower = Color.FromHex("#D9534F");
    private static readonly Color Neutral = Color.FromHex(ThemePalette.SecondaryTextHex);

    private NaiveVsOptimisedPageViewModel? _viewModel;

    /// <summary>Creates the view; the shell's data template supplies the view model.</summary>
    public NaiveVsOptimisedView()
    {
        InitializeComponent();

        // A fixed picture of the results: no pan or zoom (same as the comparison chart).
        Chart.UserInputProcessor.Disable();
        StyleChart();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.Rows.CollectionChanged -= OnRowsChanged;

        _viewModel = DataContext as NaiveVsOptimisedPageViewModel;

        if (_viewModel is not null)
            _viewModel.Rows.CollectionChanged += OnRowsChanged;

        Redraw();
    }

    private static double Min(double[] values)
    {
        double min = double.MaxValue;
        foreach (double value in values)
            min = Math.Min(min, value);
        return min;
    }

    private static double Max(double[] values)
    {
        double max = double.MinValue;
        foreach (double value in values)
            max = Math.Max(max, value);
        return max;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void StyleChart()
    {
        Plot plot = Chart.Plot;
        plot.FigureBackground.Color = Colors.Transparent;
        plot.DataBackground.Color = Colors.Transparent;
        plot.Axes.Color(Neutral);
        plot.Grid.MajorLineColor = Neutral.WithAlpha(0.2);
        plot.Grid.YAxisStyle.MajorLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Bottom.FrameLineStyle.Width = 0;
        plot.Axes.Left.MajorTickStyle.Length = 8;
        plot.Axes.Left.MajorTickStyle.Color = Colors.Transparent;
        plot.Axes.Bottom.MinorTickStyle.Length = 0;
    }

    private void Redraw()
    {
        Plot plot = Chart.Plot;
        plot.Clear();

        int count = _viewModel?.Rows.Count ?? 0;
        ChartCard.Height = Math.Max(200, 90 + count * 34);

        if (_viewModel is null || count == 0)
        {
            Chart.Refresh();
            return;
        }

        // Speedups run from well under 1x to over 1000x, so the axis is log10: a bar
        // starts at 1x (log 0) and grows right when faster, left when slower.
        // Top row first: ScottPlot puts position 0 at the bottom.
        double[] values = new double[count];
        double[] speedups = new double[count];
        double[] positions = new double[count];
        string[] labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            PairRow row = _viewModel.Rows[i];
            int slot = count - 1 - i;
            speedups[slot] = row.Speedup;
            values[slot] = Math.Log10(row.Speedup);
            positions[slot] = slot;
            labels[slot] = $"{row.Case}  {row.Method}";
        }

        BarPlot bars = plot.Add.Bars(values);
        bars.Horizontal = true;
        for (int i = 0; i < count; i++)
        {
            Bar bar = bars.Bars[i];
            bar.FillColor = speedups[i] >= 1 ? Faster : Slower;
            bar.LineWidth = 0;
            bar.Size = 0.6;
            bar.Label = speedups[i] >= 1
                ? Localizer.Instance.Number(speedups[i], "0.##") + "×"
                : Localizer.Instance.Format("Pairs.Chart.Slower", 1 / speedups[i]);
        }

        bars.ValueLabelStyle.ForeColor = Neutral;
        bars.ValueLabelStyle.FontSize = 12;

        var same = plot.Add.VerticalLine(0);
        same.Color = Neutral;
        same.LineWidth = 1;
        same.LinePattern = LinePattern.Dashed;

        // Decade ticks, labelled as speedups rather than logarithms.
        double min = Math.Min(0, Math.Floor(Min(values)));
        double max = Math.Max(1, Math.Ceiling(Max(values)));
        int decades = (int)(max - min) + 1;
        double[] tickPositions = new double[decades];
        string[] tickLabels = new string[decades];
        for (int d = 0; d < decades; d++)
        {
            double exponent = min + d;
            tickPositions[d] = exponent;
            tickLabels[d] = Localizer.Instance.Number(Math.Pow(10, exponent), exponent >= 0 ? "0" : "0.###") + "×";
        }

        plot.Axes.Bottom.SetTicks(tickPositions, tickLabels);
        plot.Axes.Left.SetTicks(positions, labels);
        plot.Axes.Left.TickLabelStyle.FontSize = 12;
        plot.Axes.SetLimitsX(min - 0.05, max + 0.35);
        plot.Axes.SetLimitsY(-0.6, count - 0.4);
        Chart.Refresh();
    }
}
