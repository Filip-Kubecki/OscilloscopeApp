using System;
using System.Linq;
using Avalonia.Controls;

namespace Oscilloscope.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Testowy sinus, tylko po to, żeby sprawdzić, że wykres działa
        double[] xs = Enumerable.Range(0, 1000).Select(i => i / 100.0).ToArray();
        double[] ys = xs.Select(Math.Sin).ToArray();

        Plot.Plot.Add.Scatter(xs, ys);
        Plot.Refresh();
    }
}