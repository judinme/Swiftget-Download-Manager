using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Controls;

public sealed partial class SegmentedProgressBar : UserControl
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(IEnumerable<SegmentProgressViewModel>), typeof(SegmentedProgressBar),
            new PropertyMetadata(null, OnSegmentsChanged));

    public static readonly DependencyProperty OverallProgressProperty =
        DependencyProperty.Register(nameof(OverallProgress), typeof(double), typeof(SegmentedProgressBar),
            new PropertyMetadata(0.0, OnOverallProgressChanged));

    public IEnumerable<SegmentProgressViewModel>? Segments
    {
        get => (IEnumerable<SegmentProgressViewModel>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double OverallProgress
    {
        get => (double)GetValue(OverallProgressProperty);
        set => SetValue(OverallProgressProperty, value);
    }

    public SegmentedProgressBar()
    {
        InitializeComponent();
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedProgressBar control)
        {
            if (e.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= control.OnCollectionChanged;
            }

            if (e.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += control.OnCollectionChanged;
            }

            control.RenderSegments();
        }
    }

    private static void OnOverallProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedProgressBar control && e.NewValue is double val)
        {
            control.OverallProgressBar.Value = Math.Clamp(val, 0, 100);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderSegments();
    }

    private void SegmentsCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderSegments();
    }

    public void RenderSegments()
    {
        SegmentsCanvas.Children.Clear();
        if (Segments == null)
        {
            return;
        }

        double width = SegmentsCanvas.ActualWidth;
        double height = SegmentsCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        SolidColorBrush unfilledBrush = new(ColorHelper.FromArgb(60, 0, 120, 215));
        SolidColorBrush filledBrush = new(ColorHelper.FromArgb(230, 0, 120, 215));
        SolidColorBrush dividerBrush = new(ColorHelper.FromArgb(180, 255, 255, 255));

        foreach (SegmentProgressViewModel seg in Segments)
        {
            double segX = seg.LeftRatio * width;
            double segW = Math.Max(1.0, seg.WidthRatio * width);

            // 1. Background range block
            Rectangle rangeRect = new()
            {
                Width = segW,
                Height = height,
                Fill = unfilledBrush
            };
            Canvas.SetLeft(rangeRect, segX);
            Canvas.SetTop(rangeRect, 0);
            SegmentsCanvas.Children.Add(rangeRect);

            // 2. Downloaded fill block
            double fillW = Math.Max(0.0, Math.Min(segW, seg.FillRatio * segW));
            if (fillW > 0)
            {
                Rectangle fillRect = new()
                {
                    Width = fillW,
                    Height = height,
                    Fill = filledBrush
                };
                Canvas.SetLeft(fillRect, segX);
                Canvas.SetTop(fillRect, 0);
                SegmentsCanvas.Children.Add(fillRect);
            }

            // 3. Segment boundary divider
            Rectangle divider = new()
            {
                Width = 1,
                Height = height,
                Fill = dividerBrush,
                Opacity = 0.5
            };
            Canvas.SetLeft(divider, segX + segW);
            Canvas.SetTop(divider, 0);
            SegmentsCanvas.Children.Add(divider);
        }
    }
}
