using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace DeviceScope
{
    public sealed partial class DriverDiagnosticsView : UserControl
    {
        private readonly ObservableCollection<DeviceRow> rows = new ObservableCollection<DeviceRow>();
        private readonly ObservableCollection<DriverDependencyRow> dependencies = new ObservableCollection<DriverDependencyRow>();
        private readonly ObservableCollection<DeviceEventRow> events = new ObservableCollection<DeviceEventRow>();
        private readonly DataGrid deviceGrid = new DataGrid();
        private readonly DataGrid dependencyGrid = new DataGrid();
        private readonly DataGrid eventGrid = new DataGrid();
        private readonly TextBox search = new TextBox();
        private readonly TextBox diagnosis = ReadOnlyText();
        private readonly TextBox eventDetails = ReadOnlyText();
        private readonly TextBlock summary = Label("Waiting for the first device inventory.", 15);
        private readonly TextBlock selected = Label("Select a device", 16);
        private readonly TextBlock dependencyNote = Label("Select a device to inspect directly declared service dependencies.", 12);
        private readonly TextBlock eventNote = Label("Events are matched by exact device instance ID, not by a similar name.", 12);
        private readonly TextBlock status = Label("Read-only diagnostics. No driver, service or registry settings are changed.", 12);
        private readonly CheckBox problemsOnly = new CheckBox { Content = "Needs attention only", IsChecked = true };
        private readonly Button refresh = new Button();
        private readonly Button refreshInventory = new Button();
        public event EventHandler InventoryRefreshRequested;
        private List<DeviceRow> inventory = new List<DeviceRow>();
        private CancellationTokenSource activeRead;
        private int requestVersion;
        private bool closed, detailsStale, reading;

        public DriverDiagnosticsView()
        {
            Background = Color("#EDF2F8"); Content = BuildUi();
            Loaded += (s, e) => LoadIfVisible();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) LoadIfVisible();
                else if (reading) { detailsStale = true; CancelRead(); }
            };
        }

        public void UpdateInventory(SnapshotReport report)
        {
            if (closed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(new Action(() => UpdateInventory(report))); return; }
            refreshInventory.IsEnabled = true;
            if (report == null || report.Devices == null)
            {
                if (inventory.Count == 0) summary.Text = "Waiting for the first successful device inventory. Use Refresh in Devices & Drivers.";
                return;
            }
            inventory = report.Devices.Where(row => row != null && !row.IsRemoved).ToList();
            int errors = inventory.Count(row => row.HasProblem);
            int code28 = inventory.Count(row => row.ErrorCode == 28);
            summary.Text = inventory.Count + " devices  |  " + errors + " need attention  |  " + code28 + " Code 28";
            if (report.Warnings != null && report.Warnings.Count > 0) summary.Text += "  |  inventory has unavailable fields";
            ApplyFilter();
        }

        public void SetInventoryError(string error)
        {
            if (closed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(new Action(() => SetInventoryError(error))); return; }
            refreshInventory.IsEnabled = true;
            summary.Text = inventory.Count == 0 ? "Device inventory unavailable: " + error : "Latest device scan failed; previous inventory remains displayed. " + error;
            status.Text = "Refresh the inventory in Devices & Drivers to retry. Diagnostics cannot invent unavailable device information.";
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summary.Margin = new Thickness(0, 0, 0, 10); summary.Foreground = Color("#53647A"); summary.FontSize = 13; summary.TextWrapping = TextWrapping.Wrap; Grid.SetRow(summary, 1); root.Children.Add(summary);
            Grid toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            problemsOnly.Foreground = Color("#18263B"); problemsOnly.VerticalAlignment = VerticalAlignment.Center; problemsOnly.Margin = new Thickness(12, 0, 12, 0);
            problemsOnly.Checked += (s, e) => ApplyFilter(); problemsOnly.Unchecked += (s, e) => ApplyFilter();
            refresh.Content = "Inspect selected"; refresh.IsEnabled = false; UiTheme.StyleButton(refresh); refresh.Margin = new Thickness(7, 0, 0, 0);
            refresh.Click += async (s, e) => await RefreshSelected();
            refreshInventory.Content = "Refresh devices"; UiTheme.StyleButton(refreshInventory);
            refreshInventory.Click += (s, e) => { EventHandler handler = InventoryRefreshRequested; if (handler != null) { refreshInventory.IsEnabled = false; summary.Text = "Refreshing Windows device inventory..."; handler(this, EventArgs.Empty); } };
            buttons.Children.Add(refreshInventory); buttons.Children.Add(refresh); buttons.Children.Add(problemsOnly); toolbar.Children.Add(buttons);
            StackPanel searchPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            searchPanel.Children.Add(new TextBlock { Text = "Search", Foreground = Color("#18263B"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) });
            search.Background = Brushes.White; search.Foreground = Brushes.Black; search.CaretBrush = Brushes.Black; search.Padding = new Thickness(9, 6, 9, 6); search.Width = 210; search.BorderBrush = Color("#DBE3EF");
            search.ToolTip = "Search device name, hardware IDs, status, service, driver or error code";
            System.Windows.Automation.AutomationProperties.SetName(search, "Search driver diagnostics");
            search.TextChanged += (s, e) => ApplyFilter(); searchPanel.Children.Add(search); Grid.SetColumn(searchPanel, 2); toolbar.Children.Add(searchPanel);
            root.Children.Add(toolbar);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.35, GridUnitType.Star), MinHeight = 150 });
            ConfigureGrid(deviceGrid); deviceGrid.Columns.Add(Column("Device", "Name", 2)); deviceGrid.Columns.Add(Column("Device Type", "DeviceType", 1.25)); deviceGrid.Columns.Add(Column("Code", "ErrorCode", .5)); deviceGrid.Columns.Add(Column("Status", "Status", 1.2));
            deviceGrid.ItemsSource = rows; deviceGrid.SelectionChanged += (s, e) => SelectedChanged(); content.Children.Add(deviceGrid);
            GridSplitter splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Color("#DBE3EF"), ResizeDirection = GridResizeDirection.Rows }; Grid.SetRow(splitter, 1); content.Children.Add(splitter);
            Grid right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            selected.TextWrapping = TextWrapping.Wrap; selected.Margin = new Thickness(2, 0, 0, 8); selected.FontWeight = FontWeights.SemiBold; right.Children.Add(selected);
            TabControl sections = new TabControl { Background = Color("#EDF2F8"), BorderThickness = new Thickness(0), Padding = new Thickness(0, 8, 0, 0), Foreground = Color("#18263B") };
            sections.Items.Add(Section("Diagnosis", diagnosis));
            sections.Items.Add(Section("Dependencies", BuildDependencies())); sections.Items.Add(Section("Device Events", BuildEvents()));
            Grid.SetRow(sections, 1); right.Children.Add(sections); Grid.SetRow(right, 2); content.Children.Add(right);
            Grid.SetRow(content, 2); root.Children.Add(content);
            dependencyNote.Foreground = Color("#53647A"); eventNote.Foreground = Color("#53647A");
            status.Foreground = Color("#53647A"); status.Margin = new Thickness(2, 9, 2, 0); status.TextWrapping = TextWrapping.Wrap; Grid.SetRow(status, 3); root.Children.Add(status);
            return root;
        }

        private UIElement BuildDependencies()
        {
            Grid grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ConfigureGrid(dependencyGrid); dependencyGrid.Columns.Add(Column("Type", "Kind", .8)); dependencyGrid.Columns.Add(Column("Dependency", "Name", .9)); dependencyGrid.Columns.Add(Column("State", "State", .9)); dependencyGrid.Columns.Add(Column("Evidence / limits", "Detail", 1.8)); dependencyGrid.ItemsSource = dependencies; grid.Children.Add(dependencyGrid);
            dependencyNote.TextWrapping = TextWrapping.Wrap; dependencyNote.Margin = new Thickness(2, 8, 2, 0); Grid.SetRow(dependencyNote, 1); grid.Children.Add(dependencyNote); return grid;
        }

        private UIElement BuildEvents()
        {
            Grid grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(.8, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ConfigureGrid(eventGrid); eventGrid.Columns.Add(Column("Time", "TimeText", 1.1)); eventGrid.Columns.Add(Column("ID", "EventId", .4)); eventGrid.Columns.Add(Column("Level", "Level", .6)); eventGrid.Columns.Add(Column("Provider", "Provider", 1.3)); eventGrid.ItemsSource = events;
            eventGrid.SelectionChanged += (s, e) => { DeviceEventRow row = eventGrid.SelectedItem as DeviceEventRow; eventDetails.Text = row == null ? "Select an event to view its recorded message." : row.TimeText + "  |  " + row.Provider + "  |  Event " + row.EventId + Environment.NewLine + "Log: " + row.Log + Environment.NewLine + Environment.NewLine + row.Message; };
            grid.Children.Add(eventGrid); eventDetails.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(eventDetails, 1); grid.Children.Add(eventDetails);
            eventNote.TextWrapping = TextWrapping.Wrap; eventNote.Margin = new Thickness(2, 8, 2, 0); ScrollViewer notes = new ScrollViewer { Content = eventNote, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(notes, 2); grid.Children.Add(notes); return grid;
        }

        private void ApplyFilter()
        {
            if (closed) return;
            DeviceRow old = deviceGrid.SelectedItem as DeviceRow;
            string query = (search.Text ?? "").Trim();
            IEnumerable<DeviceRow> displayed = inventory;
            if (problemsOnly.IsChecked == true) displayed = displayed.Where(row => row.HasProblem);
            if (query.Length > 0) displayed = displayed.Where(row => String.Join(" ", new[] { row.Name, row.DeviceType, row.TypeEvidence, row.EnumeratorName, row.DeviceId, row.HardwareIds, row.Status, row.Service, row.DriverInf, row.DriverProvider, row.ErrorCode.HasValue ? row.ErrorCode.Value.ToString() : "" }).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            rows.Clear(); foreach (DeviceRow row in displayed.OrderByDescending(row => row.ErrorCode == 28).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)) rows.Add(row);
            deviceGrid.SelectedItem = old == null ? rows.FirstOrDefault() : rows.FirstOrDefault(row => String.Equals(row.DeviceId, old.DeviceId, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            if (rows.Count == 0) { diagnosis.Text = inventory.Count == 0 ? "No successful device inventory is available yet." : "No devices match these filters. Uncheck Needs attention only to inspect every detected device."; selected.Text = "No matching device"; }
        }

        private void SelectedChanged()
        {
            CancelRead();
            DeviceRow row = deviceGrid.SelectedItem as DeviceRow;
            dependencies.Clear(); events.Clear(); eventDetails.Text = "Select an event to view its recorded message.";
            refresh.IsEnabled = row != null;
            if (row == null)
            {
                selected.Text = "Select a device"; diagnosis.Text = "Select a device to inspect its driver status, declared dependencies and matching events.";
                dependencyNote.Text = "Select a device to inspect directly declared service dependencies.";
                eventNote.Text = "Select a device to inspect matching device events.";
                status.Text = "Read-only diagnostics. No driver, service or registry settings are changed.";
                detailsStale = false; return;
            }
            selected.Text = row.Name ?? row.DeviceId;
            diagnosis.Text = Describe(row);
            dependencyNote.Text = "Declared service dependencies have not been read yet.";
            eventNote.Text = "Events have not been read yet. The scan checks up to 100 newest relevant events per log from the last 30 days.";
            detailsStale = true; LoadIfVisible();
        }

        private void LoadIfVisible()
        {
            if (!closed && IsLoaded && IsVisible && detailsStale && !reading) RefreshSelected().ConfigureAwait(false);
        }

        private async Task RefreshSelected()
        {
            if (closed) return;
            DeviceRow row = deviceGrid.SelectedItem as DeviceRow; if (row == null) return;
            CancelRead();
            int version = requestVersion;
            activeRead = new CancellationTokenSource(); CancellationToken cancellation = activeRead.Token;
            reading = true; detailsStale = false; refresh.IsEnabled = false; status.Text = "Reading declared dependencies and recent device events...";
            try
            {
                DriverDiagnosticResult result = await Task.Run(() => DriverDiagnosticsReader.Read(row, cancellation), cancellation);
                if (closed || version != requestVersion || cancellation.IsCancellationRequested) return;
                dependencies.Clear(); foreach (DriverDependencyRow dependency in result.Dependencies) dependencies.Add(dependency);
                events.Clear(); foreach (DeviceEventRow item in result.Events) events.Add(item);
                dependencyNote.Text = result.DependencyScope;
                eventNote.Text = (result.Events.Count == 0 ? "No exact device-ID matches were found in the events that could be read. This does not prove there were no device events. " : result.Events.Count + " matching event(s). ") + String.Join(" ", result.Notes);
                if (events.Count > 0) eventGrid.SelectedIndex = 0;
                status.Text = "Checked " + DateTime.Now.ToString("HH:mm:ss") + ". Direct service dependencies and bounded event-log results are shown; no settings were changed.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closed && version == requestVersion) { status.Text = "Diagnostic read failed: " + ex.Message; dependencyNote.Text = "Dependency data unavailable; no absence is assumed."; eventNote.Text = "Event data unavailable; no absence is assumed."; }
            }
            finally
            {
                if (version == requestVersion)
                {
                    reading = false; refresh.IsEnabled = !closed && deviceGrid.SelectedItem != null;
                    if (activeRead != null) { activeRead.Dispose(); activeRead = null; }
                }
            }
        }

        private void CancelRead()
        {
            requestVersion++; reading = false;
            if (activeRead != null) { activeRead.Cancel(); activeRead.Dispose(); activeRead = null; }
        }

        private static string Describe(DeviceRow row)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("WINDOWS DEVICE STATUS"); text.AppendLine(Display(row.Status)); text.AppendLine("Problem code: " + (row.ErrorCode.HasValue ? row.ErrorCode.Value.ToString() : "Unknown"));
            text.AppendLine(); text.AppendLine("WHAT THIS MEANS / NEXT CHECK"); text.AppendLine(DriverDiagnosticsReader.Advice(row.ErrorCode));
            text.AppendLine(); text.AppendLine("EXACT DEVICE INFORMATION");
            text.AppendLine("Device instance ID: " + Display(row.DeviceId)); text.AppendLine("Hardware IDs:"); text.AppendLine(Display(row.HardwareIds));
            text.AppendLine(); text.AppendLine("Device type: " + (row.DeviceType ?? "Unknown")); text.AppendLine("Classification basis: " + Display(row.TypeEvidence));
            text.AppendLine("Enumerator: " + Display(row.EnumeratorName)); text.AppendLine("Parent device: " + Display(row.ParentDeviceId)); text.AppendLine("Setup class GUID: " + Display(row.ClassGuid));
            text.AppendLine(); text.AppendLine("Driver provider: " + Display(row.DriverProvider)); text.AppendLine("Driver version: " + Display(row.DriverVersion));
            text.AppendLine("Driver INF: " + Display(row.DriverInf)); text.AppendLine("Driver service: " + Display(row.Service)); text.AppendLine("Signed: " + Display(row.DriverSigned));
            return text.ToString();
        }

        public void Shutdown() { closed = true; CancelRead(); }
        private static string Display(string value) { return String.IsNullOrWhiteSpace(value) ? "Unavailable" : value; }
        private static TabItem Section(string name, object content) { return new TabItem { Header = name, Content = content, Padding = new Thickness(12, 7, 12, 7) }; }
        private static TextBox ReadOnlyText() { return new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White, Foreground = Brushes.Black, CaretBrush = Brushes.Black, FontSize = 13, Padding = new Thickness(12), BorderBrush = Color("#DBE3EF") }; }
        private static TextBlock Label(string value, double size) { return new TextBlock { Text = value, FontSize = size, Foreground = Color("#18263B") }; }
        private static SolidColorBrush Color(string value) { SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value); brush.Freeze(); return brush; }
        private static void ConfigureGrid(DataGrid grid)
        {
            UiTheme.ConfigureGrid(grid); grid.SelectionMode = DataGridSelectionMode.Single;
        }
        private static DataGridTextColumn Column(string title, string property, double width)
        {
            Style text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6)));
            return new DataGridTextColumn { Header = title, Binding = new Binding(property) { TargetNullValue = "Unavailable" }, SortMemberPath = property, Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = title == "Device Type" ? 110 : title == "Code" ? 45 : 20, ElementStyle = text };
        }
    }
}
