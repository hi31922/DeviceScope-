using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace DeviceScope
{
    // The Linux edition's palette expressed as native WPF controls and templates.
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
            button.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#DFE8F5")));
            button.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#18263B")));
            button.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#CBD8E9")));
            button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
            button.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            button.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            button.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            ControlTemplate buttonTemplate = new ControlTemplate(typeof(Button));
            FrameworkElementFactory buttonBorder = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
            buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            buttonBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            buttonBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            FrameworkElementFactory buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
            buttonContent.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            buttonContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            buttonContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            buttonBorder.AppendChild(buttonContent); buttonTemplate.VisualTree = buttonBorder;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#C9DCF6"), "ButtonSurface")); buttonTemplate.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#B7D0F4"), "ButtonSurface")); buttonTemplate.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .5, "ButtonSurface")); buttonTemplate.Triggers.Add(disabled);
            button.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate)); target.Resources[typeof(Button)] = button;

            Style textBox = new Style(typeof(TextBox));
            textBox.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            textBox.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            textBox.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#CBD8E9")));
            textBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
            textBox.Setters.Add(new Setter(TextBox.CaretBrushProperty, Brushes.Black)); target.Resources[typeof(TextBox)] = textBox;

            Style check = new Style(typeof(CheckBox));
            check.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#18263B")));
            check.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            target.Resources[typeof(CheckBox)] = check;

            Style tabs = new Style(typeof(TabControl));
            tabs.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#EDF2F8")));
            tabs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            ControlTemplate tabsTemplate = new ControlTemplate(typeof(TabControl));
            FrameworkElementFactory tabsRoot = new FrameworkElementFactory(typeof(DockPanel));
            tabsRoot.SetValue(DockPanel.LastChildFillProperty, true);
            FrameworkElementFactory strip = new FrameworkElementFactory(typeof(TabPanel));
            strip.SetValue(Panel.IsItemsHostProperty, true); strip.SetValue(DockPanel.DockProperty, Dock.Top);
            tabsRoot.AppendChild(strip);
            FrameworkElementFactory contentBorder = new FrameworkElementFactory(typeof(Border));
            contentBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            contentBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            contentBorder.AppendChild(presenter); tabsRoot.AppendChild(contentBorder); tabsTemplate.VisualTree = tabsRoot;
            tabs.Setters.Add(new Setter(Control.TemplateProperty, tabsTemplate)); target.Resources[typeof(TabControl)] = tabs;

            Style tab = new Style(typeof(TabItem));
            tab.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#DFE7F2")));
            tab.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#18263B")));
            tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(17, 12, 17, 12)));
            tab.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            ControlTemplate tabTemplate = new ControlTemplate(typeof(TabItem));
            FrameworkElementFactory tabBorder = new FrameworkElementFactory(typeof(Border), "TabSurface");
            tabBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            tabBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
            tabBorder.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            FrameworkElementFactory tabContent = new FrameworkElementFactory(typeof(ContentPresenter));
            tabContent.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            tabContent.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true); tabBorder.AppendChild(tabContent);
            tabTemplate.VisualTree = tabBorder;
            Trigger tabFocus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            tabFocus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#2563EB"), "TabSurface")); tabTemplate.Triggers.Add(tabFocus);
            tab.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
            Trigger active = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            active.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            active.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#2563EB"))); tab.Triggers.Add(active);
            target.Resources[typeof(TabItem)] = tab;
        }

        public static void StyleButton(Button button)
        {
            button.Background = Brush("#DFE8F5"); button.Foreground = Brush("#18263B");
            button.BorderBrush = Brush("#CBD8E9"); button.BorderThickness = new Thickness(1);
            button.Padding = new Thickness(14, 8, 14, 8); button.FontWeight = FontWeights.Normal;
        }

        public static void ConfigureGrid(DataGrid grid)
        {
            grid.AutoGenerateColumns = false; grid.IsReadOnly = true; grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
            grid.HeadersVisibility = DataGridHeadersVisibility.Column; grid.RowHeaderWidth = 0;
            grid.Background = Brushes.White; grid.Foreground = Brushes.Black; grid.RowBackground = Brushes.White;
            grid.AlternatingRowBackground = Brush("#F8FAFD"); grid.BorderThickness = new Thickness(1); grid.BorderBrush = Brush("#DBE3EF");
            grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; grid.HorizontalGridLinesBrush = Brush("#EDF2F8");
            grid.EnableRowVirtualization = true; grid.EnableColumnVirtualization = true; grid.MinRowHeight = 29;
            grid.SelectionMode = DataGridSelectionMode.Single; grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            Style header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#E0E8F3")));
            header.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#18263B")));
            header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 9, 9, 9)));
            header.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            header.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#D0DBE9")));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold)); grid.ColumnHeaderStyle = header;
            Style cell = new Style(typeof(DataGridCell));
            cell.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            Trigger selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#C9DFFF")));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black)); cell.Triggers.Add(selected); grid.CellStyle = cell;
        }
    }
}
