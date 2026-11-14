using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OrbitDesktop;

internal sealed class Galaxy : FrameworkElement
{
    private const double Tau = Math.PI * 2;
    private static readonly Typeface Sans = new("Segoe UI");
    private static readonly Typeface Mono = new("Consolas");
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly SolidColorBrush[] Grays = Enumerable.Range(0, 256)
        .Select(opacity => Freeze(new SolidColorBrush(Color.FromArgb((byte)opacity, 215, 219, 230)))).ToArray();
    private static readonly Geometry Folder = Freeze(Geometry.Parse("M -10,-5 L -10,-8 -2,-8 1,-5 10,-5 10,7 -10,7 Z"));
    private static readonly Geometry File = Freeze(Geometry.Parse("M -8,-11 L 3,-11 8,-6 8,11 -8,11 Z M 3,-11 L 3,-6 8,-6"));
    private static readonly Brush Haze = Radial(Colors.White, 20);
    private static readonly Brush CoreLight = Radial(Color.FromRgb(237, 239, 246), 242);
    private static readonly Brush Orb = Freeze(new LinearGradientBrush(Color.FromRgb(63, 65, 73), Color.FromRgb(12, 14, 19), 63));
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(4, 5, 8)));
    private static readonly ImageSource Nebula = Freeze(new BitmapImage(new Uri("pack://application:,,,/Nebula.jpg")));
    private static readonly Geometry FolderShell = Polygon((-17, -9), (-7, -17), (10, -17), (17, -9), (17, 9), (8, 17), (-10, 17), (-17, 9));
    private static readonly Geometry FileShell = Polygon((0, -20), (17, -10), (17, 10), (0, 20), (-17, 10), (-17, -10));
    private static readonly (string Name, Geometry Mark)[] CustomMarks =
    [
        ("Spotify", Freeze(Geometry.Parse("M -9,-4 Q 1,-8 9,-2 M -7,0 Q 1,-3 7,2 M -5,4 Q 1,2 5,6"))),
        ("Obsidian", Freeze(Geometry.Parse("M 1,-11 L 7,-6 9,6 4,11 -5,8 -8,1 -4,-4 Z M 1,-11 L -1,-1 4,11 M -8,1 L -1,-1 9,6"))),
        ("Blender", Freeze(Geometry.Parse("M -10,-4 L -3,-4 -8,-8 M -3,-4 L 1,-4 M -10,1 L -4,1 M -4,1 C -4,-7 10,-8 10,1 C 10,10 -4,9 -4,1 Z M 0,1 C 0,-3 6,-3 6,1 C 6,5 0,5 0,1 Z"))),
        ("Unity Hub", Freeze(Geometry.Parse("M -9,-4 L 0,-9 9,-4 9,6 0,11 -9,6 Z M -9,-4 L 0,1 9,-4 M 0,1 L 0,11"))),
        ("Steam", Freeze(Geometry.Parse("M -10,6 L -3,8 2,4 M -5,6 A 3,3 0 1 0 1,6 A 3,3 0 1 0 -5,6 M 1,-4 A 5,5 0 1 0 11,-4 A 5,5 0 1 0 1,-4 M 3,-4 A 3,3 0 1 0 9,-4 A 3,3 0 1 0 3,-4"))),
        ("Roblox", Freeze(Geometry.Parse("M -5,-10 L 10,-5 5,10 -10,5 Z M -1,-3 L 3,-1 1,3 -3,1 Z"))),
        ("Tor Browser", Freeze(Geometry.Parse("M 0,-10 A 10,10 0 1 1 0,10 A 10,10 0 1 1 0,-10 M 0,-7 A 7,7 0 0 1 0,7 M 0,-4 A 4,4 0 0 1 0,4"))),
        ("VALORANT", Freeze(Geometry.Parse("M -10,-6 L -10,0 2,9 7,9 Z M 10,-5 L 10,0 5,3 2,0 Z"))),
    ];
    private static readonly string[] Categories = ["ПРИЛОЖЕНИЯ", "ПАПКИ", "ДОКУМЕНТЫ", "МЕДИА", "АРХИВЫ"];
    private readonly Catalog catalog;
    private readonly Func<bool> foreground;
    private readonly Action openSearch;
    private readonly Action openSettings;
    private readonly Action showError;
    private readonly Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Node> sorted = [];
    private readonly List<(double X, double Y, double Size, int Light)> stars = [];
    private readonly List<(double Angle, double Radius, double Lift, double Size, int Light)> dust = [];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastTick;
    private double phase;
    private double pulse;
    private double zoom = 1, desiredZoom = 1;
    private Vector pan, desiredPan, velocity, parallax;
    private Point pointer = new(-100, -100), lastMouse, downAt;
    private Node? over, pressed, selected;
    private bool panning, dragging, down, dirty = true;
    private int dropRing = -1;
    private double dropAngle;
    private int ringCount;
    private int activeGroup = -1;
    private DrawingGroup? sky;
    private DrawingGroup? disk;
    private SceneState? sceneState;
    private readonly Dictionary<(string Value, double Size, double Width, bool Mono, int Alpha, double Dpi), FormattedText> formatted = [];
    private bool running, rendering, covered;
    private TimeSpan? frameTime;
    private double frameDeadline;
    private double angularSpeed;
    private double lastInteraction = double.NegativeInfinity;
    private Node? centerApp;
    private readonly record struct SceneState(double Width, double Height, int Rings, bool Stars, double Glow, int Core, bool HasCoreApp);
    internal bool Suspended { get; set; }
    internal PrefView View => new(nodes.Count, ringCount, activeGroup);
    internal readonly record struct PrefView(int Count, int Rings, int Group);

    private sealed class Node(DesktopItem item)
    {
        internal DesktopItem Item = item;
        internal readonly Geometry? Mark = CustomMarks.FirstOrDefault(mark => item.Name.StartsWith(mark.Name, StringComparison.OrdinalIgnoreCase)).Mark;
        internal Position Slot = new(0, 0, false);
        internal Point Location;
        internal double Scale = 1;
        internal double Hover;
        internal double Depth;
        internal bool Initialized;
    }

    internal Galaxy(Catalog catalog, Func<bool> foreground, Action search, Action settings, Action error)
    {
        this.catalog = catalog;
        this.foreground = foreground;
        openSearch = search;
        openSettings = settings;
        showError = error;
        Focusable = true;
        ClipToBounds = true;
        var random = new Random(626200);
        for (int i = 0; i < 560; i++)
        {
            double x = random.NextDouble(), y = random.NextDouble();
            stars.Add((x, y, 0.25 + random.NextDouble() * (i % 20 == 0 ? 1.18 : 0.67),
                9 + random.Next(15, i % 23 == 0 ? 151 : 72)));
            _ = random.NextDouble();
        }
        for (int i = 0; i < 1550; i++)
        {
            double angle = random.NextDouble() * Tau;
            double band = (i % 5) switch { 0 => 0.40, 1 => 0.61, 2 => 0.80, 3 => 0.985, _ => 1.07 };
            double deviation = (random.NextDouble() + random.NextDouble() - 1) * 0.061;
            dust.Add((angle, band + deviation, (random.NextDouble() - 0.5) * 11,
                0.28 + random.NextDouble() * (i % 37 == 0 ? 0.95 : 0.43),
                random.Next(12, i % 31 == 0 ? 159 : 60)));
        }
        timer.Tick += (_, _) => UpdateCoverage();
        Reload();
    }

    internal void Start()
    {
        running = true;
        covered = foreground();
        timer.Start();
        SetRendering(true);
    }

    internal void Stop()
    {
        running = false;
        timer.Stop();
        SetRendering(false);
    }

    internal void UpdateCoverage()
    {
        covered = foreground();
        if (covered && !dirty)
        {
            angularSpeed = 0;
            SetRendering(false);
        }
        else if (dirty || WantsMotion(clock.Elapsed.TotalSeconds)) SetRendering(true);
    }

    private void SetRendering(bool enabled)
    {
        if (enabled == rendering || enabled && !running) return;
        rendering = enabled;
        if (enabled)
        {
            lastTick = clock.Elapsed.TotalSeconds - 1.0 / 60;
            frameTime = null;
            frameDeadline = 0;
            CompositionTarget.Rendering += OnFrame;
        }
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        var timestamp = ((RenderingEventArgs)args).RenderingTime;
        if (timestamp == frameTime) return;
        frameTime = timestamp;
        double present = timestamp.TotalSeconds;
        double interval = 1.0 / catalog.Options.FrameRate;
        if (present + 0.001 < frameDeadline) return;
        frameDeadline = frameDeadline == 0 || present - frameDeadline > interval * 2
            ? present + interval : frameDeadline + interval;
        Tick();
    }

    internal void Reload()
    {
        var paths = catalog.Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in nodes.Keys.Where(path => !paths.Contains(path)).ToArray()) nodes.Remove(path);
        foreach (var item in catalog.Items)
        {
            if (nodes.TryGetValue(item.Path, out var node)) node.Item = item;
            else nodes[item.Path] = new Node(item);
        }

        sorted.Clear();
        centerApp = catalog.Options.CoreApp is string corePath && nodes.TryGetValue(corePath, out var app) &&
            app.Item.Category == 0 ? app : null;
        sorted.AddRange(nodes.Values.Where(node => !ReferenceEquals(node, centerApp)));
        if (selected is not null && !paths.Contains(selected.Item.Path)) selected = null;
        if (pressed is not null && !paths.Contains(pressed.Item.Path)) { pressed = null; dragging = false; }
        ringCount = Math.Max(catalog.Options.Rings, Math.Min(36, (int)Math.Ceiling(Math.Sqrt(nodes.Count / 1.9))));
        int[] counts = new int[ringCount];
        var reserved = Enumerable.Range(0, ringCount).Select(_ => new List<double>()).ToArray();
        foreach (var node in sorted)
        {
            if (!catalog.Options.Positions.TryGetValue(node.Item.Path, out var slot) || !double.IsFinite(slot.Angle))
                continue;
            node.Slot = slot with { Ring = Math.Clamp(slot.Ring, 0, ringCount - 1) };
            reserved[node.Slot.Ring].Add(node.Slot.Angle);
            counts[node.Slot.Ring]++;
        }
        var automaticNodes = catalog.Items.Where(item => item.Path != centerApp?.Item.Path &&
            (!catalog.Options.Positions.TryGetValue(item.Path, out var slot) || !double.IsFinite(slot.Angle)));
        if (catalog.Options.Group) automaticNodes = automaticNodes.OrderBy(item => item.Category);
        foreach (var item in automaticNodes)
        {
            int preferred = catalog.Options.Group ? item.Category % ringCount : 0;
            int ring = Enumerable.Range(0, ringCount).MinBy(r =>
                (double)counts[r] / Capacity(r) + (catalog.Options.Group && r != preferred ? 0.37 : 0));
            double angle = OpenAngle(reserved[ring], -1.14 + ring * 0.77);
            var node = nodes[item.Path];
            node.Slot = new Position(angle, ring, false);
            reserved[ring].Add(angle);
            counts[ring]++;
        }
        Refresh();
    }

    private static int Capacity(int ring) => 5 + ring * 5;

    private static double OpenAngle(List<double> occupied, double seed)
    {
        if (occupied.Count == 0) return seed;
        var ordered = occupied.Select(Normalize).Order().ToArray();
        double best = 0, gap = -1;
        for (int i = 0; i < ordered.Length; i++)
        {
            double next = i + 1 < ordered.Length ? ordered[i + 1] : ordered[0] + Tau;
            if (next - ordered[i] <= gap) continue;
            gap = next - ordered[i];
            best = Normalize(ordered[i] + gap * 0.5);
        }
        return best;
    }

    internal void Refresh() { dirty = true; SetRendering(true); }
    internal void ResetCamera()
    {
        desiredPan = new Vector();
        desiredZoom = 1;
        velocity = new Vector();
        selected = null;
        activeGroup = -1;
        Refresh();
    }

    internal void FocusItem(DesktopItem item)
    {
        if (!nodes.TryGetValue(item.Path, out selected)) return;
        activeGroup = -1;
        desiredZoom = 1.35;
        pulse = 1;
        TrackFocus();
        Refresh();
    }

    private void TrackFocus()
    {
        if (selected is null) return;
        if (ReferenceEquals(selected, centerApp)) { desiredPan = new Vector(); return; }
        var sceneCenter = Center;
        double angle = selected.Slot.Angle + Offset(selected.Slot);
        Point unpanned = Project(selected.Slot.Ring, angle, desiredZoom, new Vector(), out _);
        desiredPan = sceneCenter - unpanned;
    }

    private void FocusGroup(int category)
    {
        var inGroup = nodes.Values.Where(node => node.Item.Category == category).ToArray();
        if (activeGroup == category || inGroup.Length == 0) { ResetCamera(); return; }
        selected = null;
        activeGroup = category;
        desiredZoom = 1.17;
        desiredPan = new Vector();
        pulse = 1;
        Refresh();
    }

    private bool WantsMotion(double now) => catalog.Options.Motion && catalog.Options.Speed > 0 && !down &&
        (!catalog.Options.SaveEnergy || now - lastInteraction < 1.8);

    private void Tick()
    {
        double time = clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(time - lastTick, 0, 0.05);
        lastTick = time;
        if (Suspended || ActualWidth < 1 || ActualHeight < 1 || covered && !dirty)
        {
            angularSpeed = 0;
            SetRendering(false);
            return;
        }

        double targetSpeed = !covered && WantsMotion(time) ? 0.027 + 0.29 * catalog.Options.Speed : 0;
        angularSpeed = down || covered ? 0 : angularSpeed + (targetSpeed - angularSpeed) * (1 - Math.Exp(-dt * 10));
        if (angularSpeed < 0.001) angularSpeed = 0;
        bool moving = angularSpeed > 0;
        if (moving) phase = Normalize(phase + dt * angularSpeed);
        if (moving && selected is not null) TrackFocus();

        Vector targetParallax = IsMouseOver
            ? new((pointer.X / ActualWidth - 0.5) * 24, (pointer.Y / ActualHeight - 0.5) * 18)
            : new();
        bool transition = zoom != desiredZoom || pan != desiredPan || targetParallax != parallax || velocity.LengthSquared > 1;
        if (!panning && velocity.LengthSquared > 1)
        {
            desiredPan += velocity * dt;
            velocity *= Math.Exp(-dt * 6);
            LimitPan();
        }
        double easing = 1 - Math.Exp(-dt * 12);
        zoom += (desiredZoom - zoom) * easing;
        pan += (desiredPan - pan) * easing;
        parallax += (targetParallax - parallax) * easing;
        if ((targetParallax - parallax).LengthSquared < 0.04) parallax = targetParallax;
        if ((pan - desiredPan).LengthSquared < 0.05) pan = desiredPan;
        if (Math.Abs(zoom - desiredZoom) < 0.001) zoom = desiredZoom;
        pulse *= Math.Exp(-dt * 3.2);
        bool animating = dropRing != -1 || pulse > 0.012;

        over = IsMouseOver && !panning ? Hit(pointer) : null;
        if (centerApp is not null)
        {
            centerApp.Location = CoreCenter;
            centerApp.Depth = 0;
            centerApp.Initialized = true;
            double hover = ReferenceEquals(centerApp, over) ? 1 : 0;
            if (Math.Abs(centerApp.Hover - hover) > 0.003) animating = true;
            centerApp.Hover += (hover - centerApp.Hover) * easing;
        }
        foreach (var node in sorted)
        {
            double hover = ReferenceEquals(node, over) ? 1 : over is not null && node.Slot.Ring == over.Slot.Ring &&
                (node.Location - over.Location).LengthSquared < 11500 ? 0.12 : 0;
            double hoverDelta = hover - node.Hover;
            if (hoverDelta != 0) animating = true;
            node.Hover = Math.Abs(hoverDelta) < 0.003 ? hover : node.Hover + hoverDelta * easing;

            var target = Project(node.Slot.Ring, node.Slot.Angle + Offset(node.Slot), zoom, pan, out var depth);
            node.Depth = depth;
            if (dragging && ReferenceEquals(node, pressed)) target = pointer;
            if (!node.Initialized) { node.Location = target; node.Initialized = true; }
            Vector delta = target - node.Location;
            if (delta.LengthSquared > 0) animating = true;
            node.Location = delta.LengthSquared < 0.14 ? target
                : node.Location + delta * (dragging && ReferenceEquals(node, pressed) ? Math.Min(1, easing * 2) : easing);
        }
        if (moving || transition || animating || dirty)
        {
            dirty = false;
            InvalidateVisual();
        }
        else SetRendering(false);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        foreach (var node in sorted) node.Initialized = false;
        Refresh();
    }

    private Point Center => new(ActualWidth * 0.5, ActualHeight * 0.49);
    private Point CoreCenter => Center + pan + parallax * 0.45;
    private double Disk => Math.Max(48, Math.Min(ActualWidth * 0.412, ActualHeight * 0.625));
    private static double Normalize(double angle) => angle - Tau * Math.Floor(angle / Tau);
    private double Offset(Position slot) => slot.Pinned ? 0 : phase;

    private Point Project(int ring, double angle, double magnification, Vector translation, out double depth)
        => CoreCenter - pan + translation + OrbitPoint(ring, angle, out depth) * magnification;

    private Vector OrbitPoint(int ring, double angle, out double depth)
    {
        double radius = Disk * (0.255 + 0.745 * ring / Math.Max(1, ringCount - 1));
        double vertical = radius * Math.Sin(angle);
        double x = radius * Math.Cos(angle);
        double y = vertical * (0.47 + ring * 0.006);
        depth = vertical / Disk;
        double perspective = 1 / (1 - depth * 0.092);
        double twist = -0.19 + ring * 0.006;
        double c = Math.Cos(twist), s = Math.Sin(twist);
        return new Vector((c * x - s * y) * perspective, (s * x + c * y) * perspective);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double width = ActualWidth, height = ActualHeight;
        if (width < 2 || height < 2) return;
        var state = new SceneState(width, height, ringCount, catalog.Options.Stars, catalog.Options.Glow,
            catalog.Options.Core, centerApp is not null);
        if (state != sceneState) PrepareEnvironment(state);
        drawing.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (sky is not null)
        {
            drawing.PushTransform(new TranslateTransform(parallax.X * 0.24, parallax.Y * 0.24));
            drawing.DrawDrawing(sky);
            drawing.Pop();
        }
        if (disk is not null)
        {
            var center = CoreCenter;
            drawing.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, center.X, center.Y));
            drawing.DrawDrawing(disk);
            drawing.Pop();
        }

        if (dragging && pressed is not null && dropRing >= 0)
        {
            int ring = dropRing;
            drawing.DrawGeometry(null, Pen(118), Trace(t => Project(ring, t, zoom, pan, out _), 0, Tau, 96));
            Point target = Project(dropRing, dropAngle, zoom, pan, out _);
            drawing.DrawLine(Pen(89), target, pressed.Location);
            drawing.DrawEllipse(Grays[35], Pen(160), target, 4, 4);
        }

        sorted.Sort((left, right) => left.Depth.CompareTo(right.Depth));
        foreach (var node in sorted)
        {
            if (ReferenceEquals(node, over) || ReferenceEquals(node, pressed)) continue;
            DrawNode(drawing, node);
        }
        if (over is not null && !ReferenceEquals(over, pressed)) DrawNode(drawing, over);
        if (pressed is not null) DrawNode(drawing, pressed);
        if (centerApp is not null && !ReferenceEquals(centerApp, over) && !ReferenceEquals(centerApp, pressed))
            DrawNode(drawing, centerApp);
        DrawControls(drawing, width, height);
    }

    private void PrepareEnvironment(SceneState state)
    {
        if (sceneState is not SceneState old || old.Width != state.Width || old.Height != state.Height || old.Stars != state.Stars)
        {
            sky = null;
            if (state.Stars)
            {
                var group = new DrawingGroup();
                using (var d = group.Open())
                {
                    d.DrawImage(Nebula, new Rect(-16, -16, state.Width + 32, state.Height + 32));
                    foreach (var star in stars.Take(state.Width > 850 ? 560 : 300))
                    {
                        var at = new Point(star.X * state.Width, star.Y * state.Height);
                        d.DrawEllipse(Grays[star.Light], null, at, star.Size, star.Size);
                        if (star.Light <= 115) continue;
                        d.DrawLine(Pen(22), at - new Vector(2.4, 0), at + new Vector(2.4, 0));
                        d.DrawLine(Pen(22), at - new Vector(0, 2.4), at + new Vector(0, 2.4));
                    }
                }
                sky = Freeze(group);
            }
        }
        var orbits = new DrawingGroup();
        using (var d = orbits.Open())
        {
            if (state.Stars)
            {
                double cos = Math.Cos(-0.19), sin = Math.Sin(-0.19);
                foreach (var grain in dust)
                {
                    double x = Math.Cos(grain.Angle) * Disk * grain.Radius;
                    double y = Math.Sin(grain.Angle) * Disk * grain.Radius * 0.493;
                    double depth = y / (Disk * 0.493);
                    double scale = 1 / (1 - depth * 0.09);
                    Point at = new((x * cos - y * sin) * scale, (x * sin + y * cos + grain.Lift) * scale);
                    d.DrawEllipse(Grays[grain.Light], null, at, grain.Size, grain.Size);
                }
            }
            DrawRings(d);
            if (!state.HasCoreApp) DrawCore(d, new Point());
        }
        disk = Freeze(orbits);
        sceneState = state;
    }

    private void DrawRings(DrawingContext d)
    {
        for (int ring = 0; ring < ringCount; ring++)
        {
            int index = ring;
            Point InDisk(double angle) => new Point() + OrbitPoint(index, angle, out _);
            var front = Trace(InDisk, 0, Math.PI, 52);
            var back = Trace(InDisk, Math.PI, Tau, 52);
            d.DrawGeometry(null, Pen(30), front);
            d.DrawGeometry(null, Pen(20), back);
            if (ringCount > 11) continue;
            for (int tick = 0; tick < 32; tick++)
            {
                double angle = Tau * tick / 32;
                var at = InDisk(angle);
                double size = tick % 9 == 0 ? 1.35 : 0.65;
                d.DrawEllipse(Grays[tick % 9 == 0 ? 110 : angle < Math.PI ? 39 : 25], null, at, size, size);
                if (tick % 9 == 0 && catalog.Options.Glow > 0)
                    d.DrawEllipse(Haze, null, at, 8, 8);
            }
        }
    }

    private void DrawCore(DrawingContext d, Point core)
    {
        double size = Math.Clamp(Disk * 0.063, 13, 38);
        double glow = catalog.Options.Glow;
        if (glow > 0)
        {
            d.PushOpacity(glow);
            d.DrawEllipse(Haze, null, core, size * 3.45, size * 3.45);
            d.DrawEllipse(Haze, null, core, size * 1.83, size * 1.83);
            d.Pop();
        }
        d.DrawEllipse(null, Pen(24), core, size * 1.23, size * 1.23);
        if (catalog.Options.Core == 1)
        {
            d.DrawEllipse(CoreLight, null, core, size * 0.57, size * 0.57);
            d.DrawEllipse(null, Pen(150), core, size * 0.39, size * 0.39);
            d.DrawLine(Pen(91), core - new Vector(size, 0), core + new Vector(size, 0));
        }
        else if (catalog.Options.Core == 2)
        {
            for (int i = 0; i < 3; i++)
            {
                d.PushTransform(new RotateTransform(i * 60, core.X, core.Y));
                d.DrawEllipse(null, Pen(127), core, size * 0.62, size * 0.21);
                d.Pop();
            }
            d.DrawEllipse(Grays[232], null, core, 3.1, 3.1);
        }
        else
        {
            d.DrawEllipse(Orb, Pen(161), core, size * 0.83, size * 0.83);
            d.PushTransform(new RotateTransform(-17, core.X, core.Y));
            for (int i = 1; i <= 4; i++)
                d.DrawEllipse(null, Pen(53 + i * 9), core, size * 0.17 * i, size * 0.83);
            for (int i = -3; i <= 3; i++)
            {
                double latitude = i * Math.PI / 10;
                var at = core + new Vector(0, size * 0.74 * Math.Sin(latitude));
                d.DrawEllipse(null, Pen(i == 0 ? 104 : 67), at, size * 0.81 * Math.Cos(latitude), size * 0.13 * Math.Cos(latitude));
            }
            d.Pop();
        }
        Text(d, "ЦЕНТР", core + new Vector(0, size * 1.31), 8, 125, true, true);
    }

    private void DrawNode(DrawingContext d, Node node)
    {
        var point = node.Location;
        if (!node.Initialized || point.X < -110 || point.Y < -65 || point.X > ActualWidth + 110 || point.Y > ActualHeight + 65) return;
        bool chosen = ReferenceEquals(node, selected);
        bool isCore = ReferenceEquals(node, centerApp);
        double crowd = Math.Clamp(1 - (nodes.Count - 36) * 0.005, 0.63, 1);
        double scale = Math.Clamp(Math.Min(ActualWidth, ActualHeight) / 1000, 0.57, 1.08) *
            (0.94 + node.Depth * 0.11) * (1 + node.Hover * 0.24 + (chosen ? pulse * 0.32 : 0)) *
            Math.Clamp(Math.Sqrt(zoom), 0.71, 1.65) * (isCore ? 1.65 : crowd);
        node.Scale = scale;
        d.PushOpacity(activeGroup >= 0 && node.Item.Category != activeGroup ? 0.22 : 1);
        d.PushTransform(new TranslateTransform(point.X, point.Y));
        d.PushTransform(new ScaleTransform(scale, scale));
        double glow = catalog.Options.Glow;
        if (glow > 0)
        {
            d.PushOpacity((0.48 + node.Hover * 0.44) * glow);
            d.DrawEllipse(Haze, null, new Point(), 33, 33);
            d.Pop();
        }

        int edge = (int)(110 + node.Hover * 100 + (chosen ? 31 : 0));
        Point zero = new();
        switch (node.Item.Category)
        {
            case 0:
                d.DrawEllipse(Orb, Pen(edge), zero, 18, 18);
                d.DrawEllipse(null, Pen(edge / 4), zero, 21, 21);
                if (node.Mark is not null) d.DrawGeometry(null, Pen(233), node.Mark);
                else if (node.Item.Icon is not null) d.DrawImage(node.Item.Icon, new Rect(-10, -10, 20, 20));
                else Text(d, First(node.Item.Name), new Point(0, -8), 12, 230, true, true);
                break;
            case 1:
                d.DrawGeometry(Orb, Pen(edge), FolderShell);
                d.DrawGeometry(null, Pen(216), Folder);
                break;
            default:
                d.DrawGeometry(Orb, Pen(edge), FileShell);
                d.DrawGeometry(null, Pen(99), File);
                Text(d, node.Item.Type, new Point(0, -2), node.Item.Type.Length > 5 ? 5.8 : 7.5, 220, true, true, 28);
                break;
        }

        if (node.Slot.Pinned && !isCore) d.DrawEllipse(Grays[230], Pen(56), new Point(14, -16), 2.1, 2.1);
        if (chosen && pulse > 0.01)
        {
            double wave = 20 + (1 - pulse) * 38;
            d.DrawEllipse(null, Pen((int)(170 * pulse)), zero, wave, wave);
        }
        d.Pop();
        d.Pop();
        if (isCore || catalog.Options.Labels || node.Hover > 0.16 || chosen)
        {
            var label = Format(node.Item.Name, 11, node.Hover > 0.16 || chosen ? 193 : 106, false,
                chosen || node.Hover > 0.16 ? 250 : 174);
            var origin = new Point(point.X - label.Width / 2, point.Y + 23 * scale + 3);
            d.DrawRoundedRectangle(Background, null, new Rect(origin.X - 4, origin.Y - 1, label.Width + 8, label.Height + 2), 4, 4);
            d.DrawText(label, origin);
        }
        d.Pop();
    }

    private void DrawControls(DrawingContext d, double width, double height)
    {
        d.DrawLine(Pen(24), new Point(34, 75), new Point(width - 34, 75));
        d.DrawEllipse(null, Pen(133), new Point(45, 44), 10, 10);
        d.DrawEllipse(null, Pen(51), new Point(45, 44), 14, 14);
        d.DrawEllipse(Grays[232], null, new Point(45, 44), 2, 2);
        Text(d, "ORBIT", new Point(66, 23), 21, 255, false);
        Text(d, "РАБОЧИЙ СТОЛ", new Point(66, 49), 9, 138, true);
        var search = SearchRect;
        var settings = SettingsRect;
        d.DrawRoundedRectangle(Orb, Pen(search.Contains(pointer) ? 127 : 61), search, 7, 7);
        d.DrawEllipse(null, Pen(180), new Point(search.Left + 17, search.Top + 14), 4.2, 4.2);
        d.DrawLine(Pen(180), new Point(search.Left + 20, search.Top + 17), new Point(search.Left + 24, search.Top + 21));
        Text(d, "Поиск", new Point(search.Left + 33, search.Top + 8), 11, 220, false);
        d.DrawRoundedRectangle(Orb, Pen(settings.Contains(pointer) ? 127 : 61), settings, 7, 7);
        Text(d, "⋯", new Point(settings.Left + settings.Width / 2, settings.Top + 3), 20, 226, false, true);

        d.DrawLine(Pen(24), new Point(34, height - 56), new Point(width - 34, height - 56));
        d.DrawEllipse(Grays[catalog.Options.Motion ? 186 : 71], null, new Point(39, height - 31), 2, 2);
        Text(d, catalog.Options.Motion ? "ДВИЖЕНИЕ" : "СТАТИКА", new Point(50, height - 38), 10, 141, true);
        Text(d, $"{nodes.Count:D2} ОБЪЕКТОВ   /   {ringCount:D2} ОРБИТ", new Point(width - 186, height - 38), 10, 141, true);
        if (width > 780)
        {
            for (int i = 0; i < Categories.Length; i++)
            {
                var rect = GroupRect(i);
                int count = sorted.Count(n => n.Item.Category == i);
                if (count == 0) continue;
                Text(d, Categories[i], new Point(rect.Left + rect.Width / 2, rect.Top + 6), 10,
                    activeGroup == i || rect.Contains(pointer) ? 247 : 119, true, true);
            }
        }
    }

    private Rect SearchRect => new(Math.Max(164, ActualWidth - 269), 28, Math.Max(69, Math.Min(184, ActualWidth - 245)), 33);
    private Rect SettingsRect => new(ActualWidth - 73, 28, 39, 33);
    private Rect GroupRect(int group) => new(ActualWidth / 2 - 239 + group * 86, ActualHeight - 44, 84, 27);

    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }

    private static Brush Radial(Color c, byte alpha) => Freeze(new RadialGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(alpha, c.R, c.G, c.B), 0),
            new(Color.FromArgb((byte)(alpha * 0.34), c.R, c.G, c.B), 0.49),
            new(Color.FromArgb(0, c.R, c.G, c.B), 1) }));

    private static Pen Pen(int alpha) => Pens[Math.Clamp(alpha, 0, 255)];
    private static readonly Pen[] Pens = Grays.Select(brush => Freeze(new Pen(brush, 1))).ToArray();

    private static Geometry Trace(Func<double, Point> fn, double from, double to, int steps)
    {
        var geometry = new StreamGeometry();
        using var stream = geometry.Open();
        stream.BeginFigure(fn(from), false, false);
        for (int i = 1; i <= steps; i++) stream.LineTo(fn(from + (to - from) * i / steps), true, false);
        return geometry;
    }

    private static Geometry Polygon(params (double X, double Y)[] vertices)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(vertices[0].X, vertices[0].Y), true, true);
            foreach (var p in vertices.Skip(1)) path.LineTo(new Point(p.X, p.Y), true, false);
        }
        return geometry;
    }

    private static string First(string name) => string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();

    private FormattedText Format(string value, double size, double max, bool mono, int alpha)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = (value, size, max, mono, alpha, dpi);
        if (formatted.TryGetValue(key, out var text)) return text;
        if (formatted.Count >= 1024) formatted.Clear();
        text = new(value, Culture, FlowDirection.LeftToRight, mono ? Mono : Sans, size, Grays[alpha], dpi)
            { MaxTextWidth = max, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        formatted[key] = text;
        return text;
    }

    private void Text(DrawingContext d, string value, Point p, double size, int alpha, bool mono, bool center = false, double max = 380)
    {
        var formatted = Format(value, size, max, mono, alpha);
        if (center) p.X -= formatted.Width / 2;
        d.DrawText(formatted, p);
    }

    private Node? Hit(Point point)
    {
        if (centerApp is not null && (CoreCenter - point).LengthSquared < Math.Pow(23 * centerApp.Scale, 2)) return centerApp;
        return sorted.AsEnumerable().Reverse()
            .FirstOrDefault(node => node.Initialized && (node.Location - point).LengthSquared < Math.Pow(23 * node.Scale, 2));
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        pointer = args.GetPosition(this);
        MarkInteraction();
        if (down && !dragging && !panning && (pointer - downAt).LengthSquared > 25)
        {
            if (pressed is null) { panning = true; selected = null; }
            else if (!pressed.Slot.Pinned && !ReferenceEquals(pressed, centerApp)) { dragging = true; selected = null; }
        }
        if (panning)
        {
            Vector delta = pointer - lastMouse;
            desiredPan += delta;
            velocity = velocity * 0.47 + delta * 23;
            LimitPan();
        }
        if (dragging) FindDrop();
        lastMouse = pointer;
        Cursor = dragging || panning ? Cursors.SizeAll : SearchRect.Contains(pointer) || SettingsRect.Contains(pointer) ||
            Hit(pointer) is not null ? Cursors.Hand : Cursors.Arrow;
        Refresh();
    }

    protected override void OnMouseLeave(MouseEventArgs args)
    {
        base.OnMouseLeave(args);
        if (!down) pointer = new Point(-100, -100);
        Refresh();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        MarkInteraction();
        Focus();
        pointer = args.GetPosition(this);
        if (SettingsRect.Contains(pointer)) { openSettings(); return; }
        if (SearchRect.Contains(pointer)) { openSearch(); return; }
        if (ActualWidth > 780)
        {
            for (int i = 0; i < Categories.Length; i++)
            {
                if (!GroupRect(i).Contains(pointer)) continue;
                FocusGroup(i);
                return;
            }
        }
        down = true;
        pressed = Hit(pointer);
        lastMouse = downAt = pointer;
        velocity = new Vector();
        CaptureMouse();
        args.Handled = true;
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (!down) return;
        var item = pressed;
        bool drop = dragging && item is not null && dropRing >= 0;
        bool launch = item is not null && !dragging && (pointer - downAt).LengthSquared <= 36;
        int ring = dropRing;
        double angle = dropAngle;
        ClearGesture();
        ReleaseMouseCapture();
        if (drop && item is not null)
        {
            item.Slot = new Position(Normalize(angle - phase), ring, false);
            catalog.Options.Positions[item.Item.Path] = item.Slot;
            Save();
        }
        if (launch && item is not null)
        {
            selected = item;
            pulse = 1;
            var feedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(155) };
            feedback.Tick += (_, _) => { feedback.Stop(); Open(item.Item.Path); };
            feedback.Start();
        }
        args.Handled = true;
        Refresh();
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        ClearGesture();
        Refresh();
    }

    private void ClearGesture()
    {
        dragging = panning = down = false;
        dropRing = -1;
        pressed = null;
    }

    private void LimitPan()
    {
        desiredPan.X = Math.Clamp(desiredPan.X, -ActualWidth * 0.68, ActualWidth * 0.68);
        desiredPan.Y = Math.Clamp(desiredPan.Y, -ActualHeight * 0.74, ActualHeight * 0.74);
    }

    private void FindDrop()
    {
        if (pressed is null) return;
        double best = double.MaxValue, bestFree = double.MaxValue;
        int fallbackRing = 0;
        double fallbackAngle = 0;
        dropRing = -1;
        for (int ring = 0; ring < ringCount; ring++)
        {
            var other = sorted.Where(node => node.Slot.Ring == ring && !ReferenceEquals(node, pressed))
                .Select(node => node.Location).ToArray();
            for (int sample = 0; sample < 144; sample++)
            {
                double angle = sample * Tau / 144;
                Point candidate = Project(ring, angle, zoom, pan, out _);
                double distance = (candidate - pointer).LengthSquared;
                if (distance < best)
                {
                    best = distance;
                    fallbackRing = ring;
                    fallbackAngle = angle;
                }
                if (distance >= bestFree || other.Any(point => (point - candidate).LengthSquared < 43 * 43)) continue;
                bestFree = distance;
                dropRing = ring;
                dropAngle = angle;
            }
        }
        if (dropRing < 0) { dropRing = fallbackRing; dropAngle = fallbackAngle; }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        MarkInteraction();
        selected = null;
        var focal = args.GetPosition(this) - Center;
        double previous = desiredZoom;
        desiredZoom = Math.Clamp(desiredZoom * Math.Pow(1.16, args.Delta / 120.0), 0.6, 2.55);
        desiredPan = focal - (focal - desiredPan) * (desiredZoom / previous);
        LimitPan();
        Refresh();
        args.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseRightButtonUp(args);
        var node = Hit(args.GetPosition(this));
        var menu = Ui.Menu(node?.Item.Name ?? "Рабочий стол");
        if (node is not null)
        {
            Ui.Append(menu, Ui.MenuIcon.Open, "Открыть", () => Open(node.Item.Path));
            Ui.Append(menu, Ui.MenuIcon.Folder, "Показать в Проводнике", () => Call(() => DesktopShell.Reveal(node.Item.Path)));
            Ui.Divide(menu);
            Ui.Append(menu, Ui.MenuIcon.Copy, "Копировать файл", () => Call(() =>
            {
                var paths = new System.Collections.Specialized.StringCollection { node.Item.Path };
                Clipboard.SetFileDropList(paths);
            }));
            Ui.Append(menu, Ui.MenuIcon.Link, "Копировать путь", () => Call(() => Clipboard.SetText(node.Item.Path)));
            Ui.Append(menu, Ui.MenuIcon.Info, "Свойства Windows", () => Call(() => NativeFileProperties.Show(node.Item.Path)));
            Ui.Divide(menu);
            if (node.Item.Category == 0)
            {
                bool isCore = ReferenceEquals(node, centerApp);
                Ui.Append(menu, Ui.MenuIcon.Orbit, isCore ? "Вернуть обычное ядро" : "Поместить в центр", () =>
                {
                    catalog.Options.CoreApp = isCore ? null : node.Item.Path;
                    Save();
                });
            }
            if (!ReferenceEquals(node, centerApp)) Ui.Append(menu, node.Slot.Pinned ? Ui.MenuIcon.Unpin : Ui.MenuIcon.Pin,
                node.Slot.Pinned ? "Открепить от орбиты" : "Закрепить на орбите", () =>
            {
                var slot = node.Slot;
                node.Slot = slot with { Pinned = !slot.Pinned, Angle = Normalize(slot.Angle + (slot.Pinned ? -phase : phase)) };
                catalog.Options.Positions[node.Item.Path] = node.Slot;
                Save();
            });
        }
        else
        {
            Ui.Append(menu, Ui.MenuIcon.Search, "Поиск", openSearch);
            Ui.Append(menu, Ui.MenuIcon.Settings, "Настройки", openSettings);
            Ui.Divide(menu);
            Ui.Append(menu, Ui.MenuIcon.Folder, "Открыть папку рабочего стола", () => Open(catalog.PersonalDesktop));
            Ui.Append(menu, Ui.MenuIcon.Reset, "Сбросить камеру", ResetCamera);
        }
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        args.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && args.Key == Key.F) openSearch();
        else if (args.Key == Key.Escape || args.Key == Key.Home) ResetCamera();
        else if (args.Key == Key.F1) openSettings();
        else return;
        args.Handled = true;
    }

    private void Save()
    {
        Call(catalog.Save);
        SlotsChanged?.Invoke();
        Refresh();
    }

    internal event Action? SlotsChanged;

    private void MarkInteraction()
    {
        lastInteraction = clock.Elapsed.TotalSeconds;
        if (!covered) SetRendering(true);
    }

    private void Open(string path) => Call(() => DesktopShell.Open(path));
    private void Call(Action work)
    {
        try { work(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or
                                   UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            showError();
        }
    }
}
