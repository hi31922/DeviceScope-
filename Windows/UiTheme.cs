using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DeviceScope
{
    // Compact XP-era utility palette, using native WPF controls.
    public static class UiTheme
    {
        public static SolidColorBrush Brush(string value)
        {
            SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
            brush.Freeze(); return brush;
        }

        public static void ApplyResources(FrameworkElement target)
        {
            Style button = new Style(typeof(Button));
            button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#D4D4D4")));
            button.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#000000")));
            button.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#7F9DB9")));
            button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 3, 9, 3)));
            button.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            button.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 23.0));
            button.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            button.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            ControlTemplate buttonTemplate = new ControlTemplate(typeof(Button));
            FrameworkElementFactory buttonBorder = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
            LinearGradientBrush face = new LinearGradientBrush(); face.StartPoint = new Point(0, 0); face.EndPoint = new Point(0, 1);
            face.GradientStops.Add(new GradientStop(Colors.White, 0)); face.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#D4D4D4"), .55)); face.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#BFBFBF"), 1)); face.Freeze();
            buttonBorder.SetValue(Border.BackgroundProperty, face);
            buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            buttonBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            buttonBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            FrameworkElementFactory buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
            buttonContent.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            buttonContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            buttonContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            buttonBorder.AppendChild(buttonContent); buttonTemplate.VisualTree = buttonBorder;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#E5E5E5"), "ButtonSurface")); buttonTemplate.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#BDBDBD"), "ButtonSurface")); buttonTemplate.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .5, "ButtonSurface")); buttonTemplate.Triggers.Add(disabled);
            button.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate)); target.Resources[typeof(Button)] = button;

            Style textBox = new Style(typeof(TextBox));
            textBox.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            textBox.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            textBox.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#7F9DB9")));
            textBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(3, 2, 3, 2)));
            textBox.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.Black)); target.Resources[typeof(TextBox)] = textBox;

            Style check = new Style(typeof(CheckBox));
            check.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#000000")));
            check.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            target.Resources[typeof(CheckBox)] = check;

            Style tabs = new Style(typeof(TabControl));
            tabs.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#D4D4D4")));
            tabs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            ControlTemplate tabsTemplate = new ControlTemplate(typeof(TabControl));
            FrameworkElementFactory tabsRoot = new FrameworkElementFactory(typeof(DockPanel));
            tabsRoot.SetValue(DockPanel.LastChildFillProperty, true);
            // WrapPanel preserves header positions; TabPanel rotates rows around the selected tab.
            FrameworkElementFactory strip = new FrameworkElementFactory(typeof(WrapPanel));
            strip.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
            strip.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once);
            strip.SetValue(Panel.IsItemsHostProperty, true); strip.SetValue(DockPanel.DockProperty, Dock.Top);
            strip.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, -1)); strip.SetValue(Panel.ZIndexProperty, 1); tabsRoot.AppendChild(strip);
            FrameworkElementFactory contentBorder = new FrameworkElementFactory(typeof(Border));
            contentBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            contentBorder.SetValue(Border.BorderBrushProperty, Brush("#919B9C")); contentBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            contentBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            contentBorder.AppendChild(presenter); tabsRoot.AppendChild(contentBorder); tabsTemplate.VisualTree = tabsRoot;
            tabs.Setters.Add(new Setter(Control.TemplateProperty, tabsTemplate)); target.Resources[typeof(TabControl)] = tabs;

            Style tab = new Style(typeof(TabItem));
            tab.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#C6C6C6")));
            tab.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#000000")));
            tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            tab.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            ControlTemplate tabTemplate = new ControlTemplate(typeof(TabItem));
            FrameworkElementFactory tabBorder = new FrameworkElementFactory(typeof(Border), "TabSurface");
            tabBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            tabBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
            tabBorder.SetValue(Border.BorderBrushProperty, Brush("#919B9C")); tabBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(2, 2, 0, 0));
            FrameworkElementFactory tabContent = new FrameworkElementFactory(typeof(ContentPresenter));
            tabContent.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            tabContent.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            FrameworkElementFactory tabAccent = new FrameworkElementFactory(typeof(Border), "TabAccent");
            tabAccent.SetValue(Border.BorderThicknessProperty, new Thickness(0, 2, 0, 0));
            tabAccent.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            tabAccent.AppendChild(tabContent); tabBorder.AppendChild(tabAccent);
            tabTemplate.VisualTree = tabBorder;
            Trigger selectedTab = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selectedTab.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#E6A647"), "TabAccent")); tabTemplate.Triggers.Add(selectedTab);
            Trigger tabFocus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            tabFocus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#003C74"), "TabSurface")); tabTemplate.Triggers.Add(tabFocus);
            tab.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
            Trigger active = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            active.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#D4D4D4")));
            active.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#003C74"))); tab.Triggers.Add(active);
            target.Resources[typeof(TabItem)] = tab;
        }

        public static void StyleButton(Button button)
        {
            button.Background = Brush("#D4D4D4"); button.Foreground = Brush("#000000");
            button.BorderBrush = Brush("#7F9DB9"); button.BorderThickness = new Thickness(1);
            button.Padding = new Thickness(9, 3, 9, 3); button.MinHeight = 23; button.FontWeight = FontWeights.Normal;
        }

        public static void ConfigureGrid(DataGrid grid)
        {
            grid.AutoGenerateColumns = false; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
            grid.HeadersVisibility = DataGridHeadersVisibility.Column; grid.RowHeaderWidth = 0;
            grid.Background = Brushes.White; grid.Foreground = Brushes.Black; grid.RowBackground = Brushes.White;
            grid.AlternatingRowBackground = Brush("#FFFFFF"); grid.BorderThickness = new Thickness(1); grid.BorderBrush = Brush("#7F9DB9");
            grid.GridLinesVisibility = DataGridGridLinesVisibility.None; grid.HorizontalGridLinesBrush = Brush("#D4D4D4");
            grid.EnableRowVirtualization = true; grid.EnableColumnVirtualization = true; grid.MinRowHeight = 21;
            grid.SelectionMode = DataGridSelectionMode.Single; grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            Style header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#D4D4D4")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#000000")));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 4, 5, 4)));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#B0B0B0")));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal)); grid.ColumnHeaderStyle = header;
            Style cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            Trigger selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#316AC5")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); cell.Triggers.Add(selected); grid.CellStyle = cell;
        }
    }
}
