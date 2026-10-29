using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceScope
{
    public sealed class TrendChart : FrameworkElement
    {
        private sealed class PointValue { public DateTime Time; public double? Value; }
        private readonly List<PointValue> history = new List<PointValue>();
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double GapSeconds { get; set; }
        public Brush LineBrush { get; set; }
        public int SampleCount { get { return history.Count; } }
        public TrendChart() { Minimum = 0; Maximum = 100; GapSeconds = 4; LineBrush = UiTheme.Brush("#2563EB"); MinHeight = 90; }
        public void Clear() { history.Clear(); InvalidateVisual(); }
        public void Add(DateTime timeUtc, double? value)
        {
            if (value.HasValue && (Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))) value = null;
            history.Add(new PointValue { Time = timeUtc, Value = value });
            history.RemoveAll(point => point.Time < timeUtc.AddMinutes(-2));
            if (history.Count > 360) history.RemoveRange(0, history.Count - 360);
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double width = ActualWidth, height = ActualHeight;
            if (width <= 1 || height <= 1 || Maximum <= Minimum) return;
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            Pen grid = new Pen(UiTheme.Brush("#EDF2F8"), 1);
            for (int i = 0; i <= 4; i++) drawing.DrawLine(grid, new Point(0, height * i / 4), new Point(width, height * i / 4));
            for (int i = 0; i <= 6; i++) drawing.DrawLine(grid, new Point(width * i / 6, 0), new Point(width * i / 6, height));
            DateTime end = history.Count == 0 ? DateTime.UtcNow : history[history.Count - 1].Time;
            DateTime start = end.AddSeconds(-60);
            Pen line = new Pen(LineBrush, 2);
            Point? previous = null; DateTime previousTime = DateTime.MinValue;
            foreach (PointValue value in history)
            {
                if (value.Time < start) continue;
                if (!value.Value.HasValue) { previous = null; continue; }
                double x = (value.Time - start).TotalSeconds / 60.0 * width;
                double y = height - Math.Max(0, Math.Min(1, (value.Value.Value - Minimum) / (Maximum - Minimum))) * height;
                Point current = new Point(x, y);
                if (previous.HasValue && (value.Time - previousTime).TotalSeconds <= GapSeconds) drawing.DrawLine(line, previous.Value, current);
                else drawing.DrawEllipse(LineBrush, null, current, 2, 2);
                previous = current; previousTime = value.Time;
            }
        }
    }

    public sealed class PerformanceView : UserControl
    {
        private readonly PerformanceReader reader = new PerformanceReader();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer sensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private readonly CheckBox automatic = new CheckBox { Content = "Live updates", IsChecked = true };
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock sensorStatus = new TextBlock();
        private readonly TextBlock cpuValue = new TextBlock(), gpuValue = new TextBlock(), ramValue = new TextBlock(), temperatureValue = new TextBlock();
        private readonly TextBlock cpuNote = new TextBlock(), gpuNote = new TextBlock(), ramNote = new TextBlock();
        private readonly TrendChart cpuGraph = new TrendChart { LineBrush = Color("#2563EB") };
        private readonly TrendChart gpuGraph = new TrendChart { LineBrush = Color("#059669") };
        private readonly TrendChart ramGraph = new TrendChart { LineBrush = Color("#7C3AED") };
        private readonly TrendChart temperatureGraph = new TrendChart { Minimum = -20, Maximum = 150, GapSeconds = 35, LineBrush = Color("#E08716") };
        private readonly ComboBox sensors = new ComboBox { DisplayMemberPath = "DisplayName" };
        private readonly DataGrid gpuGrid = new DataGrid();
        private readonly Button refresh = new Button { Content = "Refresh now" };
        private bool busy, sensorBusy, closed, sensorsInitialized, updatingSensors;
        private bool intervalsStale = true;
        private int session;

        public PerformanceView()
        {
            Background = Color("#EDF2F8");
            Content = new ScrollViewer { Content = BuildUi(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            timer.Tick += (s, e) => Refresh(); sensorTimer.Tick += (s, e) => RefreshSensors();
            Loaded += (s, e) => UpdateTimer(); Unloaded += (s, e) => { timer.Stop(); sensorTimer.Stop(); session++; intervalsStale = true; };
            IsVisibleChanged += (s, e) => RestartSamplingSession();
        }
        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DockPanel toolbar = new DockPanel { Margin = new Thickness(3, 0, 3, 12) };
            StackPanel controls = new StackPanel { Orientation = Orientation.Horizontal };
            automatic.Foreground = Color("#18263B"); automatic.VerticalAlignment = VerticalAlignment.Center; automatic.Margin = new Thickness(14, 0, 16, 0);
            automatic.Checked += (s, e) => RestartSamplingSession(); automatic.Unchecked += (s, e) => RestartSamplingSession();
            StyleButton(refresh); refresh.Click += (s, e) => { Refresh(); RefreshSensors(); };
            controls.Children.Add(refresh); controls.Children.Add(automatic); DockPanel.SetDock(controls, Dock.Left); toolbar.Children.Add(controls);
            TextBlock title = Text("Real CPU, RAM, supported GPU load and readable sensors", 13, "#53647A"); title.VerticalAlignment = VerticalAlignment.Center; title.TextWrapping = TextWrapping.Wrap; toolbar.Children.Add(title); root.Children.Add(toolbar);

            Grid metrics = new Grid();
            metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Border cpu = MetricCard("CPU", cpuValue, cpuNote, cpuGraph); metrics.Children.Add(cpu);
            Border ram = MetricCard("RAM", ramValue, ramNote, ramGraph); Grid.SetColumn(ram, 1); metrics.Children.Add(ram);
            Border gpu = MetricCard("GPU", gpuValue, gpuNote, gpuGraph); Grid.SetRow(gpu, 1); metrics.Children.Add(gpu);
            Grid.SetRow(metrics, 1); root.Children.Add(metrics);

            Grid thermal = new Grid { Margin = new Thickness(12) };
            thermal.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            thermal.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            thermal.RowDefinitions.Add(new RowDefinition());
            thermal.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DockPanel thermalHeading = new DockPanel();
            temperatureValue.FontSize = 20; temperatureValue.Foreground = Color("#E08716"); temperatureValue.Text = "Unavailable";
            DockPanel.SetDock(temperatureValue, Dock.Right); thermalHeading.Children.Add(temperatureValue); thermalHeading.Children.Add(Text("Selected sensor", 13, "#53647A")); thermal.Children.Add(thermalHeading);
            sensors.Background = Brushes.White; sensors.Foreground = Brushes.Black; sensors.Margin = new Thickness(0, 8, 0, 8); sensors.MinHeight = 28;
            sensors.SelectionChanged += (s, e) => { if (!updatingSensors) { temperatureGraph.Clear(); AddSelectedTemperature(DateTime.UtcNow); } };
            Grid.SetRow(sensors, 1); thermal.Children.Add(sensors);
            Grid.SetRow(temperatureGraph, 2); thermal.Children.Add(temperatureGraph);
            sensorStatus.Foreground = Color("#53647A"); sensorStatus.FontSize = 11; sensorStatus.TextWrapping = TextWrapping.Wrap; sensorStatus.Margin = new Thickness(0, 7, 0, 0); sensorStatus.MaxHeight = 54;
            sensorStatus.Text = "Sensor availability depends on firmware and existing sensor providers.";
            Grid.SetRow(sensorStatus, 3); thermal.Children.Add(sensorStatus);
            Border thermalCard = Card(); thermalCard.Child = thermal; Grid.SetRow(thermalCard, 1); Grid.SetColumn(thermalCard, 1); metrics.Children.Add(thermalCard);

            Grid engines = new Grid { Margin = new Thickness(12) }; engines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); engines.RowDefinitions.Add(new RowDefinition());
            TextBlock enginesTitle = Text("GPU engine readings", 13, "#53647A"); enginesTitle.Margin = new Thickness(0, 0, 0, 9); engines.Children.Add(enginesTitle);
            ConfigureGrid(gpuGrid);
            gpuGrid.Columns.Add(new DataGridTextColumn { Header = "Engine / adapter identity", Binding = new Binding("Engine"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            gpuGrid.Columns.Add(new DataGridTextColumn { Header = "%", Binding = new Binding("Percent") { StringFormat = "N1" }, SortMemberPath = "Percent", Width = 65 });
            Grid.SetRow(gpuGrid, 1); engines.Children.Add(gpuGrid); Border engineCard = Card(); engineCard.MinHeight = 0; engineCard.Height = 175; engineCard.Child = engines;
            Grid.SetRow(engineCard, 2); root.Children.Add(engineCard);
            status.Foreground = Color("#53647A"); status.FontSize = 12; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(3, 7, 3, 0);
            status.Text = "Graphs show the last 60 seconds. CPU/GPU/RAM: about 1 second; sensors: about 10 seconds. Updates pause when this tab is hidden.";
            Grid.SetRow(status, 3); root.Children.Add(status);
            return root;
        }
        private static Border MetricCard(string title, TextBlock value, TextBlock note, TrendChart graph)
        {
            Grid grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DockPanel heading = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            value.Text = "Waiting..."; value.FontSize = 22; value.Foreground = graph.LineBrush;
            DockPanel.SetDock(value, Dock.Right); heading.Children.Add(value);
            TextBlock label = Text(title, 13, "#53647A"); label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.Wrap; heading.Children.Add(label); grid.Children.Add(heading);
            Grid.SetRow(graph, 1); grid.Children.Add(graph);
            note.TextWrapping = TextWrapping.Wrap; note.Foreground = Color("#53647A"); note.FontSize = 11; note.Margin = new Thickness(0, 8, 0, 0); note.MinHeight = 30; note.MaxHeight = 54;
            Grid.SetRow(note, 2); grid.Children.Add(note); Border card = Card(); card.Child = grid; return card;
        }
        private void RestartSamplingSession()
        {
            session++; intervalsStale = true; UpdateTimer();
        }
        private void UpdateTimer()
        {
            timer.Stop(); sensorTimer.Stop();
            if (closed || !IsLoaded || !IsVisible) return;
            if (automatic.IsChecked == true) { timer.Start(); sensorTimer.Start(); Refresh(); if (!sensorsInitialized) RefreshSensors(); }
        }
        private async void Refresh()
        {
            if (closed || busy || !IsVisible) return;
            busy = true; refresh.IsEnabled = false; int currentSession = session;
            bool resetIntervals = intervalsStale; intervalsStale = false;
            try
            {
                // Reset inside the next serialized read so an older in-flight
                // sample cannot re-prime the counters after a pause or tab change.
                PerformanceSample sample = await Task.Run(() => { if (resetIntervals) reader.ResetIntervals(); return reader.Capture(); });
                if (closed || currentSession != session || !IsVisible) return;
                ApplySample(sample);
            }
            catch (Exception ex) { if (!closed && currentSession == session && IsVisible) status.Text = "Performance sample unavailable: " + ex.Message; }
            finally { busy = false; if (!closed) refresh.IsEnabled = true; }
        }
        public void ApplySample(PerformanceSample sample)
        {
            cpuValue.Text = Percent(sample.CpuPercent); gpuValue.Text = Percent(sample.GpuPercent); ramValue.Text = Percent(sample.RamPercent);
            cpuNote.Text = sample.CpuNote; gpuNote.Text = sample.GpuNote; ramNote.Text = sample.RamNote;
            cpuNote.ToolTip = sample.CpuNote; gpuNote.ToolTip = sample.GpuNote;
            cpuGraph.Add(sample.TimestampUtc, sample.CpuPercent); gpuGraph.Add(sample.TimestampUtc, sample.GpuPercent); ramGraph.Add(sample.TimestampUtc, sample.RamPercent);
            gpuGrid.ItemsSource = sample.GpuEngines;
            status.Text = "Updated " + sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss") + ". Last 60 seconds; missing readings leave gaps. CPU/GPU/RAM ~1 sec, sensors ~10 sec. Updates pause when this tab is hidden.";
        }
        private async void RefreshSensors()
        {
            if (closed || sensorBusy || !IsVisible) return;
            sensorBusy = true; int currentSession = session;
            try
            {
                TemperatureSnapshot snapshot = await Task.Run(() => TemperatureReader.Capture());
                if (closed || currentSession != session || !IsVisible) return;
                ApplyTemperatures(snapshot);
            }
            catch (Exception ex) { if (!closed && currentSession == session && IsVisible) sensorStatus.Text = "Temperature read failed: " + ex.Message; }
            finally { sensorBusy = false; }
        }
        public void ApplyTemperatures(TemperatureSnapshot snapshot)
        {
            TemperatureReading previous = sensors.SelectedItem as TemperatureReading;
            string selectedId = previous == null ? null : previous.Id;
            updatingSensors = true;
            sensors.ItemsSource = snapshot.Readings;
            sensors.SelectedItem = snapshot.Readings.FirstOrDefault(sensor => sensor.Id == selectedId) ?? snapshot.Readings.FirstOrDefault();
            updatingSensors = false; sensorsInitialized = true;
            TemperatureReading selected = sensors.SelectedItem as TemperatureReading;
            if (selected == null || selected.Id != selectedId) temperatureGraph.Clear();
            AddSelectedTemperature(snapshot.TimestampUtc);
            sensorStatus.Text = snapshot.Readings.Count + " sensor(s). " + (snapshot.Notes.Count == 0 ? "Values are labelled by their actual provider." : String.Join(" | ", snapshot.Notes));
            sensorStatus.ToolTip = sensorStatus.Text;
        }
        private void AddSelectedTemperature(DateTime time)
        {
            TemperatureReading selected = sensors.SelectedItem as TemperatureReading;
            temperatureValue.Text = selected == null ? "Unavailable" : selected.Celsius.ToString("N1") + " °C";
            temperatureGraph.Add(time, selected == null ? (double?)null : selected.Celsius);
        }
        private static string Percent(double? value) { return value.HasValue ? value.Value.ToString("N1") + "%" : "Unavailable"; }
        public void Shutdown() { closed = true; session++; timer.Stop(); sensorTimer.Stop(); reader.Dispose(); }
        private static Border Card() { return new Border { Margin = new Thickness(3, 0, 3, 8), MinHeight = 205, Background = Brushes.White, BorderBrush = Color("#DBE3EF"), BorderThickness = new Thickness(1) }; }
        private static TextBlock Text(string text, double size, string hex) { return new TextBlock { Text = text, FontSize = size, Foreground = Color(hex) }; }
        private static SolidColorBrush Color(string hex) { return UiTheme.Brush(hex); }
        private static void StyleButton(Button button) { UiTheme.StyleButton(button); }
        private static void ConfigureGrid(DataGrid grid) { UiTheme.ConfigureGrid(grid); }
    }
}
