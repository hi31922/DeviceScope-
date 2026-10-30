using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DeviceScope
{
    public sealed class DetailRow
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }

    public sealed class App : Application
    {
        [STAThread]
        public static void Main() { new App().Run(new MainWindow()); }
    }

    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<DeviceRow> devices = new ObservableCollection<DeviceRow>();
        private readonly ObservableCollection<DetailRow> details = new ObservableCollection<DetailRow>();
        private readonly DataGrid deviceGrid = new DataGrid();
        private readonly DataGrid detailGrid = new DataGrid();
        private readonly TextBox search = new TextBox();
        private readonly ComboBox category = new ComboBox();
        private readonly CheckBox problemsOnly = new CheckBox { Content = "Needs attention" };
        private readonly CheckBox changesOnly = new CheckBox { Content = "Changes only" };
        private readonly TextBlock summary = new TextBlock();
        private readonly TextBlock selected = new TextBlock();
        private readonly TextBlock footer = new TextBlock();
        private readonly TextBlock visibleCount = new TextBlock();
        private readonly ProcessesView processesView = new ProcessesView();
        private readonly DriverDiagnosticsView driverDiagnosticsView = new DriverDiagnosticsView();
        private readonly PerformanceView performanceView = new PerformanceView();
        private readonly DiskHealthView diskHealthView = new DiskHealthView();
        private readonly SystemSpecificationsView systemSpecificationsView = new SystemSpecificationsView();
        private readonly List<Button> actions = new List<Button>();
        private SnapshotReport inventory;
        private SnapshotReport baseline;
        private ComparisonResult comparison;
        private List<DeviceRow> displayRows = new List<DeviceRow>();
        private bool scanning, closed, changingFilters;

        public MainWindow()
        {
            Title = "System Information";
            Width = 700; Height = 580; MinWidth = 700; MinHeight = 580;
            SourceInitialized += (s, e) =>
            {
                // WPF dimensions are logical units. Keep the requested initial
                // outer size in physical pixels on scaled Windows desktops.
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                {
                    Matrix scale = source.CompositionTarget.TransformToDevice;
                    if (scale.M11 > 0 && scale.M22 > 0)
                    {
                        MinWidth = 700 / scale.M11; MinHeight = 580 / scale.M22;
                        Width = MinWidth; Height = MinHeight;
                    }
                }
            };
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Tahoma");
            FontSize = 11; Foreground = Color("#000000"); Background = Color("#D4D4D4");
            UiTheme.ApplyResources(this);
            using (Stream artwork = typeof(App).Assembly.GetManifestResourceStream("DeviceScope.Icon.ico"))
            {
                if (artwork != null)
                {
                    IconBitmapDecoder decoder = new IconBitmapDecoder(artwork, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    Icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                }
            }
            Content = BuildUi();
            driverDiagnosticsView.InventoryRefreshRequested += async (s, e) => await RefreshDevices();
            Loaded += async (s, e) => await RefreshDevices();
            Closed += (s, e) => { closed = true; processesView.Shutdown(); driverDiagnosticsView.Shutdown(); performanceView.Shutdown(); diskHealthView.Shutdown(); systemSpecificationsView.Shutdown(); };
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid { Background = Color("#D4D4D4") };
            TabControl tabs = new TabControl { Background = Color("#D4D4D4"), Margin = new Thickness(8), Padding = new Thickness(8) };
            tabs.Items.Add(new TabItem { Header = "Devices & Drivers", Content = BuildDevices() });
            tabs.Items.Add(new TabItem { Header = "Processes & RAM", Content = processesView });
            tabs.Items.Add(new TabItem { Header = "Driver Diagnostics", Content = driverDiagnosticsView });
            tabs.Items.Add(new TabItem { Header = "Live Performance", Content = performanceView });
            tabs.Items.Add(new TabItem { Header = "Disk Health", Content = diskHealthView });
            tabs.Items.Add(new TabItem { Header = "System Specifications", Content = systemSpecificationsView });
            root.Children.Add(tabs);
            return root;
        }

        private UIElement BuildDevices()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid toolbar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ActionButton("Refresh devices", async (s, e) => await RefreshDevices()));
            buttons.Children.Add(ActionButton("Export snapshot", (s, e) => ExportJson()));
            buttons.Children.Add(ActionButton("Compare snapshot", (s, e) => CompareSnapshot()));
            toolbar.Children.Add(buttons);
            search.MinHeight = 23; search.Width = 260; search.Padding = new Thickness(3, 2, 3, 2);
            search.Background = Brushes.White; search.Foreground = Brushes.Black; search.CaretBrush = Brushes.Black;
            search.BorderBrush = Color("#7F9DB9"); search.Margin = new Thickness(0);
            search.ToolTip = "Search device, manufacturer, hardware ID, driver or status";
            System.Windows.Automation.AutomationProperties.SetName(search, "Search devices");
            search.TextChanged += (s, e) => ApplyFilters();
            StackPanel searchPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock searchLabel = Text("Search", 11, "#000000", FontWeights.Normal); searchLabel.VerticalAlignment = VerticalAlignment.Center; searchLabel.Margin = new Thickness(0, 0, 6, 0);
            searchPanel.Children.Add(searchLabel); searchPanel.Children.Add(search); Grid.SetColumnSpan(searchPanel, 3); Grid.SetRow(searchPanel, 1); searchPanel.Margin = new Thickness(0, 5, 0, 0); toolbar.Children.Add(searchPanel);
            root.Children.Add(toolbar);

            Grid filters = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            WrapPanel left = new WrapPanel { Orientation = Orientation.Horizontal };
            category.Width = 156; category.Background = Brushes.White; category.Foreground = Brushes.Black; category.Margin = new Thickness(0, 0, 16, 0);
            category.Items.Add("All categories"); category.SelectedIndex = 0;
            category.SelectionChanged += (s, e) => { if (!changingFilters) ApplyFilters(); };
            System.Windows.Automation.AutomationProperties.SetName(category, "Device category");
            left.Children.Add(category);
            foreach (CheckBox check in new[] { problemsOnly, changesOnly })
            {
                check.Foreground = Color("#000000"); check.VerticalAlignment = VerticalAlignment.Center; check.Margin = new Thickness(0, 0, 16, 0);
                check.Checked += (s, e) => ApplyFilters(); check.Unchecked += (s, e) => ApplyFilters(); left.Children.Add(check);
            }
            filters.Children.Add(left);
            visibleCount.Foreground = Color("#555555"); visibleCount.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(visibleCount, 1); filters.Children.Add(visibleCount);
            Grid.SetRow(filters, 1); root.Children.Add(filters);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 90 });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 90 });
            Grid.SetRow(content, 2); root.Children.Add(content);

            Grid list = new Grid(); list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            summary.Text = "Ready to scan devices"; summary.Foreground = Color("#555555"); summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0, 0, 0, 9); list.Children.Add(summary);
            ConfigureGrid(deviceGrid);
            deviceGrid.SelectionMode = DataGridSelectionMode.Single;
            deviceGrid.Columns.Add(Column("Device", "Name", 2.1));
            deviceGrid.Columns.Add(Column("Category", "Class", .8));
            deviceGrid.Columns.Add(Column("Device Type", "DeviceType", 1.25));
            deviceGrid.Columns.Add(Column("Driver service", "Service", 1));
            deviceGrid.Columns.Add(Column("Status", "Status", 1.1));
            deviceGrid.Columns.Add(Column("Comparison", "Comparison", 1));
            deviceGrid.ItemsSource = devices; deviceGrid.SelectionChanged += (s, e) => ShowDetails(deviceGrid.SelectedItem as DeviceRow);
            Grid.SetRow(deviceGrid, 1); list.Children.Add(deviceGrid); content.Children.Add(list);

            GridSplitter splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Color("#B0B0B0"), ResizeDirection = GridResizeDirection.Rows }; Grid.SetRow(splitter, 1); content.Children.Add(splitter);
            Grid detail = new Grid(); detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            selected.Text = "Select a device"; selected.Foreground = Color("#000000"); selected.FontSize = 11; selected.FontWeight = FontWeights.SemiBold; selected.TextWrapping = TextWrapping.Wrap; selected.Margin = new Thickness(4, 0, 0, 9);
            detail.Children.Add(selected); ConfigureGrid(detailGrid);
            detailGrid.Columns.Add(Column("Property", "Label", .8)); detailGrid.Columns.Add(Column("Value", "Value", 1.6)); detailGrid.ItemsSource = details;
            Grid.SetRow(detailGrid, 1); detail.Children.Add(detailGrid); Grid.SetRow(detail, 2); content.Children.Add(detail);

            footer.Foreground = Color("#555555"); footer.FontSize = 11; footer.Margin = new Thickness(2, 8, 2, 0); footer.TextWrapping = TextWrapping.Wrap;
            footer.Text = "Local, read-only inventory.";
            ScrollViewer messages = new ScrollViewer { Content = footer, MaxHeight = 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(messages, 3); root.Children.Add(messages);
            return root;
        }

        private static void ConfigureGrid(DataGrid grid)
        {
            UiTheme.ConfigureGrid(grid);
        }

        private static DataGridTextColumn Column(string title, string path, double width)
        {
            Style text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, path == "Value" ? TextWrapping.Wrap : TextWrapping.NoWrap)); text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); text.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path))); text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3)));
            return new DataGridTextColumn { Header = title, Binding = new Binding(path), SortMemberPath = path, Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = title == "Device Type" ? 110 : Math.Max(55, title.Length * 6 + 18), ElementStyle = text };
        }

        private async Task RefreshDevices()
        {
            if (scanning || closed) return;
            scanning = true; SetActions(false); footer.Text = "Scanning devices and drivers... You can use the Processes & RAM tab meanwhile.";
            try
            {
                SnapshotReport updated = await Task.Run(() => InventoryReader.Capture());
                if (closed) return;
                inventory = updated;
                UpdateRows(); UpdateCategories(); ApplyFilters();
                driverDiagnosticsView.UpdateInventory(updated);
                footer.Text = "Scanned " + DateTime.Now.ToString("HH:mm:ss") + ". " + (updated.Warnings.Count == 0 ? "Select a device for details." : String.Join(" ", updated.Warnings));
            }
            catch (Exception ex)
            {
                if (!closed)
                {
                    if (inventory == null) summary.Text = "Device scan unavailable";
                    footer.Text = "Device scan failed" + (inventory == null ? "" : "; previous inventory is still shown") + ": " + ex.Message;
                    driverDiagnosticsView.SetInventoryError(ex.Message);
                }
            }
            finally { scanning = false; if (!closed) SetActions(true); }
        }

        private void SetActions(bool enabled)
        {
            for (int i = 0; i < actions.Count; i++) actions[i].IsEnabled = enabled && (i == 0 || inventory != null);
        }

        private void UpdateRows()
        {
            if (inventory == null) return;
            if (baseline == null) { comparison = null; displayRows = inventory.Devices; }
            else { comparison = SnapshotStore.Compare(inventory, baseline); displayRows = comparison.Rows; }
            int errors = inventory.Devices.Count(d => d.HasProblem);
            summary.Text = inventory.Devices.Count + " devices  |  " + errors + " need attention";
            if (comparison != null) summary.Text += "\nCompared: " + comparison.Added + " new, " + comparison.Changed + " changed, " + comparison.Removed + " removed";
        }

        private void UpdateCategories()
        {
            string previous = category.SelectedItem as string;
            changingFilters = true; category.Items.Clear(); category.Items.Add("All categories");
            foreach (string name in displayRows.Select(d => d.Class ?? "Other").Distinct().OrderBy(s => s)) category.Items.Add(name);
            category.SelectedItem = previous != null && category.Items.Contains(previous) ? previous : "All categories";
            changingFilters = false;
        }

        private void ApplyFilters()
        {
            if (changingFilters) return;
            string query = (search.Text ?? "").Trim(); string group = category.SelectedItem as string;
            DeviceRow old = deviceGrid.SelectedItem as DeviceRow;
            IEnumerable<DeviceRow> shown = displayRows;
            if (!String.IsNullOrEmpty(group) && group != "All categories") shown = shown.Where(d => (d.Class ?? "Other") == group);
            if (problemsOnly.IsChecked == true) shown = shown.Where(d => !d.IsRemoved && d.HasProblem);
            if (changesOnly.IsChecked == true) shown = shown.Where(d => d.Comparison != "Unchanged" && d.Comparison != "Not compared");
            if (query.Length > 0) shown = shown.Where(d => String.Join(" ", new[] { d.Name, d.Class, d.DeviceType, d.TypeEvidence, d.EnumeratorName, d.Manufacturer, d.DeviceId, d.HardwareIds, d.Status, d.DriverProvider, d.DriverVersion, d.Comparison }).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            // WPF selection inspects the view during collection changes, so do not
            // defer refresh while clearing/adding observable rows.
            devices.Clear(); foreach (DeviceRow row in shown.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)) devices.Add(row);
            deviceGrid.SelectedItem = old == null ? null : devices.FirstOrDefault(d => String.Equals(old.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase));
            visibleCount.Text = devices.Count + " shown";
        }

        private void ShowDetails(DeviceRow row)
        {
            details.Clear(); selected.Text = row == null ? "Select a device" : row.Name;
            if (row == null) return;
            Add("Status", row.IsRemoved ? "Absent from the current scan" : row.Status);
            Add("Error code", row.ErrorCode.HasValue ? row.ErrorCode.Value.ToString() : "Unknown");
            Add("Comparison", row.Comparison); if (!String.IsNullOrEmpty(row.ChangeDetails)) Add("What changed", row.ChangeDetails);
            Add("Category", row.Class); Add("Manufacturer", row.Manufacturer); Add("Device instance ID", row.DeviceId);
            Add("Device type", row.DeviceType ?? "Unknown"); Add("Classification basis", row.TypeEvidence);
            Add("Enumerator", row.EnumeratorName); Add("Parent device instance ID", row.ParentDeviceId); Add("Setup class GUID", row.ClassGuid); Add("Bus type GUID", row.BusTypeGuid);
            if (row.NetworkCharacteristics.HasValue) Add("Network Characteristics", "0x" + row.NetworkCharacteristics.Value.ToString("X"));
            Add("Hardware IDs", row.HardwareIds); Add("Service", row.Service); Add("Driver provider", row.DriverProvider);
            Add("Driver version", row.DriverVersion); Add("Driver date", row.DriverDate); Add("Driver INF", row.DriverInf); Add("Driver signed", row.DriverSigned);
            if (String.Equals(row.PnpClass, "Net", StringComparison.OrdinalIgnoreCase) || row.Class == "Network")
            {
                Add("Connection name", row.ConnectionName); Add("MAC address", row.MacAddress); Add("Link state", row.LinkState); Add("Link speed", row.LinkSpeed);
            }
        }
        private void Add(string label, string value) { details.Add(new DetailRow { Label = label, Value = String.IsNullOrWhiteSpace(value) ? "Unavailable" : value }); }

        private void ExportJson()
        {
            if (inventory == null || scanning) return;
            SaveFileDialog dialog = new SaveFileDialog { Filter = "DeviceScope snapshot (*.json)|*.json", FileName = "DeviceScope-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
            if (dialog.ShowDialog(this) != true) return;
            try { SnapshotStore.Save(dialog.FileName, inventory); footer.Text = "Saved: " + dialog.FileName; }
            catch (Exception ex) { footer.Text = "Could not save snapshot: " + ex.Message; }
        }

        private void CompareSnapshot()
        {
            if (inventory == null || scanning) return;
            OpenFileDialog dialog = new OpenFileDialog { Filter = "DeviceScope snapshot (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                SnapshotReport loaded = SnapshotStore.Load(dialog.FileName);
                baseline = loaded; UpdateRows(); UpdateCategories(); ApplyFilters();
                footer.Text = "Baseline: " + loaded.GeneratedAt + ". Removed devices are included in the list; select a changed device for old and new values.";
            }
            catch (Exception ex) { footer.Text = "Could not compare snapshot: " + ex.Message; }
        }

        private Button ActionButton(string caption, RoutedEventHandler handler)
        {
            Button button = new Button { Content = caption, Margin = new Thickness(0, 0, 6, 0) };
            UiTheme.StyleButton(button);
            button.Click += handler; actions.Add(button); return button;
        }
        private static SolidColorBrush Color(string hex) { SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); brush.Freeze(); return brush; }
        private static TextBlock Text(string value, double size, string color, FontWeight weight) { return new TextBlock { Text = value, FontSize = size, Foreground = Color(color), FontWeight = weight }; }
    }
}
