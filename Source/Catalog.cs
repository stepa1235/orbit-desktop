using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OrbitDesktop;

internal sealed record DesktopItem(string Path, string Name, string Type, int Category, ImageSource? Icon);
internal sealed record Position(double Angle, int Ring, bool Pinned);

internal sealed class Preferences
{
    public bool Motion { get; set; } = true;
    public bool SaveEnergy { get; set; } = true;
    public bool Labels { get; set; }
    public bool Stars { get; set; } = true;
    public bool Group { get; set; } = true;
    public int Rings { get; set; } = 5;
    public int Core { get; set; }
    public string? CoreApp { get; set; }
    public int FrameRate { get; set; } = 60;
    public int TimingVersion { get; set; }
    public double Speed { get; set; } = 0.5;
    public double Glow { get; set; } = 0.6;
    public Dictionary<string, Position> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class Catalog : IDisposable
{
    private readonly string directory;
    private readonly string[] roots;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly DispatcherTimer settle;
    private readonly DispatcherTimer rescan;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

    internal Preferences Options { get; }
    internal IReadOnlyList<DesktopItem> Items { get; private set; } = [];
    internal event Action? Changed;
    internal string PersonalDesktop => roots[0];

    internal Catalog(string? testRoot)
    {
        directory = testRoot is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit Desktop") : Path.Combine(testRoot, ".orbit");
        roots = testRoot is null
            ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [testRoot];

        Options = Load();
        settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        settle.Tick += (_, _) => { settle.Stop(); Refresh(); };
        rescan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        rescan.Tick += (_, _) => Refresh();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes };
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Changed += OnChange;
                watcher.Renamed += OnChange;
                watcher.Error += (_, _) => QueueRefresh();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (IOException) { }
        }
        Refresh();
        rescan.Start();
    }

    private void OnChange(object sender, FileSystemEventArgs args) => QueueRefresh();

    private void QueueRefresh()
    {
        dispatcher.BeginInvoke(() => { settle.Stop(); settle.Start(); });
    }

    private void Refresh()
    {
        var previous = Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var items = new List<DesktopItem>();
        foreach (string root in roots)
        {
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(root))
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (IOException) { continue; }
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                        Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool folder = (attributes & FileAttributes.Directory) != 0;
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    bool app = !folder && extension is ".lnk" or ".url" or ".exe" or ".appref-ms";
                    int category = folder ? 1 : app ? 0 : extension is ".png" or ".jpg" or ".jpeg" or ".mp4" or ".wav" or ".mp3" or ".webm" or ".webp" or ".mov"
                        ? 3 : extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz" ? 4 : 2;
                    if (previous.TryGetValue(path, out var existing) && existing.Category == category &&
                        string.Equals(path, existing.Path, StringComparison.Ordinal))
                    {
                        items.Add(existing);
                        continue;
                    }
                    var name = app && extension != ".exe" ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
                    items.Add(new DesktopItem(path, name, folder ? "DIR" : app ? "APP" : (extension.Length > 1 ? extension[1..].ToUpperInvariant() : "FILE"),
                        category, app ? GetMark(path) : null));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        items.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        if (Items.SequenceEqual(items)) return;
        Items = items;
        Changed?.Invoke();
    }

    internal void Save()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        Options.TimingVersion = 1;
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Options, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    private Preferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(directory, "settings.json"))) ?? new();
            if (value.TimingVersion == 0 && value.FrameRate == 24) value.FrameRate = 60;
            value.Rings = Math.Clamp(value.Rings, 2, 8);
            value.Core = Math.Clamp(value.Core, 0, 2);
            value.FrameRate = Math.Clamp(value.FrameRate, 15, 60);
            value.Glow = Math.Clamp(value.Glow, 0, 1);
            value.Speed = Math.Clamp(value.Speed, 0, 2);
            value.Positions = new(value.Positions ?? [], StringComparer.OrdinalIgnoreCase);
            return value;
        }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    private static ImageSource? GetMark(string path)
    {
        if (SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            var original = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            var bitmap = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[32 * 32 * 4];
            bitmap.CopyPixels(pixels, 128, 0);
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                double luminance = 0.114 * pixels[offset] + 0.587 * pixels[offset + 1] + 0.299 * pixels[offset + 2];
                byte light = (byte)Math.Clamp(70 + 0.72 * luminance, 0, 255);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = light;
            }
            var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 128);
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }

    public void Dispose()
    {
        settle.Stop();
        rescan.Stop();
        foreach (var watcher in watchers) watcher.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal IntPtr Icon;
        internal int Index;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string name, uint attributes, out ShellFileInfo result, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
