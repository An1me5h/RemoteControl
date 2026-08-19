using System.Drawing;

namespace RemoteControl;

/// Handles a phone-sent image: saves it to disk and puts it on the real Windows clipboard.
/// Disk is what makes "send several images at once" actually work - the clipboard can only
/// ever hold ONE image, so sending N images the same way a single one is sent would just
/// have each new one silently replace the last with no trace of the others. Every image
/// gets a real file; the clipboard ends up with whichever was sent most recently, as a
/// convenience for immediate pasting, not the primary destination.
static class ClipboardHelper
{
    /// %UserProfile%\Pictures\RemoteControl - not next to the exe, for the same reason
    /// DeviceTrustStore uses %AppData% instead of the exe's own folder: that can be
    /// read-only (e.g. under Program Files), and Pictures is exactly where a user would
    /// look for images that landed on their PC from somewhere else.
    private static readonly string SaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RemoteControl");

    /// Returns the path the image was saved to (the clipboard write is best-effort and
    /// already swallows its own failures - see SetImage - so it's not reflected here).
    public static string SaveAndSetClipboard(byte[] jpegBytes)
    {
        string path = SaveToDisk(jpegBytes);
        SetImage(jpegBytes);
        return path;
    }

    private static string SaveToDisk(byte[] jpegBytes)
    {
        Directory.CreateDirectory(SaveDir);
        // A short GUID fragment, not just the timestamp - several images from one "send
        // multiple" batch can arrive within the same second, and the timestamp alone would
        // collide.
        string uniquePart = Guid.NewGuid().ToString("N")[..8];
        string fileName = $"remote-{DateTime.Now:yyyyMMdd-HHmmss}-{uniquePart}.jpg";
        string path = Path.Combine(SaveDir, fileName);
        File.WriteAllBytes(path, jpegBytes);
        return path;
    }

    /// Server.HandleClientAsync's dispatch loop runs on a thread-pool thread (MTA), but the
    /// Win32 clipboard/OLE APIs System.Windows.Forms.Clipboard wraps require an STA thread
    /// to even call into, let alone one already holding the clipboard open. Spinning up a
    /// dedicated throwaway STA thread per image (rather than trying to marshal onto
    /// TrayApp's existing UI thread) keeps this self-contained - no need to thread a
    /// UI-thread callback through Server/InputInjector for something that happens rarely
    /// and isn't performance-sensitive the way input dispatch is.
    private static void SetImage(byte[] jpegBytes)
    {
        using var ms = new MemoryStream(jpegBytes);
        using var image = Image.FromStream(ms);
        // Clipboard.SetImage needs its own copy it can own after this method returns and
        // `image`/`ms` get disposed - a plain reference into a stream-backed Image would be
        // invalid by the time anything actually pastes it.
        using var owned = new Bitmap(image);

        var thread = new Thread(() =>
        {
            try { System.Windows.Forms.Clipboard.SetImage(owned); }
            catch (Exception) { /* clipboard busy/unavailable - nothing to recover, just drop it */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
