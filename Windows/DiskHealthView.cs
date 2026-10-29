using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceScope
{
    public sealed partial class DiskHealthView : UserControl
    {
        private readonly TextBlock summary = new TextBlock();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock smartSummary = new TextBlock();
        private readonly TextBox rawPayload = new TextBox();
        private readonly DataGrid disks = new DataGrid(), diskDetails = new DataGrid(), volumes = new DataGrid(), smart = new DataGrid();
        private readonly ComboBox smartInstances = new ComboBox();
        private readonly Button refresh = new Button();
        private readonly CheckBox automatic = new CheckBox();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private CancellationTokenSource pending;
        private bool busy, closed, hasData;

        public DiskHealthView()
        {
            Background = Color("#EDF2F8"); Content = BuildUi();
            timer.Interval = TimeSpan.FromSeconds(30); timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => UpdateTimer(); IsVisibleChanged += (s, e) => UpdateTimer(); Unloaded += (s, e) => timer.Stop();
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summary.Text = "Storage health, reliability counters and volume space"; summary.FontSize = 15; summary.Foreground = Color("#18263B"); summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0, 0, 0, 9); root.Children.Add(summary);

            DockPanel toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            automatic.Content = "Auto-refresh (30 sec)"; automatic.IsChecked = false; automatic.Foreground = Color("#18263B"); automatic.VerticalAlignment = VerticalAlignment.Center; automatic.Margin = new Thickness(14, 0, 14, 0);
            automatic.Checked += (s, e) => UpdateTimer(); automatic.Unchecked += (s, e) => UpdateTimer();
            refresh.Content = "Refresh storage"; UiTheme.StyleButton(refresh); refresh.Click += (s, e) => Refresh();
            actions.Children.Add(refresh); actions.Children.Add(automatic); DockPanel.SetDock(actions, Dock.Left); toolbar.Children.Add(actions);
            TextBlock hint = new TextBlock { Text = "Read-only. Health and temperatures depend on the device and its driver.", Foreground = Color("#53647A"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; toolbar.Children.Add(hint);
            Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

            TabControl sections = new TabControl { Background = Color("#EDF2F8"), BorderThickness = new Thickness(0), Padding = new Thickness(0, 8, 0, 0) };
            sections.Items.Add(new TabItem { Header = "Physical disks", Content = BuildPhysical() });
            sections.Items.Add(new TabItem { Header = "Volume space", Content = BuildVolumes() });
            sections.Items.Add(new TabItem { Header = "ATA SMART / raw data", Content = BuildSmart() });
            Grid.SetRow(sections, 2); root.Children.Add(sections);
            status.Text = "Select Disk Health to read the available providers."; status.Foreground = Color("#53647A"); status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 10, 0, 0); status.FontSize = 12;
            ScrollViewer footer = new ScrollViewer { Content = status, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 115 }; Grid.SetRow(footer, 3); root.Children.Add(footer);
            return root;
        }

        private UIElement BuildPhysical()
        {
            Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star), MinHeight = 100 }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            Configure(disks); disks.SelectionMode = DataGridSelectionMode.Single;
            disks.Columns.Add(Column("Disk", "Name", 1.8)); disks.Columns.Add(Column("Type", "MediaType", .65)); disks.Columns.Add(Column("Capacity", "Capacity", .7)); disks.Columns.Add(Column("Windows health", "Health", 1.2)); disks.Columns.Add(Column("Temperature", "Temperature", 1));
            disks.SelectionChanged += (s, e) => { PhysicalDiskHealth row = disks.SelectedItem as PhysicalDiskHealth; diskDetails.ItemsSource = row == null ? null : row.Details; }; root.Children.Add(disks);
            GridSplitter splitter = new GridSplitter { Height = 4, Background = Color("#DBE3EF"), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows }; Grid.SetRow(splitter, 1); root.Children.Add(splitter);
            Configure(diskDetails); diskDetails.Columns.Add(Column("Property", "Label", .85)); diskDetails.Columns.Add(Column("Reported value", "Value", 1.35)); Grid.SetRow(diskDetails, 2); root.Children.Add(diskDetails);
            return root;
        }

        private UIElement BuildVolumes()
        {
            Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            TextBlock note = new TextBlock { Text = "Logical local volumes: used/free filesystem space. These are not guessed to correspond one-to-one to physical disks.", Foreground = Color("#53647A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) }; root.Children.Add(note);
            Configure(volumes); volumes.Columns.Add(Column("Volume", "Name", .6)); volumes.Columns.Add(Column("Filesystem", "FileSystem", .65)); volumes.Columns.Add(Column("Total", "Total", .8)); volumes.Columns.Add(Column("Free", "Free", .8)); volumes.Columns.Add(Column("Used", "Used", 1.2));
            FrameworkElementFactory bar = new FrameworkElementFactory(typeof(ProgressBar)); bar.SetValue(ProgressBar.MaximumProperty, 100.0); bar.SetValue(FrameworkElement.HeightProperty, 14.0); bar.SetValue(FrameworkElement.MarginProperty, new Thickness(8)); bar.SetBinding(ProgressBar.ValueProperty, new Binding("UsedPercent")); bar.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Used"));
            bar.SetValue(Control.ForegroundProperty, Color("#2563EB")); bar.SetValue(Control.BackgroundProperty, Color("#DFE8F5"));
            Style visible = new Style(typeof(ProgressBar)); DataTrigger unavailable = new DataTrigger { Binding = new Binding("HasUsage"), Value = false }; unavailable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed)); visible.Triggers.Add(unavailable); bar.SetValue(FrameworkElement.StyleProperty, visible);
            volumes.Columns.Add(new DataGridTemplateColumn { Header = "Space used", CellTemplate = new DataTemplate { VisualTree = bar }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); volumes.Columns.Add(Column("Status", "Status", 1)); Grid.SetRow(volumes, 1); root.Children.Add(volumes); return root;
        }

        private UIElement BuildSmart()
        {
            Grid root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            smartInstances.Background = Brushes.White; smartInstances.Foreground = Brushes.Black; smartInstances.MinHeight = 30; smartInstances.Margin = new Thickness(0, 0, 0, 8); smartInstances.DisplayMemberPath = "InstanceName"; smartInstances.SelectionChanged += (s, e) => ShowSmart(); root.Children.Add(smartInstances);
            smartSummary.Text = "No SMART provider instance selected."; smartSummary.Foreground = Color("#18263B"); smartSummary.TextWrapping = TextWrapping.Wrap; smartSummary.Margin = new Thickness(0, 0, 0, 9); Grid.SetRow(smartSummary, 1); root.Children.Add(smartSummary);
            Configure(smart); smart.Columns.Add(Column("Attribute", "Attribute", 1.5)); smart.Columns.Add(Column("Current", "Current", .65)); smart.Columns.Add(Column("Worst", "Worst", .65)); smart.Columns.Add(Column("Threshold", "Threshold", .8)); smart.Columns.Add(Column("Raw bytes (hex)", "RawHex", 1.15)); DataGridTextColumn rawNumber = Column("Raw unsigned value", "RawDecimal", 1); rawNumber.SortMemberPath = "RawValue"; smart.Columns.Add(rawNumber); Grid.SetRow(smart, 2); root.Children.Add(smart);
            rawPayload.IsReadOnly = true; rawPayload.Background = Brushes.White; rawPayload.Foreground = Brushes.Black; rawPayload.BorderBrush = Color("#DBE3EF"); rawPayload.FontFamily = new FontFamily("Consolas"); rawPayload.TextWrapping = TextWrapping.Wrap; rawPayload.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; rawPayload.Height = 120; rawPayload.Padding = new Thickness(8);
            Expander raw = new Expander { Header = "Uninterpreted provider bytes", Content = rawPayload, Foreground = Color("#18263B"), Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(raw, 3); root.Children.Add(raw); return root;
        }

        private void ShowSmart()
        {
            DiskSmartInstance instance = smartInstances.SelectedItem as DiskSmartInstance;
            smart.ItemsSource = instance == null ? null : instance.Attributes; rawPayload.Text = instance == null ? "" : instance.RawPayloadHex ?? "Unavailable";
            smartSummary.Text = instance == null ? "No readable SMART provider instances. Consult the availability messages below." : instance.Prediction + Environment.NewLine + instance.DataStatus + Environment.NewLine + "Instances are kept separate; no physical-disk identity is guessed from list order.";
        }
        private void UpdateTimer()
        {
            timer.Stop(); if (closed || !IsLoaded || !IsVisible) return;
            if (automatic.IsChecked == true) timer.Start(); if (!hasData) Refresh();
        }
        private async void Refresh()
        {
            if (closed || busy || !IsVisible) return;
            busy = true; refresh.IsEnabled = false; status.Text = "Reading local volumes and disk-health providers...";
            CancellationTokenSource cancellation = new CancellationTokenSource(); pending = cancellation;
            try
            {
                DiskHealthSnapshot snapshot = await Task.Run(() => DiskHealthReader.Capture(cancellation.Token));
                if (!closed) ApplySnapshot(snapshot);
            }
            catch (OperationCanceledException) { if (!closed) status.Text = "Disk scan cancelled."; }
            catch (Exception ex) { if (!closed) status.Text = "Disk scan failed; previous data remains visible: " + ex.Message; }
            finally { busy = false; if (!closed) refresh.IsEnabled = true; if (Object.ReferenceEquals(pending, cancellation)) pending = null; cancellation.Dispose(); }
        }
        private void ApplySnapshot(DiskHealthSnapshot snapshot)
        {
            PhysicalDiskHealth selected = disks.SelectedItem as PhysicalDiskHealth; string oldInstance = (smartInstances.SelectedItem as DiskSmartInstance ?? new DiskSmartInstance()).InstanceName;
            disks.ItemsSource = snapshot.Disks; disks.SelectedItem = selected == null ? snapshot.Disks.FirstOrDefault() : snapshot.Disks.FirstOrDefault(item => item.Id == selected.Id && item.Source == selected.Source) ?? snapshot.Disks.FirstOrDefault();
            volumes.ItemsSource = snapshot.Volumes; smartInstances.ItemsSource = snapshot.SmartInstances; smartInstances.SelectedItem = snapshot.SmartInstances.FirstOrDefault(item => item.InstanceName == oldInstance) ?? snapshot.SmartInstances.FirstOrDefault(); ShowSmart();
            summary.Text = snapshot.Disks.Count + " physical disk records  |  " + snapshot.Volumes.Count + " local volumes  |  " + snapshot.SmartInstances.Count + " SMART instances";
            status.Text = "Updated " + snapshot.CapturedAt.ToString("HH:mm:ss") + ". " + (snapshot.Warnings.Count == 0 ? "Only device-reported health is shown; no universal health percentage is inferred." : String.Join(Environment.NewLine, snapshot.Warnings)); hasData = true;
        }
        public void Shutdown() { if (closed) return; closed = true; timer.Stop(); if (pending != null) pending.Cancel(); }
        private static void Configure(DataGrid grid) { UiTheme.ConfigureGrid(grid); }
        private static DataGridTextColumn Column(string title, string path, double width)
        {
            Style text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6)));
            return new DataGridTextColumn { Header = title, Binding = new Binding(path), SortMemberPath = path, Width = new DataGridLength(width, DataGridLengthUnitType.Star), ElementStyle = text };
        }
        private static SolidColorBrush Color(string hex) { return UiTheme.Brush(hex); }
    }
}
