using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace OrbitDesktop;

internal static class Ui
{
    private static readonly Brush Background = ColorBrush(10, 11, 15);
    private static readonly Brush Surface = ColorBrush(22, 24, 30);
    private static readonly Brush Edge = ColorBrush(52, 55, 65);
    private static readonly Brush Title = ColorBrush(240, 240, 246);
    private static readonly Brush Text = ColorBrush(205, 209, 220);
    private static readonly Brush Muted = ColorBrush(153, 159, 175);

    private static SolidColorBrush ColorBrush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static (Window Frame, StackPanel Body) Frame(string title, double width, double height)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 19, 24, 18) };
        var window = new Window
        {
            Title = "Orbit · " + title,
            Background = Background,
            Foreground = Text,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Content = new Border { Background = Background, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = panel }
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var close = Button("×", () => window.Close());
        close.FontSize = 18;
        close.Width = 34;
        close.Padding = new Thickness(2, 0, 2, 1);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var caption = Label(title, 24, Title);
        caption.MouseLeftButtonDown += (_, args) => { if (args.LeftButton == MouseButtonState.Pressed) window.DragMove(); };
        header.Children.Add(caption);
        panel.Children.Add(header);
        window.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; window.Close(); } };
        return (window, panel);
    }

    internal static Window Search(Catalog catalog, Action<DesktopItem> focus, Action error)
    {
        var (window, panel) = Frame("Поиск на рабочем столе", 508, 424);
        var query = new TextBox
        {
            Padding = new Thickness(10, 7, 10, 7),
            Background = Surface,
            Foreground = Title,
            CaretBrush = Title,
            BorderBrush = Edge
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(query, "OrbitSearch");
        panel.Children.Add(query);
        var list = new ListBox
        {
            Height = 238,
            Margin = new Thickness(0, 10, 0, 8),
            Background = Background,
            Foreground = Title,
            BorderBrush = Edge
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        panel.Children.Add(list);
        var status = Label("", 11, Muted);
        DockPanel commands = new() { LastChildFill = true };
        var launch = Button("Открыть", () => Accept(true));
        DockPanel.SetDock(launch, Dock.Right);
        commands.Children.Add(launch);
        var find = Button("Показать на орбите", () => Accept(false));
        DockPanel.SetDock(find, Dock.Right);
        commands.Children.Add(find);
        commands.Children.Add(status);
        panel.Children.Add(commands);

        void Accept(bool open)
        {
            if (list.SelectedItem is not ListBoxItem row || row.Tag is not DesktopItem item) return;
            window.Close();
            focus(item);
            if (!open) return;
            try { DesktopShell.Open(item.Path); }
            catch (System.ComponentModel.Win32Exception) { error(); }
            catch (System.IO.IOException) { error(); }
        }

        void Update()
        {
            var terms = query.Text.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var found = catalog.Items.Where(item => terms.All(term => item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                                                                      item.Type.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            list.Items.Clear();
            foreach (var item in found)
            {
                var entry = new DockPanel { LastChildFill = true, Margin = new Thickness(7, 5, 8, 5) };
                var tag = Label(item.Type, 10, Muted);
                tag.Margin = new Thickness(10, 2, 0, 0);
                DockPanel.SetDock(tag, Dock.Right);
                entry.Children.Add(tag);
                var name = Label(item.Name, 13, Text);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                entry.Children.Add(name);
                list.Items.Add(new ListBoxItem { Tag = item, ToolTip = item.Path, Content = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            status.Text = found.Length == 0 ? "Совпадений нет" : $"Результатов  {found.Length}";
            list.SelectedIndex = found.Length > 0 ? 0 : -1;
            launch.IsEnabled = find.IsEnabled = found.Length > 0;
        }

        query.TextChanged += (_, _) => Update();
        list.MouseDoubleClick += (_, _) => Accept(false);
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { Accept((Keyboard.Modifiers & ModifierKeys.Control) != 0); args.Handled = true; }
            if ((args.Key == Key.Down || args.Key == Key.Up) && query.IsKeyboardFocused && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                args.Handled = true;
            }
        };
        window.Loaded += (_, _) => Keyboard.Focus(query);
        catalog.Changed += Update;
        window.Closed += (_, _) => catalog.Changed -= Update;
        Update();
        return window;
    }

    internal static Window Settings(Catalog catalog, Action save, Action toggle, Action quit, Action reset, Action startup,
        bool autorun, bool paused, bool canStartup, int tier)
    {
        var (window, panel) = Frame("Настройки", 448, 610);
        var options = catalog.Options;
        panel.Children.Add(Check("Вращение орбит", options.Motion, value => { options.Motion = value; save(); }));
        panel.Children.Add(Check("Экономия GPU — пауза без движения мыши", options.SaveEnergy, value => { options.SaveEnergy = value; save(); }));
        panel.Children.Add(Check("Подписи объектов", options.Labels, value => { options.Labels = value; save(); }));
        panel.Children.Add(Check("Звёзды и туманность", options.Stars, value => { options.Stars = value; save(); }));
        panel.Children.Add(Check("Группировать по типу", options.Group, value => { options.Group = value; save(); }));
        if (canStartup) panel.Children.Add(Check("Запускать при входе в Windows", autorun, _ => startup()));

        panel.Children.Add(Scale("Количество орбит", options.Rings, 2, 8, 1, value => { options.Rings = (int)value; save(); }));
        panel.Children.Add(Scale("Скорость движения", options.Speed, 0, 2, 0.1, value => { options.Speed = value; save(); }));
        panel.Children.Add(Scale("Интенсивность свечения", options.Glow, 0, 1, 0.05, value => { options.Glow = value; save(); }));
        panel.Children.Add(Scale("Частота кадров", options.FrameRate, 15, 60, 3, value => { options.FrameRate = (int)value; save(); }));

        var core = new ComboBox { Width = 156, ItemsSource = new[] { "Сфера", "Звезда", "Атом" }, SelectedIndex = options.Core };
        core.SelectionChanged += (_, _) => { options.Core = core.SelectedIndex; save(); };
        panel.Children.Add(Row("Центральный объект", core));

        var presentation = new[] { "Обычное ядро" }.Concat(catalog.Items.Where(item => item.Category == 0).Select(item => item.Name)).ToArray();
        var paths = new string?[] { null }.Concat(catalog.Items.Where(item => item.Category == 0).Select(item => (string?)item.Path)).ToArray();
        var centerApp = new ComboBox
        {
            Width = 184, ItemsSource = presentation,
            SelectedIndex = Math.Max(0, Array.FindIndex(paths, path => string.Equals(path, options.CoreApp, StringComparison.OrdinalIgnoreCase)))
        };
        centerApp.SelectionChanged += (_, _) =>
        {
            if (centerApp.SelectedIndex < 0) return;
            options.CoreApp = paths[centerApp.SelectedIndex];
            save();
        };
        panel.Children.Add(Row("Приложение в центре", centerApp));

        var capabilities = Label(tier > 0 ? $"WPF · ускорение GPU, уровень {tier}" : "WPF · программный рендер", 11, Muted);
        capabilities.Margin = new Thickness(0, 10, 0, 8);
        panel.Children.Add(capabilities);

        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(Button("Сброс камеры", reset));
        actions.Children.Add(Button(paused ? "Показать Orbit" : "Вернуть стол Windows", () => { toggle(); window.Close(); }));
        actions.Children.Add(Button("Выйти", quit));
        panel.Children.Add(actions);
        return window;
    }

    private static CheckBox Check(string title, bool value, Action<bool> change)
    {
        var check = new CheckBox { Content = title, IsChecked = value, Foreground = Text, Margin = new Thickness(0, 0, 0, 7) };
        check.Checked += (_, _) => change(true);
        check.Unchecked += (_, _) => change(false);
        return check;
    }

    private static FrameworkElement Scale(string title, double value, double min, double max, double step, Action<double> change)
    {
        var control = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = value, Width = 110 };
        var display = Label(value.ToString("0.##"), 12, Muted);
        display.Width = 34;
        display.Margin = new Thickness(8, 0, 0, 0);
        var inline = new StackPanel { Orientation = Orientation.Horizontal };
        inline.Children.Add(control);
        inline.Children.Add(display);
        control.ValueChanged += (_, args) => display.Text = args.NewValue.ToString("0.##");
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        debounce.Tick += (_, _) => { debounce.Stop(); change(control.Value); };
        control.ValueChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        return Row(title, inline);
    }

    private static DockPanel Row(string title, FrameworkElement control)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 7, 0, 4), LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right);
        panel.Children.Add(control);
        panel.Children.Add(Label(title, 13, Text));
        return panel;
    }

    private static TextBlock Label(string value, double size, Brush brush) => new()
    {
        Text = value,
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button Button(string label, Action action)
    {
        var control = new Button
        {
            Content = label,
            Foreground = Title,
            Background = Surface,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(4, 0, 0, 3)
        };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding(nameof(Control.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, ColorBrush(37, 40, 50)));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        control.Template = template;
        control.Click += (_, _) => action();
        return control;
    }

    private static ResourceDictionary? menuStyles;
    private static ResourceDictionary MenuStyles => menuStyles ??= new ResourceDictionary
    {
        Source = new Uri("/Orbit Desktop;component/ContextMenu.xaml", UriKind.Relative)
    };

    internal enum MenuIcon { Open, Folder, Copy, Link, Info, Orbit, Pin, Unpin, Search, Settings, Reset }

    internal static ContextMenu Menu(string caption) => new()
    {
        Style = (Style)MenuStyles["OrbitMenu"],
        Tag = caption
    };

    internal static void Divide(ContextMenu menu) =>
        menu.Items.Add(new Separator { Style = (Style)MenuStyles["OrbitMenuRule"] });

    internal static void Append(ContextMenu menu, MenuIcon icon, string title, Action action)
    {
        var item = new MenuItem
        {
            Style = (Style)MenuStyles["OrbitMenuItem"],
            Header = title,
            Icon = new ShapePath
            {
                Data = Geometry.Parse(icon switch
                {
                    MenuIcon.Open => "M8,2 H14 V8 M14,2 L7,9 M6,3 H2 V14 H13 V10",
                    MenuIcon.Folder => "M1,5 V13 H14 L15,6 H8 L6,3 H2 Q1,3 1,5 Z",
                    MenuIcon.Copy => "M5,5 H14 V14 H5 Z M2,11 V2 H11",
                    MenuIcon.Link => "M6,5 L8,3 A3,3 0 0 1 13,7 L11,9 M10,11 L8,13 A3,3 0 0 1 3,9 L5,7 M6,10 L10,6",
                    MenuIcon.Info => "M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M8,7 V11.5 M8,4.6 V4.8",
                    MenuIcon.Orbit => "M12.7,3.3 A3,7 45 1 1 12.69,3.29 Z M8,6.6 A1.4,1.4 0 1 1 7.99,6.6 Z",
                    MenuIcon.Pin => "M6,2 H10 V6 L12,9 V10 H4 V9 L6,6 Z M8,10 V15",
                    MenuIcon.Unpin => "M6,2 H10 V6 L12,9 M4,9 L5,7 M7,10 H4 M8,10 V15 M2,2 L14,14",
                    MenuIcon.Search => "M6.7,2 A4.7,4.7 0 1 1 6.69,2 Z M10,10 L14,14",
                    MenuIcon.Settings => "M1.5,4 H5 M9,4 H14.5 M5,4 A2,2 0 1 1 9,4 A2,2 0 1 1 5,4 M1.5,12 H8 M12,12 H14.5 M8,12 A2,2 0 1 1 12,12 A2,2 0 1 1 8,12",
                    _ => "M2.5,6 A5.8,5.8 0 1 1 2.5,10 M2,2 V6.5 H6.5"
                }),
                Stroke = Title,
                Opacity = 0.77,
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            }
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
