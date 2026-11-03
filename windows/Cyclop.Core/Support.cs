namespace Cyclop;

/// <summary>
/// <c>%APPDATA%\Cyclop</c> — where everything Cyclop keeps of its own lives,
/// the Windows counterpart of <c>~/Library/Application Support/Cyclop</c>.
/// </summary>
public static class Support
{
    /// <summary>The folder itself, created on first use.</summary>
    public static string Folder { get; } = Create(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cyclop"));

    /// <summary>A file inside it, with the folder guaranteed to exist.</summary>
    public static string File(string name) => Path.Combine(Folder, name);

    /// <summary>A subfolder inside it, created the same way.</summary>
    public static string Directory(string name) => Create(Path.Combine(Folder, name));

    static string Create(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); } catch { }
        return path;
    }

    /// <summary>
    /// Written through a temporary file and moved into place, so a crash or a
    /// full disk mid-write leaves the previous version instead of half a file —
    /// the same promise <c>.atomic</c> makes on the Mac.
    /// </summary>
    public static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        System.IO.File.Move(temp, path, overwrite: true);
    }

    static readonly object logGate = new();

    /// <summary>
    /// Also appended to <c>log.txt</c> next to the settings: the app has no
    /// console, and a log the user can send is the one way to see what Windows
    /// reported on a PC the code was never run on. Kept under 256 KB by
    /// starting over.
    /// </summary>
    public static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine("Cyclop: " + message);
        try
        {
            lock (logGate)
            {
                var path = File("log.txt");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 256 * 1024) info.Delete();
                System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>
/// The smallest observable there is: one event, raised whenever anything
/// changed. The panes are rebuilt from the store on it, and the lists here are
/// short enough — forty copies, sixty cards — that re-reading beats tracking.
/// </summary>
public abstract class Observable
{
    public event Action? Changed;
    protected void Notify() => Changed?.Invoke();
}
