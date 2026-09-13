using ToneSnip.App.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Output;
using ToneSnip.Windows;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App.Output;

/// <summary>A history entry plus what the session still has in memory for it (the PNG of a recent snip).</summary>
public sealed class HistoryItem(HistoryEntry entry)
{
    public HistoryEntry Entry { get; set; } = entry;
    public CaptureResult? Result { get; set; }
    /// <summary>Whether the snip's file was missing when last probed. Cached and refreshed off the UI thread by
    /// <see cref="SnipHistory.ProbeAsync"/>, since File.Exists can stall on a network share.</summary>
    public bool FileMissing { get; internal set; }
    /// <summary>Whether deleting the file would reach the Recycle Bin. Cached with <see cref="FileMissing"/>; true until
    /// probed.</summary>
    public bool RecycleBinCovers { get; internal set; } = true;
}

/// <summary>Recent snips: history.json plus one thumbnail PNG per snip under thumbs/. Toasts and the flyout share the thumbnails.</summary>
public sealed class SnipHistory
{
    /// <summary>The long edge of the thumbnail PNG, sized for display in physical pixels on high-DPI monitors.</summary>
    public const int ThumbMaxEdge = 720;
    private readonly ILog _log;
    private readonly string _path = Path.Combine(AppPaths.Dir, "history.json");
    private readonly string _thumbDir = Path.Combine(AppPaths.Dir, "thumbs");
    private readonly Func<string> _saveFolder;
    /// <summary>Read at delete time, since the setting can change while the app runs.</summary>
    private readonly Func<bool> _recycle;
    /// <summary>Folders this process has written a snip into (such as the editor's "Save as…"), trusted by
    /// <see cref="MayDelete"/>. Deliberately not persisted, because history.json is untrusted input.</summary>
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private HistoryList _list;
    private readonly List<HistoryItem> _items = new();
    public IReadOnlyList<HistoryItem> Items => _items;
    public event Action? Changed;

    /// <param name="saveFolder">Read at delete time, since the save folder can change while the app runs.</param>
    /// <param name="deleteToRecycleBin"><c>SnipSettings.DeleteToRecycleBin</c>, read the same way; defaults to
    /// on.</param>
    public SnipHistory(ILog log, Func<string>? saveFolder = null, Func<bool>? deleteToRecycleBin = null)
    {
        _log = log;
        _saveFolder = saveFolder ?? (() => Path.Combine(AppPaths.Pictures, SnipSettings.DefaultSaveFolderName));
        _recycle = deleteToRecycleBin ?? (() => true);
        // Never fatal: an unreadable history.json must not break startup.
        try
        {
            (List<HistoryEntry>? saved, string? err) = JsonFile.Load<List<HistoryEntry>>(_path);
            if (err != null) log.Warn("history: " + err);
            _list = new HistoryList(saved);
        }
        catch (Exception e) { log.Warn("history: " + e.Message); _list = new HistoryList(); }
        foreach (HistoryEntry e in _list.Entries) _items.Add(new HistoryItem(e));
    }

    /// <summary>
    /// Checks each row's file (exists, Recycle Bin available) on the thread pool, applies the results on the calling
    /// (UI) thread, and raises <see cref="Changed"/> if any changed.
    /// </summary>
    public async Task ProbeAsync()
    {
        HistoryItem[] items = _items.ToArray();
        (bool Missing, bool Covers)[] found = await Task.Run(() => items.Select(i =>
        {
            string? path = i.Entry.Path;
            if (!PathGuard.IsSafeAbsolute(path)) return (path != null, false);
            try { return (!File.Exists(path), Shell.RecycleBinCovers(path)); }
            catch { return (true, false); }
        }).ToArray());
        bool moved = false;
        for (int n = 0; n < items.Length; n++)
        {
            if (items[n].FileMissing == found[n].Missing && items[n].RecycleBinCovers == found[n].Covers) continue;
            items[n].FileMissing = found[n].Missing;
            items[n].RecycleBinCovers = found[n].Covers;
            moved = true;
        }
        if (moved) Changed?.Invoke();
    }

    /// <summary>Deletes thumbnails no row names (<see cref="HistoryList.OrphanThumbs"/>), on the thread pool. Only files
    /// older than this process are touched, so a thumbnail written during the sweep is safe.</summary>
    public void SweepOrphanThumbs()
    {
        HistoryEntry[] entries = _list.Entries.ToArray();
        DateTime started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_thumbDir)) return;
                IEnumerable<string> old = Directory.EnumerateFiles(_thumbDir, "*.png").Where(f => File.GetLastWriteTimeUtc(f) < started);
                List<string> orphans = HistoryList.OrphanThumbs(old, entries);
                foreach (string f in orphans) { try { File.Delete(f); } catch (Exception e) { _log.Warn("thumbnail sweep: " + e.Message); } }
                if (orphans.Count > 0) _log.Info($"thumbnail sweep: {orphans.Count} orphaned thumbnail(s) deleted");
            }
            catch (Exception e) { _log.Warn("thumbnail sweep: " + e.Message); }
        });
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: replaces the list with synthetic rows, in memory only. The caller owns the files
    /// the entries name.</summary>
    internal void SeedForHarness(IEnumerable<HistoryEntry> entries)
    {
        _items.Clear();
        // _list is what Save() writes, so a seeded run has nothing to persist.
        _list = new HistoryList();
        // Probed synchronously so a screenshot does not race ProbeAsync.
        foreach (HistoryEntry e in entries) _items.Add(new HistoryItem(e) { FileMissing = e.Path != null && !File.Exists(e.Path) });
        Changed?.Invoke();
    }
#endif

    /// <summary>Records a finished snip and writes its thumbnail; returns the thumbnail path for the toast.</summary>
    public string Add(CaptureResult r)
    {
        Directory.CreateDirectory(_thumbDir);
        BgraImage img = r.Output ?? r.Image;
        string id = Guid.NewGuid().ToString("N");
        string thumb = Path.Combine(_thumbDir, id + ".png");
        try { File.WriteAllBytes(thumb, Bitmaps.EncodePng(Bitmaps.Thumbnail(img, ThumbMaxEdge))); } catch (Exception e) { _log.Warn("thumbnail: " + e.Message); }
        Wrote(r.SavedPath); Wrote(r.HdrPath);
        var entry = new HistoryEntry(id, r.SavedPath, r.TakenLocal.ToUniversalTime(), img.Width, img.Height, r.AnyHdr, thumb, r.HdrPath);
        foreach (HistoryEntry dropped in _list.Add(entry)) { DeleteOwn(dropped.Thumb, Png); _items.RemoveAll(i => i.Entry.Id == dropped.Id); }
        foreach (HistoryItem older in _items.Skip(2)) older.Result = null;   // only the newest few keep their PNG in memory
        _items.Insert(0, new HistoryItem(entry) { Result = r });
        Save();
        return thumb;
    }

    /// <summary>After the editor saved over a snip's own file: rewrites that entry's thumbnail and size from the edited image.</summary>
    public void Replace(CaptureResult r, BgraImage img)
    {
        // Match by path first: after a Save as… the result's own entry still names the original file.
        HistoryItem? item = (r.SavedPath == null ? null : _items.FirstOrDefault(i => string.Equals(i.Entry.Path, r.SavedPath, StringComparison.OrdinalIgnoreCase)))
            ?? _items.FirstOrDefault(i => i.Result == r);
        if (item == null) return;
        Directory.CreateDirectory(_thumbDir);
        // A fresh filename, not an overwrite: images are cached by URI, so a rewritten path would show the old bitmap.
        string thumb = Path.Combine(_thumbDir, Guid.NewGuid().ToString("N") + ".png");
        try { File.WriteAllBytes(thumb, Bitmaps.EncodePng(Bitmaps.Thumbnail(img, ThumbMaxEdge))); }
        catch (Exception e) { _log.Warn("thumbnail: " + e.Message); thumb = item.Entry.Thumb; }
        if (!string.Equals(thumb, item.Entry.Thumb, StringComparison.OrdinalIgnoreCase)) DeleteOwn(item.Entry.Thumb, Png);
        item.Result = null;   // the retained result holds pre-edit pixels; Load decodes the new file instead
        // After a Save as… r.HdrPath may name the original snip's sidecar; adopt it only if it belongs to this path.
        bool ownsSidecar = OwnsSidecar(r.SavedPath, r.HdrPath);
        string? hdrPath = ownsSidecar ? r.HdrPath : item.Entry.HdrPath;
        // Record only folders this call actually wrote into.
        Wrote(r.SavedPath);
        if (ownsSidecar) Wrote(r.HdrPath);
        item.Entry = item.Entry with { Path = r.SavedPath, Width = img.Width, Height = img.Height, Thumb = thumb, HdrPath = hdrPath };
        _list.Update(item.Entry);   // in place: Add would move a re-saved older snip to the front of the list
        Save();
    }

    private static bool OwnsSidecar(string? sdrPath, string? hdrPath) => HdrOutput.OwnsSidecar(sdrPath, hdrPath);

    /// <summary>Records a second file written from the same snip (the editor's Save as…). Keeps no in-memory result.</summary>
    public void AddSavedCopy(CaptureResult r, string path, BgraImage img)
    {
        // Save as… over a file that already has a row (its own, or another snip's) updates that row instead of adding a twin.
        if (_items.Any(i => string.Equals(i.Entry.Path, path, StringComparison.OrdinalIgnoreCase))) { Replace(r, img); return; }
        Directory.CreateDirectory(_thumbDir);
        string id = Guid.NewGuid().ToString("N");
        string thumb = Path.Combine(_thumbDir, id + ".png");
        try { File.WriteAllBytes(thumb, Bitmaps.EncodePng(Bitmaps.Thumbnail(img, ThumbMaxEdge))); } catch (Exception e) { _log.Warn("thumbnail: " + e.Message); }
        Wrote(path);
        var entry = new HistoryEntry(id, path, r.TakenLocal.ToUniversalTime(), img.Width, img.Height, r.AnyHdr, thumb);
        foreach (HistoryEntry dropped in _list.Add(entry)) { DeleteOwn(dropped.Thumb, Png); _items.RemoveAll(i => i.Entry.Id == dropped.Id); }
        _items.Insert(0, new HistoryItem(entry));
        Save();
    }

    public void Remove(HistoryItem item, bool deleteFile)
    {
        // The snip and its sidecar may go to the Recycle Bin; the thumbnail is the app's cache and is deleted outright.
        if (deleteFile) { DeleteOwn(item.Entry.Path, SnipFiles, recycle: true); DeleteOwn(item.Entry.HdrPath, SnipFiles, recycle: true); }
        DeleteOwn(item.Entry.Thumb, Png);
        _list.Remove(item.Entry.Id);
        _items.Remove(item);
        Save();
    }

    /// <summary>A result the viewer can open for this entry: the retained one while it exists, else one decoded from the file.</summary>
    public CaptureResult? ToResult(HistoryItem item)
    {
        if (item.Result != null) return item.Result;
        BgraImage? img = Load(item);
        if (img == null) return null;
        // history.json is untrusted and the editor saves over these paths: Load has checked the snip path, and the
        // sidecar is kept only if it is the one HdrOutput derives for it.
        string? hdr = OwnsSidecar(item.Entry.Path, item.Entry.HdrPath) && PathGuard.IsSafeAbsolute(item.Entry.HdrPath) ? item.Entry.HdrPath : null;
        return new CaptureResult { Image = img, Region = new Core.Geometry.IntRect(0, 0, img.Width, img.Height), AnyHdr = item.Entry.Hdr, Crops = new(), SavedPath = item.Entry.Path, TakenLocal = item.Entry.TakenUtc.ToLocalTime(), HdrPath = hdr };
    }

    /// <summary>The snip as an image: from memory when recent, otherwise decoded from its file. Null when neither exists.</summary>
    public BgraImage? Load(HistoryItem item)
    {
        if (item.Result != null) return item.Result.Peek();
        if (!PathGuard.IsSafeAbsolute(item.Entry.Path) || !File.Exists(item.Entry.Path)) return null;
        try { return Bitmaps.Decode(File.ReadAllBytes(item.Entry.Path)); }
        catch (Exception e) { _log.Warn("history load: " + e.Message); return null; }
    }

    /// <summary>The image and PNG bytes for the flyout's Copy, reusing existing PNG bytes where possible to avoid an
    /// encode.</summary>
    public (BgraImage Image, byte[] Png)? LoadForCopy(HistoryItem item)
    {
        if (item.Result is { } r)
        {
            byte[]? held = r.CompactedPng;
            BgraImage img = r.Peek();
            return (img, held ?? Bitmaps.EncodePng(img));
        }
        if (!PathGuard.IsSafeAbsolute(item.Entry.Path) || !File.Exists(item.Entry.Path)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(item.Entry.Path);
            BgraImage img = Bitmaps.Decode(bytes);
            return (img, PathGuard.HasExtension(item.Entry.Path, ".png") ? bytes : Bitmaps.EncodePng(img));
        }
        catch (Exception e) { _log.Warn("history copy: " + e.Message); return null; }
    }

    private void Save()
    {
        try { JsonFile.Save(_path, _list.Entries.ToList()); } catch (Exception e) { _log.Warn("history save: " + e.Message); }
        Changed?.Invoke();
    }

    /// <summary>The extensions a snip's own files carry. Nothing else is ever deleted, whatever history.json says.</summary>
    private static readonly string[] SnipFiles = { ".png", ".jpg", ".jpeg", ".jxr" };
    private static readonly string[] Png = { ".png" };

    /// <summary>Notes a folder the app itself has just written a snip into (see <see cref="_written"/>).</summary>
    private void Wrote(string? path)
    {
        if (PathGuard.IsSafeAbsolute(path) && Path.GetDirectoryName(path) is { Length: > 0 } dir) _written.Add(dir);
    }

    /// <summary>
    /// The delete rule. history.json is editable, so a row's path is untrusted. A file is deleted only when:
    /// <list type="number">
    /// <item>it passes <see cref="PathGuard.IsSafeAbsolute"/>;</item>
    /// <item>its extension is allowed (<c>.png/.jpg/.jpeg/.jxr</c> for snips and sidecars, <c>.png</c> for
    /// thumbnails);</item>
    /// <item>it is under <see cref="AppPaths.Dir"/>, the current save folder, or a folder this process wrote a snip
    /// into;</item>
    /// <item>no link lies between the file and that root.</item>
    /// </list>
    /// A refused path is logged and left alone; the row is still removed. So a file saved outside the save folder in an
    /// earlier session is kept.
    /// </summary>
    private bool MayDelete(string? path, string[] extensions)
    {
        if (path == null) return false;
        if (!PathGuard.HasExtension(path, extensions)) { _log.Warn("history delete refused (not a snip file): " + PathGuard.Describe(path)); return false; }
        string[] roots = new[] { AppPaths.Dir, _saveFolder() }.Concat(_written).ToArray();
        if (!PathGuard.IsUnder(path, roots)) { _log.Warn("history delete refused (outside this app's folders): " + PathGuard.Describe(path)); return false; }
        if (HasReparsePoint(path, roots)) { _log.Warn("history delete refused (reparse point on the way): " + PathGuard.Describe(path)); return false; }
        return true;
    }

    /// <summary>Walks from the file up to its root, looking for a symbolic link or junction, which
    /// <see cref="Path.GetFullPath(string)"/> does not resolve. Only links count, because OneDrive files are cloud-file
    /// reparse points. Unreadable counts as a link.</summary>
    private static bool HasReparsePoint(string path, string[] roots)
    {
        try
        {
            for (string? p = path; p != null; p = Path.GetDirectoryName(p))
            {
                if (roots.Any(r => string.Equals(Path.TrimEndingDirectorySeparator(p), Path.TrimEndingDirectorySeparator(r), StringComparison.OrdinalIgnoreCase))) return false;
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                if (!File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) continue;
                FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                if (info.LinkTarget != null) return true;
            }
            return false;
        }
        catch { return true; }
    }

    /// <summary>Deletes one of the app's own files if <see cref="MayDelete"/> allows it, logging any failure.
    /// <para>With <paramref name="recycle"/> (the user's snip and sidecar) and <c>SnipSettings.DeleteToRecycleBin</c>
    /// on, the file goes to the Recycle Bin; if the shell refuses, the file is left rather than permanently
    /// deleted.</para></summary>
    private void DeleteOwn(string? path, string[] extensions, bool recycle = false)
    {
        if (!MayDelete(path, extensions)) return;
        // A failed delete is logged, never swallowed.
        try
        {
            if (!File.Exists(path)) return;
            if (recycle && _recycle())
            {
                if (!Shell.Recycle(path, _log)) _log.Warn($"history delete '{path}': the Recycle Bin would not take it, so the file was left alone");
                return;
            }
            File.Delete(path!);
        }
        catch (Exception e) { _log.Warn($"history delete '{path}': {e.Message}"); }
    }
}
