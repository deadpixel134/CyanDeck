using DesktopDeck.Services;
using System.IO;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

var scratch = Path.Combine(Path.GetTempPath(), "desktopdeck-watch-harness-" + Guid.NewGuid().ToString("N"));
var first = Path.Combine(scratch, "first");
var second = Path.Combine(scratch, "second");
Directory.CreateDirectory(first);
Directory.CreateDirectory(second);
try
{
    using var desktop = new DesktopWatcherLifetime();
    FileSystemWatcher? original = null;
    FileSystemWatcher? successor = null;
    Check(desktop.Rebind([first], dir => original = new FileSystemWatcher(dir)), "initial watcher binds");
    Check(desktop.RootCount == 1 && desktop.Matches([first]) && desktop.Owns(original), "initial root is committed");
    Check(original!.EnableRaisingEvents, "new watcher activated after commit");

    Check(!desktop.Rebind([second], _ => null), "factory failure returns false");
    Check(desktop.Matches([first]) && desktop.Owns(original) && original.EnableRaisingEvents,
        "failed rebind retains previous active watcher");
    Check(desktop.Rebind([second], dir => successor = new FileSystemWatcher(dir)), "replacement watcher binds");
    Check(desktop.Matches([second]) && !desktop.Owns(original) && desktop.Owns(successor),
        "commit switches root identity and watcher ownership");

    using var portal = new FolderPortalWatcherLifetime();
    var posted = new List<Action>();
    Check(portal.Bind(first, callback => posted.Add(callback), (_, _) => { }, _ => { }),
        "portal watcher binds");
    Check(portal.CurrentPath == first, "portal watcher records current directory");
    Check(!portal.Bind(Path.Combine(scratch, "missing"), callback => posted.Add(callback), (_, _) => { }, _ => { }),
        "invalid new portal root does not replace watcher");
    Check(portal.CurrentPath == first, "portal watcher preserves old root on bind failure");
    Check(portal.Bind(second, callback => posted.Add(callback), (_, _) => { }, _ => { }),
        "portal watcher switches to another directory after failure");
    portal.Clear();
    Check(portal.CurrentPath is null, "portal watcher clear releases path");
    desktop.Dispose();
    Check(!desktop.Owns(successor), "disposed owner rejects old watchers");
    Console.WriteLine("WatcherLifetimeHarness: all assertions passed");
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch { }
}
