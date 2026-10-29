using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeviceScope
{
    public sealed class ProcessRow : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string WindowTitle { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        public bool MemoryAvailable { get; set; }
        public long? StartTimeUtcTicks { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public void Update(ProcessRow next)
        {
            Name = next.Name; Kind = next.Kind; WindowTitle = next.WindowTitle;
            WorkingSetBytes = next.WorkingSetBytes; PrivateBytes = next.PrivateBytes;
            MemoryAvailable = next.MemoryAvailable;
            StartTimeUtcTicks = next.StartTimeUtcTicks;
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(String.Empty));
        }
    }

    public sealed class ProcessSnapshot
    {
        public List<ProcessRow> Processes { get; set; }
        public ulong TotalPhysicalBytes { get; set; }
        public ulong AvailablePhysicalBytes { get; set; }
        public DateTime CapturedAt { get; set; }
    }

    public static class ProcessSnapshotReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
            public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        public static ProcessSnapshot Capture()
        {
            ProcessSnapshot snapshot = new ProcessSnapshot { Processes = new List<ProcessRow>(), CapturedAt = DateTime.Now };
            Process[] processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                using (process)
                {
                    ProcessRow row;
                    try { row = new ProcessRow { Id = process.Id, Name = process.ProcessName, Kind = "Background / service", WindowTitle = "", WorkingSetBytes = -1, PrivateBytes = -1 }; }
                    catch (Exception ex) { if (ExpectedProcessError(ex)) continue; throw; }
                    try
                    {
                        process.Refresh();
                        row.WorkingSetBytes = process.WorkingSet64;
                        row.PrivateBytes = process.PrivateMemorySize64;
                        row.MemoryAvailable = true;
                    }
                    catch (Exception ex) { if (!ExpectedProcessError(ex)) throw; }
                    try
                    {
                        row.WindowTitle = process.MainWindowTitle ?? "";
                        if (process.MainWindowHandle != IntPtr.Zero) row.Kind = "Application";
                    }
                    catch (Exception ex) { if (!ExpectedProcessError(ex)) throw; row.Kind = "Unknown (restricted)"; }
                    ProcessIdentity identity = ProcessController.ReadIdentity(row.Id);
                    if (identity != null) { row.StartTimeUtcTicks = identity.StartTimeUtcTicks; row.Name = identity.Name; }
                    snapshot.Processes.Add(row);
                }
            }
            MemoryStatus memory = new MemoryStatus();
            memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (GlobalMemoryStatusEx(ref memory))
            {
                snapshot.TotalPhysicalBytes = memory.TotalPhysical;
                snapshot.AvailablePhysicalBytes = memory.AvailablePhysical;
            }
            return snapshot;
        }

        private static bool ExpectedProcessError(Exception ex)
        {
            return ex is Win32Exception || ex is InvalidOperationException || ex is NotSupportedException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException;
        }
    }

    public sealed class MemoryValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long bytes = (long)value;
            return bytes < 0 ? "Unavailable" : (bytes / 1048576.0).ToString("N1", culture);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { return Binding.DoNothing; }
    }

    public sealed partial class ProcessesView : UserControl
    {
        private readonly ObservableCollection<ProcessRow> rows = new ObservableCollection<ProcessRow>();
        private readonly DataGrid grid = new DataGrid();
        private readonly TextBox search = new TextBox();
        private readonly CheckBox automatic = new CheckBox();
        private readonly TextBlock summary = new TextBlock();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock selection = new TextBlock();
        private readonly TextBlock stopNote = new TextBlock();
        private readonly TextBlock actionStatus = new TextBlock();
        private readonly Button refresh = new Button();
        private readonly Button stop = new Button();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly ICollectionView view;
        private bool busy, closed, hasData, stopping, refreshAgain;
        private int processGeneration;

        public ProcessesView()
        {
            Background = Color("#EDF2F8");
            view = CollectionViewSource.GetDefaultView(rows);
            view.Filter = MatchesSearch;
            view.SortDescriptions.Add(new SortDescription("WorkingSetBytes", ListSortDirection.Descending));
            Content = BuildUi();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (s, e) => RefreshProcesses();
            Loaded += (s, e) => UpdateTimer();
            IsVisibleChanged += (s, e) => UpdateTimer();
            Unloaded += (s, e) => timer.Stop();
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            summary.Foreground = Color("#53647A"); summary.FontSize = 13; summary.Margin = new Thickness(0, 0, 0, 10);
            summary.Text = "Select this tab to inspect running processes and memory.";
            summary.TextWrapping = TextWrapping.Wrap; Grid.SetRow(summary, 1); root.Children.Add(summary);

            Grid actions = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel controls = new StackPanel { Orientation = Orientation.Horizontal };
            automatic.Content = "Refresh every 3 seconds"; automatic.Foreground = Color("#18263B");
            automatic.IsChecked = true; automatic.VerticalAlignment = VerticalAlignment.Center; automatic.Margin = new Thickness(12, 0, 12, 0);
            automatic.Checked += (s, e) => UpdateTimer(); automatic.Unchecked += (s, e) => UpdateTimer();
            refresh.Content = "Refresh processes"; UiTheme.StyleButton(refresh); refresh.Click += (s, e) => RefreshProcesses();
            controls.Children.Add(refresh); controls.Children.Add(automatic); actions.Children.Add(controls);
            StackPanel searchPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            searchPanel.Children.Add(new TextBlock { Text = "Search", Foreground = Color("#18263B"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) });
            search.Background = Brushes.White; search.Foreground = Brushes.Black; search.CaretBrush = Brushes.Black;
            search.BorderBrush = Color("#DBE3EF"); search.Padding = new Thickness(10, 6, 10, 6); search.Width = 230;
            search.ToolTip = "Search process name, PID, type or window title";
            search.TextChanged += (s, e) => { view.Refresh(); UpdateSelection(); };
            searchPanel.Children.Add(search); Grid.SetColumn(searchPanel, 2); actions.Children.Add(searchPanel); root.Children.Add(actions);

            ConfigureGrid(); Grid.SetRow(grid, 2); root.Children.Add(grid);
            DockPanel selectedPanel = new DockPanel { LastChildFill = true };
            stop.Content = "Stop Process"; UiTheme.StyleButton(stop);
            stop.Padding = new Thickness(14, 8, 14, 8); stop.Margin = new Thickness(12, 10, 0, 0);
            stop.VerticalAlignment = VerticalAlignment.Top; stop.Visibility = Visibility.Collapsed;
            stop.Click += (s, e) => StopSelectedProcess();
            DockPanel.SetDock(stop, Dock.Right); selectedPanel.Children.Add(stop);
            selection.Foreground = Color("#18263B"); selection.TextWrapping = TextWrapping.Wrap;
            selection.Margin = new Thickness(0, 10, 0, 0); selection.Text = "Select a process for its memory details.";
            selectedPanel.Children.Add(selection); Grid.SetRow(selectedPanel, 3); root.Children.Add(selectedPanel);
            StackPanel stopFeedback = new StackPanel();
            stopNote.Text = "Force stop may lose unsaved data in that process."; stopNote.Foreground = Color("#8A4B0F");
            stopNote.Visibility = Visibility.Collapsed; stopNote.TextWrapping = TextWrapping.Wrap; stopNote.Margin = new Thickness(0, 6, 0, 0);
            actionStatus.Foreground = Color("#18263B"); actionStatus.TextWrapping = TextWrapping.Wrap; actionStatus.Margin = new Thickness(0, 5, 0, 0);
            stopFeedback.Children.Add(stopNote); stopFeedback.Children.Add(actionStatus);
            Grid.SetRow(stopFeedback, 4); root.Children.Add(stopFeedback);
            status.Foreground = Color("#53647A"); status.FontSize = 12; status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 8, 0, 0);
            status.Text = "RAM = working set currently in physical memory (shared pages included). Private bytes = private committed memory; it may include paged-out memory.";
            Grid.SetRow(status, 5); root.Children.Add(status);
            return root;
        }

        private void ConfigureGrid()
        {
            UiTheme.ConfigureGrid(grid);
            grid.SelectionMode = DataGridSelectionMode.Single; grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            grid.Columns.Add(Column("Process", "Name", 1.5, false));
            grid.Columns.Add(Column("PID", "Id", .55, false));
            grid.Columns.Add(Column("RAM (MB)", "WorkingSetBytes", .8, true));
            grid.Columns.Add(Column("Private (MB)", "PrivateBytes", .8, true));
            grid.Columns.Add(Column("Type", "Kind", 1, false));
            grid.Columns.Add(Column("Window title", "WindowTitle", 1.9, false));
            grid.ItemsSource = view; grid.SelectionChanged += (s, e) => UpdateSelection();
        }

        private static DataGridTextColumn Column(string title, string path, double width, bool memory)
        {
            Binding binding = new Binding(path);
            if (memory) binding.Converter = new MemoryValueConverter();
            Style text = new Style(typeof(TextBlock));
            text.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(7, 5, 7, 5)));
            text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            return new DataGridTextColumn { Header = title, Binding = binding, SortMemberPath = path, Width = new DataGridLength(width, DataGridLengthUnitType.Star), ElementStyle = text };
        }

        private void UpdateTimer()
        {
            timer.Stop();
            if (closed || !IsLoaded || !IsVisible) return;
            if (automatic.IsChecked == true) timer.Start();
            if (!hasData || refreshAgain) RefreshProcesses();
        }

        private async void RefreshProcesses()
        {
            if (closed || !IsVisible) return;
            if (busy) { refreshAgain = true; return; }
            refreshAgain = false;
            busy = true; refresh.IsEnabled = false;
            int generation = processGeneration;
            try
            {
                ProcessSnapshot snapshot = await Task.Run(() => ProcessSnapshotReader.Capture());
                if (closed) return;
                if (generation == processGeneration) ApplySnapshot(snapshot);
                else refreshAgain = true;
            }
            catch (Exception ex) { if (!closed) status.Text = "Could not refresh processes: " + ex.Message; }
            finally
            {
                busy = false;
                if (!closed) refresh.IsEnabled = true;
                if (refreshAgain && !closed && IsVisible) { refreshAgain = false; RefreshProcesses(); }
            }
        }

        private void ApplySnapshot(ProcessSnapshot snapshot)
        {
            ProcessRow currentSelection = grid.SelectedItem as ProcessRow;
            int? selectedId = currentSelection == null ? (int?)null : currentSelection.Id;
            long? selectedBirth = currentSelection == null ? (long?)null : currentSelection.StartTimeUtcTicks;
            Dictionary<int, ProcessRow> existing = rows.ToDictionary(p => p.Id);
            HashSet<int> alive = new HashSet<int>(snapshot.Processes.Select(p => p.Id));
            for (int i = rows.Count - 1; i >= 0; i--) if (!alive.Contains(rows[i].Id)) rows.RemoveAt(i);
            foreach (ProcessRow next in snapshot.Processes)
            {
                ProcessRow row;
                if (existing.TryGetValue(next.Id, out row) && row.StartTimeUtcTicks == next.StartTimeUtcTicks) row.Update(next);
                else if (row != null) { rows.Remove(row); rows.Add(next); }
                else rows.Add(next);
            }
            view.Refresh();
            if (selectedId.HasValue) grid.SelectedItem = rows.FirstOrDefault(p => p.Id == selectedId.Value && p.StartTimeUtcTicks == selectedBirth);
            UpdateSelection(); hasData = true;
            string ram = snapshot.TotalPhysicalBytes == 0 ? "System RAM unavailable" : String.Format(CultureInfo.CurrentCulture, "RAM: {0:N1} / {1:N1} GB used ({2:N0}%)", (snapshot.TotalPhysicalBytes - snapshot.AvailablePhysicalBytes) / 1073741824.0, snapshot.TotalPhysicalBytes / 1073741824.0, 100.0 * (snapshot.TotalPhysicalBytes - snapshot.AvailablePhysicalBytes) / snapshot.TotalPhysicalBytes);
            summary.Text = snapshot.Processes.Count + " running processes  •  " + ram;
            int restricted = snapshot.Processes.Count(p => !p.MemoryAvailable);
            status.Text = "Updated " + snapshot.CapturedAt.ToString("HH:mm:ss") + (restricted > 0 ? "  •  " + restricted + " process memory readings unavailable (restricted or exited)." : ".") + " RAM includes shared pages; private bytes measure committed private memory. Shared RAM must not be added together.";
        }

        private bool MatchesSearch(object item)
        {
            ProcessRow row = item as ProcessRow;
            if (row == null) return false;
            string query = (search.Text ?? "").Trim();
            return query.Length == 0 || (row.Name + " " + row.Id + " " + row.Kind + " " + row.WindowTitle).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void UpdateSelection()
        {
            ProcessRow row = grid.SelectedItem as ProcessRow;
            stop.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            stopNote.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            stop.Content = stopping ? "Stopping..." : "Stop Process";
            string protection = row == null ? null : ProcessController.ProtectionReason(row.Id, row.Name);
            stop.IsEnabled = !stopping && row != null && row.StartTimeUtcTicks.HasValue && protection == null;
            stop.ToolTip = protection ?? (row != null && !row.StartTimeUtcTicks.HasValue ? "Process identity is unavailable; refresh or select another process." : "Force-stop only this selected process");
            stopNote.Text = protection ?? (row != null && !row.StartTimeUtcTicks.HasValue ? "Process identity is unavailable, so this process cannot be stopped here." : "Force stop may lose unsaved data in that process.");
            if (row == null) { selection.Text = "Select a process for its memory details."; return; }
            selection.Text = row.Name + "  •  PID " + row.Id + "  •  " + row.Kind + (row.MemoryAvailable ? String.Format(CultureInfo.CurrentCulture, "  •  RAM {0:N1} MB  •  private {1:N1} MB", row.WorkingSetBytes / 1048576.0, row.PrivateBytes / 1048576.0) : "  •  Memory unavailable (restricted or process exited)") + (String.IsNullOrWhiteSpace(row.WindowTitle) ? "" : "\n" + row.WindowTitle);
        }

        private async void StopSelectedProcess()
        {
            if (closed || stopping) return;
            ProcessRow selectedRow = grid.SelectedItem as ProcessRow;
            if (selectedRow == null || !selectedRow.StartTimeUtcTicks.HasValue) return;
            // Capture immutable identity before awaiting; changing selection or a
            // background refresh cannot redirect this action to another target.
            int targetId = selectedRow.Id;
            long targetBirth = selectedRow.StartTimeUtcTicks.Value;
            string targetLabel = selectedRow.Name + " (PID " + targetId + ")";
            stopping = true; UpdateSelection(); actionStatus.Text = "Stopping " + targetLabel + "...";
            try
            {
                ProcessStopResult result = await Task.Run(() => ProcessController.StopSelected(targetId, targetBirth));
                if (closed) return;
                actionStatus.Text = targetLabel + ": " + result.Message;
                if (result.Success || result.TerminationRequested)
                {
                    processGeneration++;
                    if (result.Success)
                    {
                        ProcessRow stoppedRow = rows.FirstOrDefault(row => row.Id == targetId && row.StartTimeUtcTicks == targetBirth);
                        if (stoppedRow != null) rows.Remove(stoppedRow);
                        UpdateSelection();
                    }
                    refreshAgain = true;
                    RefreshProcesses();
                }
            }
            catch (Exception ex) { if (!closed) actionStatus.Text = targetLabel + ": " + ex.Message; }
            finally { stopping = false; if (!closed) UpdateSelection(); }
        }

        public void Shutdown() { closed = true; timer.Stop(); }
        private static SolidColorBrush Color(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
    }
}
