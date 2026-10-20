using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetPulse.App;

/// <summary>
/// Game icons: the icon of the game's own program (or its launcher), taken from the file on this PC the first time
/// NetPulse sees it running, as Explorer would show it, and kept as a small PNG so it shows later too. Until then
/// a game has its initials.
/// </summary>
public static class GameIcons
{
    private static readonly Dictionary<string, string> Initials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lol"] = "LoL", ["fortnite"] = "FN", ["apex"] = "AL", ["cs2"] = "CS2", ["wow"] = "WoW", ["marvelrivals"] = "MR", ["poe"] = "PoE"
    };

    public static string MonogramOf(string id, string displayName)
    {
        if (Initials.TryGetValue(id, out var known)) return known;
        var words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : string.Concat(Array.ConvertAll(words.Length > 3 ? words[..3] : words, w => char.ToUpperInvariant(w[0]).ToString()));
    }

    /// <summary>The icon's file: ids such as "win:1234" become "win-1234.png" (a colon would name a hidden stream, not a file).</summary>
    public static string FileOf(string dataDirectory, string id)
    {
        var safe = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(dataDirectory, "game-icons", (safe.Length == 0 ? "game" : safe) + ".png");
    }

    public static ImageSource? Load(string dataDirectory, string id)
    {
        string path = FileOf(dataDirectory, id);
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Takes the icon of <paramref name="exePath"/> and keeps it; null when the file has none or cannot be read.</summary>
    public static Task<ImageSource?> CaptureAsync(string dataDirectory, string id, string exePath) => Task.Run<ImageSource?>(() =>
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            string path = FileOf(dataDirectory, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    });
}
