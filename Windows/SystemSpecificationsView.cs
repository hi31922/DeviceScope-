using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeviceScope
{
    public sealed class SystemSpecificationsView : UserControl
    {
        private readonly StackPanel sections = new StackPanel();
        private readonly TextBlock status = new TextBlock();
        private readonly Button refresh = new Button { Content = "Refresh specifications" };
        private readonly Button copy = new Button { Content = "Copy specifications", IsEnabled = false };
        private readonly Action<string> setClipboard;
        private string specificationText;
        private bool busy, closed, loaded;

        public SystemSpecificationsView() : this(text => Clipboard.SetDataObject(text, true)) { }

        internal SystemSpecificationsView(Action<string> setClipboard)
        {
            this.setClipboard = setClipboard;
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WrapPanel toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            UiTheme.StyleButton(refresh); refresh.Click += async (s, e) => await Refresh(); DockPanel.SetDock(refresh, Dock.Left); toolbar.Children.Add(refresh);
            UiTheme.StyleButton(copy); copy.Margin = new Thickness(8, 0, 0, 0); copy.Click += (s, e) => CopySpecifications(); DockPanel.SetDock(copy, Dock.Left); toolbar.Children.Add(copy);
            root.Children.Add(toolbar);
            ScrollViewer scroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            status.Text = "Open this tab to read system specifications. Unknown or restricted fields remain unavailable.";
            status.TextWrapping = TextWrapping.Wrap; status.Foreground = UiTheme.Brush("#555555"); status.FontSize = 11; status.Margin = new Thickness(0, 8, 0, 0); status.MaxHeight = 55;
            Grid.SetRow(status, 2); root.Children.Add(status); Content = root;
            Loaded += async (s, e) => { if (IsVisible && !loaded) await Refresh(); };
            IsVisibleChanged += async (s, e) => { if (IsVisible && !loaded) await Refresh(); };
        }
        private async Task Refresh()
        {
            if (closed || busy) return;
            busy = true; refresh.IsEnabled = false; status.Text = "Reading system specifications in the background...";
            try
            {
                SystemSpecificationsReport report = await Task.Run(() => SystemSpecificationsReader.Capture());
                if (closed) return;
                ApplyReport(report); loaded = true;
            }
            catch (Exception ex) { if (!closed) status.Text = "Could not read specifications: " + ex.Message; }
            finally { busy = false; if (!closed) refresh.IsEnabled = true; }
        }
        public void ApplyReport(SystemSpecificationsReport report)
        {
            sections.Children.Clear();
            foreach (SpecificationSection section in report.Sections)
            {
                StackPanel body = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
                body.Children.Add(new TextBlock { Text = section.Title, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = UiTheme.Brush("#000000"), Margin = new Thickness(0, 0, 0, 4) });
                Grid values = new Grid(); values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                foreach (SpecificationProperty property in section.Rows)
                {
                    int row = values.RowDefinitions.Count; values.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    TextBlock label = new TextBlock { Text = property.Label, Foreground = UiTheme.Brush("#555555"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 10, 2) };
                    TextBox value = new TextBox { Text = property.Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 2), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    Grid.SetRow(label, row); values.Children.Add(label); Grid.SetRow(value, row); Grid.SetColumn(value, 1); values.Children.Add(value);
                }
                body.Children.Add(values);
                sections.Children.Add(new Border { Child = body, Background = UiTheme.Brush("#EFEFEF"), BorderBrush = UiTheme.Brush("#A0A0A0"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(0) });
            }
            status.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss") + ". Local read-only information. " + (report.Notes.Count == 0 ? "" : String.Join(" | ", report.Notes));
            status.ToolTip = status.Text;
            specificationText = FormatReport(report);
            copy.IsEnabled = !String.IsNullOrWhiteSpace(specificationText);
        }

        public static string FormatReport(SystemSpecificationsReport report)
        {
            if (report == null || report.Sections.Count == 0) return String.Empty;
            StringBuilder text = new StringBuilder("DeviceScope System Specifications");
            foreach (SpecificationSection section in report.Sections)
            {
                text.AppendLine().AppendLine().AppendLine(section.Title);
                foreach (SpecificationProperty property in section.Rows)
                    text.AppendLine(property.Label + ": " + (String.IsNullOrWhiteSpace(property.Value) ? "Unavailable" : property.Value));
            }
            if (report.Notes.Count > 0)
            {
                text.AppendLine().AppendLine("Collection notes");
                foreach (string note in report.Notes) text.AppendLine(note);
            }
            return text.ToString().TrimEnd();
        }

        private void CopySpecifications()
        {
            if (closed || String.IsNullOrWhiteSpace(specificationText)) return;
            try
            {
                setClipboard(specificationText);
                status.Text = "Copied all specifications to the clipboard.";
            }
            catch (Exception ex) { status.Text = "Could not copy specifications: " + ex.Message; }
            status.ToolTip = status.Text;
        }
        public void Shutdown() { closed = true; }
    }
}
