using System.Text;

namespace Arcline.SteamGuard.Storage;

/// <summary>Crash-safe writes (temp file + atomic rename) with owner-only permissions on Unix.</summary>
public static class SecureFile
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        EnsureDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerFile;
            using (var fs = new FileStream(tmp, options))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(contents);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, OwnerDir);
    }

    /// <summary>Best-effort overwrite before delete (helps on HDDs; SSD wear-levelling makes it advisory only).</summary>
    public static void Shred(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Write(new byte[len]);
                fs.Flush(true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        File.Delete(path);
    }
}
