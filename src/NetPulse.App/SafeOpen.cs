using System;
using System.Diagnostics;
using System.IO;
using NetPulse.Core;

namespace NetPulse.App;

/// <summary>
/// The only ways NetPulse hands something to Windows to open: a web page (http or https only, in the browser), a folder,
/// or a file selected in Explorer. Explorer is started by its full path, never by name.
/// </summary>
public static class SafeOpen
{
    public static void WebPage(string url)
    {
        if (!SystemTools.IsWebLink(url)) throw new InvalidOperationException("Only web addresses (https://...) are opened.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void Folder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"The folder {path} does not exist.");
        var psi = new ProcessStartInfo(SystemTools.Explorer) { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    public static void SelectFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The file is no longer there.", path);
        if (!SystemTools.IsSafeCommandLinePath(path)) { Folder(Path.GetDirectoryName(path)!); return; }
        Process.Start(new ProcessStartInfo(SystemTools.Explorer, $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
