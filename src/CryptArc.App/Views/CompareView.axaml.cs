using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using CryptArc.App.ViewModels;
using CryptArc.Core;
using ScottPlot;
using ScottPlot.Plottables;

namespace CryptArc.App.Views;

/// <summary>
/// The "Compare codecs" page. The chart is drawn here rather than bound, because
/// ScottPlot is an imperative API and plot objects do not belong in a view model.
/// </summary>
public partial class CompareView : UserControl
{
    private static readonly Color Primary = Color.FromHex(ThemePalette.PrimaryHex);
    private static readonly Color PrimaryLight = Color.FromHex("#93B4DB");
    private static readonly Color Neutral = Color.FromHex(ThemePalette.SecondaryTextHex);

    private static readonly string StoreName = CodecRegistry.Get(CodecId.Store).Name;

    private ComparePageViewModel? _viewModel;

    /// <summary>Creates the view; the shell's data template supplies the view model.</summary>
    public CompareView()
    {
        InitializeComponent();

        // The chart is a fixed picture of the results. ScottPlot pans on drag and
        // zooms on scroll or pinch by default, and once moved there is nothing in
        // the UI to put it back, so all mouse and trackpad input is switched off.
        Chart.UserInputProcessor.Disable();

        StyleChart();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Results.CollectionChanged -= OnResultsChanged;
        }

        _viewModel = DataContext as ComparePageViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Results.CollectionChanged += OnResultsChanged;
        }

        Redraw();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComparePageViewModel.ShowSpeed)
            or nameof(ComparePageViewModel.RecommendedCodec))
            Redraw();
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void StyleChart()
    {
        Plot plot = Chart.Plot;
        plot.FigureBackground.Color = Colors.Transparent;
        plot.DataBackground.Color = Colors.Transparent;
        plot.Axes.Color(Neutral);
        plot.Grid.MajorLineColor = Neutral.WithAlpha(0.2);
        plot.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        plot.Axes.Top.FrameLineStyle.Width = 0;
        plot.Axes.Right.FrameLineStyle.Width = 0;
        plot.Axes.Left.FrameLineStyle.Width = 0;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 13;

        // Keep the tick length as a gap between the axis and the codec names,
        // but draw the ticks themselves invisible.
        plot.Axes.Bottom.MajorTickStyle.Length = 8;
        plot.Axes.Bottom.MajorTickStyle.Color = Colors.Transparent;
        plot.Axes.Left.MinorTickStyle.Length = 0;
    }

    private void Redraw()
    {
        Plot plot = Chart.Plot;
        plot.Clear();

        if (_viewModel is null || _viewModel.Results.Count == 0)
        {
            Chart.Refresh();
            return;
        }

        // Store is a plain copy at memory speed; on the speed chart it would flatten
        // every real codec into the axis, so it is left out there (the table keeps it).
        var shown = new List<CodecResult>(_viewModel.Results.Count);
        foreach (CodecResult result in _viewModel.Results)
            if (!_viewModel.ShowSpeed || result.Codec != StoreName)
                shown.Add(result);

        int count = shown.Count;
        double[] values = new double[count];
        double[] positions = new double[count];
        string[] labels = new string[count];

        for (int i = 0; i < count; i++)
        {
            CodecResult result = shown[i];
            values[i] = _viewModel.ShowSpeed ? result.EncodeThroughput : result.Ratio * 100;
            positions[i] = i;
            labels[i] = result.Codec;
        }

        BarPlot bars = plot.Add.Bars(values);
        for (int i = 0; i < count; i++)
        {
            Bar bar = bars.Bars[i];
            bool recommended = shown[i].Codec == _viewModel.RecommendedCodec;
            bar.FillColor = recommended ? Primary : PrimaryLight;
            bar.LineWidth = 0;
            bar.Size = 0.6;
            bar.Label = _viewModel.ShowSpeed ? $"{values[i]:F1}" : $"{values[i]:F1} %";
        }

        bars.ValueLabelStyle.ForeColor = Neutral;
        bars.ValueLabelStyle.FontSize = 12;

        plot.Axes.Bottom.SetTicks(positions, labels);
        plot.Axes.Margins(bottom: 0, top: 0.15);
        Chart.Refresh();
    }
}
