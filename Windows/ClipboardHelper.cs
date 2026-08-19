using System.Drawing;

namespace RemoteControl;

/// Puts a phone-sent image onto the real Windows clipboard - Server.HandleClientAsync's
/// dispatch loop runs on a thread-pool thread (MTA), but the Win32 clipboard/OLE APIs
/// System.Windows.Forms.Clipboard wraps require an STA thread to even call into, let alone
/// one already holding the clipboard open. Spinning up a dedicated throwaway STA thread per
/// image (rather than trying to marshal onto TrayApp's existing UI thread) keeps this
/// self-contained - no need to thread a UI-thread callback through Server/InputInjector for
/// something that happens rarely and isn't performance-sensitive the way input dispatch is.
static class ClipboardHelper
{
    public static void SetImage(byte[] jpegBytes)
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
