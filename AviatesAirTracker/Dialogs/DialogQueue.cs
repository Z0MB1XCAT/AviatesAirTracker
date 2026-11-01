using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// WinUI allows only ONE ContentDialog open per window at a time -- a second ShowAsync() while
// another is showing throws COMException ("Only a single ContentDialog can be open at any
// time"). Most dialogs here are user-initiated and modal, so they can't overlap, but the shell
// raises three on its own (landing report, held-for-review, event reminder) that can land on
// top of each other or on top of whatever dialog the pilot already has open. Every dialog goes
// through ShowQueuedAsync so the later one simply waits its turn instead of crashing the app.
// No dialog in this app opens another dialog while it is still showing, which is what would
// deadlock this gate.
// ============================================================

public static class DialogQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowQueuedAsync(this ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
