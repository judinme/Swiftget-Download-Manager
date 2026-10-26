using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwiftGet.App.ViewModels;

namespace SwiftGet.App.Controls;

public sealed partial class ConnectionTableControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<SegmentProgressViewModel>), typeof(ConnectionTableControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable<SegmentProgressViewModel>? ItemsSource
    {
        get => (IEnumerable<SegmentProgressViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ConnectionTableControl()
    {
        InitializeComponent();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ConnectionTableControl control)
        {
            control.ConnectionsList.ItemsSource = e.NewValue;
        }
    }
}
