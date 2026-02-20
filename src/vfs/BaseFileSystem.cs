using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Uplink2.Vfs;

/// <summary>VFS entry kind.</summary>
public enum VfsEntryType
{
    File,
    Dir,
}

/// <summary>Metadata for a single VFS entry.</summary>
public sealed class VfsEntryMeta
{
    /// <summary>Entry kind (file or directory).</summary>
    public VfsEntryType Type { get; }

    /// <summary>Blob content id for file entries (empty for directories).</summary>
    public string ContentId { get; }

    /// <summary>UTF-8 file size in bytes (0 for directories).</summary>
    public long Size { get; }

    private VfsEntryMeta(VfsEntryType type, string contentId, long size)
    {
        Type = type;
        ContentId = contentId;
        Size = size;
    }

    /// <summary>Creates directory metadata.</summary>
    public static VfsEntryMeta CreateDir()
    {
        return new VfsEntryMeta(VfsEntryType.Dir, string.Empty, 0);
    }

    /// <summary>Creates file metadata.</summary>
    public static VfsEntryMeta CreateFile(string contentId, long size)
    {
        return new VfsEntryMeta(VfsEntryType.File, contentId, size);
    }
}

/// <summary>Deduplicated text blob store with ref counting and pin support.</summary>
public sealed class BlobStore
{
    // Backing stores for content, ref counts, and non-collectable (base) blobs.
    private readonly Dictionary<string, string> blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> refCount = new(StringComparer.Ordinal);
    private readonly HashSet<string> pinnedContentIds = new(StringComparer.Ordinal);

    /// <summary>Stores content and increments reference count.</summary>
    public string Put(string content)
    {
        var contentId = ComputeContentId(content);
        blobs[contentId] = content;
        refCount[contentId] = refCount.GetValueOrDefault(contentId) + 1;
        return contentId;
    }

    /// <summary>Stores content as pinned so it cannot be released.</summary>
    public string PutPinned(string content)
    {
        var contentId = ComputeContentId(content);
        blobs[contentId] = content;
        pinnedContentIds.Add(contentId);

        if (!refCount.ContainsKey(contentId))
        {
            refCount[contentId] = 0;
        }

        return contentId;
    }

    /// <summary>Increments reference count for an existing content id.</summary>
    public bool Retain(string contentId)
    {
        if (!blobs.ContainsKey(contentId))
        {
            return false;
        }

        refCount[contentId] = refCount.GetValueOrDefault(contentId) + 1;
        return true;
    }

    /// <summary>Decrements reference count and collects unpinned content at zero.</summary>
    public bool Release(string contentId)
    {
        if (!blobs.ContainsKey(contentId))
        {
            return false;
        }

        if (pinnedContentIds.Contains(contentId))
        {
            return true;
        }

        var next = refCount.GetValueOrDefault(contentId) - 1;
        refCount[contentId] = next;

        if (next <= 0)
        {
            refCount.Remove(contentId);
            blobs.Remove(contentId);
        }

        return true;
    }

    /// <summary>Gets stored content by id.</summary>
    public bool TryGet(string contentId, out string content)
    {
        return blobs.TryGetValue(contentId, out content!);
    }

    /// <summary>Returns current reference count for an id.</summary>
    public int GetRefCount(string contentId)
    {
        return refCount.GetValueOrDefault(contentId);
    }

    /// <summary>Returns true if an id is pinned.</summary>
    public bool IsPinned(string contentId)
    {
        return pinnedContentIds.Contains(contentId);
    }

    private static string ComputeContentId(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Shared immutable base filesystem image.</summary>
public sealed class BaseFileSystem
{
    private readonly BlobStore blobStore;
    private readonly Dictionary<string, VfsEntryMeta> baseEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> baseDirIndex = new(StringComparer.Ordinal);

    /// <summary>Creates an empty base filesystem with root directory.</summary>
    public BaseFileSystem(BlobStore blobStore)
    {
        this.blobStore = blobStore;
        baseEntries["/"] = VfsEntryMeta.CreateDir();
        baseDirIndex["/"] = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Base entries by full path.</summary>
    public IReadOnlyDictionary<string, VfsEntryMeta> BaseEntries => baseEntries;

    /// <summary>Directory index (dir path -&gt; child names).</summary>
    public IReadOnlyDictionary<string, HashSet<string>> BaseDirIndex => baseDirIndex;

    /// <summary>Ensures a directory exists and returns normalized path.</summary>
    public string AddDirectory(string path)
    {
        var normalized = NormalizePath("/", path);
        if (normalized == "/")
        {
            return normalized;
        }

        var parent = GetParentPath(normalized);
        if (!baseEntries.TryGetValue(parent, out var parentMeta) || parentMeta.Type != VfsEntryType.Dir)
        {
            AddDirectory(parent);
        }

        baseEntries[normalized] = VfsEntryMeta.CreateDir();
        if (!baseDirIndex.ContainsKey(normalized))
        {
            baseDirIndex[normalized] = new HashSet<string>(StringComparer.Ordinal);
        }

        baseDirIndex[parent].Add(GetName(normalized));
        return normalized;
    }

    /// <summary>Adds/replaces a base file and stores its payload in blob store.</summary>
    public string AddFile(string path, string content, bool pinContent = true)
    {
        var normalized = NormalizePath("/", path);
        var parent = GetParentPath(normalized);
        AddDirectory(parent);

        var contentId = pinContent ? blobStore.PutPinned(content) : blobStore.Put(content);
        var size = Encoding.UTF8.GetByteCount(content);
        baseEntries[normalized] = VfsEntryMeta.CreateFile(contentId, size);
        baseDirIndex[parent].Add(GetName(normalized));
        return normalized;
    }

    /// <summary>Resolves an entry from base filesystem.</summary>
    public bool TryResolveEntry(string path, out VfsEntryMeta entry)
    {
        return baseEntries.TryGetValue(NormalizePath("/", path), out entry!);
    }

    /// <summary>Lists direct children of a directory in ordinal order.</summary>
    public IReadOnlyCollection<string> ListChildren(string dirPath)
    {
        var normalized = NormalizePath("/", dirPath);
        if (!baseEntries.TryGetValue(normalized, out var entry) || entry.Type != VfsEntryType.Dir)
        {
            return Array.Empty<string>();
        }

        if (!baseDirIndex.TryGetValue(normalized, out var children))
        {
            return Array.Empty<string>();
        }

        return children.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Finds paths whose child names contain the pattern.</summary>
    public IReadOnlyList<string> Find(string startDirPath, string pattern)
    {
        var normalized = NormalizePath("/", startDirPath);
        if (!baseEntries.TryGetValue(normalized, out var start) || start.Type != VfsEntryType.Dir)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(normalized);

        while (queue.Count > 0)
        {
            var currentDir = queue.Dequeue();
            foreach (var childName in ListChildren(currentDir))
            {
                var childPath = JoinPath(currentDir, childName);
                if (childName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(childPath);
                }

                if (baseEntries.TryGetValue(childPath, out var childEntry) && childEntry.Type == VfsEntryType.Dir)
                {
                    queue.Enqueue(childPath);
                }
            }
        }

        return results;
    }

    /// <summary>Reads text file content from base filesystem.</summary>
    public bool TryReadFileText(string path, out string content)
    {
        content = string.Empty;
        if (!TryResolveEntry(path, out var entry) || entry.Type != VfsEntryType.File || string.IsNullOrEmpty(entry.ContentId))
        {
            return false;
        }

        return blobStore.TryGet(entry.ContentId, out content);
    }

    /// <summary>Normalizes path with cwd (supports '.', '..', absolute/relative).</summary>
    public static string NormalizePath(string cwd, string inputPath)
    {
        var source = string.IsNullOrWhiteSpace(inputPath) ? "." : inputPath.Trim();
        var current = string.IsNullOrWhiteSpace(cwd) ? "/" : cwd.Trim();
        if (!current.StartsWith('/'))
        {
            current = "/" + current;
        }

        var combined = source.StartsWith('/')
            ? source
            : $"{current.TrimEnd('/')}/{source}";

        var segments = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                continue;
            }

            stack.Add(segment);
        }

        return stack.Count == 0 ? "/" : "/" + string.Join("/", stack);
    }

    private static string JoinPath(string dirPath, string childName)
    {
        return dirPath == "/" ? "/" + childName : dirPath + "/" + childName;
    }

    private static string GetParentPath(string path)
    {
        if (path == "/")
        {
            return "/";
        }

        var idx = path.LastIndexOf('/');
        if (idx <= 0)
        {
            return "/";
        }

        return path[..idx];
    }

    private static string GetName(string path)
    {
        if (path == "/")
        {
            return "/";
        }

        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }
}

/// <summary>Per-directory delta state for overlay merge.</summary>
public sealed class DirDelta
{
    /// <summary>Child names added compared to base.</summary>
    public HashSet<string> Added { get; } = new(StringComparer.Ordinal);

    /// <summary>Child names removed compared to base.</summary>
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

    /// <summary>Returns true when delta has no effect.</summary>
    public bool IsNeutral()
    {
        return Added.Count == 0 && Removed.Count == 0;
    }
}

/// <summary>Server-local overlay filesystem merged over base image.</summary>
public sealed class OverlayFileSystem
{
    private readonly BaseFileSystem baseFileSystem;
    private readonly BlobStore blobStore;

    // Overlay merge state.
    private readonly Dictionary<string, VfsEntryMeta> overlayEntries = new(StringComparer.Ordinal);
    private readonly HashSet<string> tombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirDelta> overlayDir = new(StringComparer.Ordinal);

    /// <summary>Creates an empty overlay for a server node.</summary>
    public OverlayFileSystem(BaseFileSystem baseFileSystem, BlobStore blobStore)
    {
        this.baseFileSystem = baseFileSystem;
        this.blobStore = blobStore;
    }

    /// <summary>Overlay entries by full path.</summary>
    public IReadOnlyDictionary<string, VfsEntryMeta> OverlayEntries => overlayEntries;

    /// <summary>Tombstoned paths hidden from merged view.</summary>
    public IReadOnlyCollection<string> Tombstones => tombstones;

    /// <summary>Per-directory added/removed deltas.</summary>
    public IReadOnlyDictionary<string, DirDelta> OverlayDir => overlayDir;

    /// <summary>Resolves merged entry with tombstone -&gt; overlay -&gt; base priority.</summary>
    public bool TryResolveEntry(string path, out VfsEntryMeta entry)
    {
        var normalized = BaseFileSystem.NormalizePath("/", path);
        if (tombstones.Contains(normalized))
        {
            entry = VfsEntryMeta.CreateDir();
            return false;
        }

        if (overlayEntries.TryGetValue(normalized, out var overlayEntry))
        {
            entry = overlayEntry;
            return true;
        }

        return baseFileSystem.TryResolveEntry(normalized, out entry!);
    }

    /// <summary>Lists merged children for a directory.</summary>
    public IReadOnlyCollection<string> ListChildren(string dirPath)
    {
        var normalized = BaseFileSystem.NormalizePath("/", dirPath);
        var names = new HashSet<string>(baseFileSystem.ListChildren(normalized), StringComparer.Ordinal);

        if (overlayDir.TryGetValue(normalized, out var delta))
        {
            names.ExceptWith(delta.Removed);
            names.UnionWith(delta.Added);
        }

        var filtered = new List<string>();
        foreach (var name in names)
        {
            var fullPath = JoinPath(normalized, name);
            if (TryResolveEntry(fullPath, out _))
            {
                filtered.Add(name);
            }
        }

        filtered.Sort(StringComparer.Ordinal);
        return filtered;
    }

    /// <summary>Finds merged entries by child-name substring.</summary>
    public IReadOnlyList<string> Find(string startDirPath, string pattern)
    {
        var normalized = BaseFileSystem.NormalizePath("/", startDirPath);
        if (!TryResolveEntry(normalized, out var startEntry) || startEntry.Type != VfsEntryType.Dir)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(normalized);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var childName in ListChildren(current))
            {
                var childPath = JoinPath(current, childName);
                if (childName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(childPath);
                }

                if (TryResolveEntry(childPath, out var entry) && entry.Type == VfsEntryType.Dir)
                {
                    queue.Enqueue(childPath);
                }
            }
        }

        return results;
    }

    /// <summary>Creates a directory entry in overlay space.</summary>
    public string AddDirectory(string path, string cwd = "/")
    {
        var normalized = BaseFileSystem.NormalizePath(cwd, path);
        if (normalized == "/")
        {
            return normalized;
        }

        var parent = GetParentPath(normalized);
        if (!TryResolveEntry(parent, out var parentEntry) || parentEntry.Type != VfsEntryType.Dir)
        {
            throw new InvalidOperationException($"Parent directory does not exist: {parent}");
        }

        tombstones.Remove(normalized);
        overlayEntries[normalized] = VfsEntryMeta.CreateDir();
        ApplyAddChild(parent, GetName(normalized));
        return normalized;
    }

    /// <summary>Writes text file content into overlay space.</summary>
    public string WriteFile(string path, string content, string cwd = "/")
    {
        var normalized = BaseFileSystem.NormalizePath(cwd, path);
        var parent = GetParentPath(normalized);
        if (!TryResolveEntry(parent, out var parentEntry) || parentEntry.Type != VfsEntryType.Dir)
        {
            throw new InvalidOperationException($"Parent directory does not exist: {parent}");
        }

        tombstones.Remove(normalized);

        if (overlayEntries.TryGetValue(normalized, out var existingEntry) &&
            existingEntry.Type == VfsEntryType.File &&
            !string.IsNullOrEmpty(existingEntry.ContentId))
        {
            blobStore.Release(existingEntry.ContentId);
        }

        var contentId = blobStore.Put(content);
        var size = Encoding.UTF8.GetByteCount(content);
        overlayEntries[normalized] = VfsEntryMeta.CreateFile(contentId, size);
        ApplyAddChild(parent, GetName(normalized));
        return normalized;
    }

    /// <summary>Reads merged file content by path.</summary>
    public bool TryReadFileText(string path, out string content)
    {
        content = string.Empty;
        if (!TryResolveEntry(path, out var entry) || entry.Type != VfsEntryType.File || string.IsNullOrEmpty(entry.ContentId))
        {
            return false;
        }

        return blobStore.TryGet(entry.ContentId, out content);
    }

    // DirDelta rules (from design doc) to keep delta neutral when possible.
    private void ApplyAddChild(string dirPath, string childName)
    {
        var delta = GetOrCreateDelta(dirPath);
        delta.Removed.Remove(childName);

        if (BaseHasChild(dirPath, childName))
        {
            delta.Added.Remove(childName);
        }
        else
        {
            delta.Added.Add(childName);
        }

        CompactDelta(dirPath, delta);
    }

    // DirDelta rules for removals with base-vs-overlay awareness.
    private void ApplyRemoveChild(string dirPath, string childName)
    {
        var delta = GetOrCreateDelta(dirPath);

        if (BaseHasChild(dirPath, childName))
        {
            delta.Removed.Add(childName);
            delta.Added.Remove(childName);
        }
        else
        {
            delta.Added.Remove(childName);
            delta.Removed.Remove(childName);
        }

        CompactDelta(dirPath, delta);
    }

    private bool BaseHasChild(string dirPath, string childName)
    {
        if (!baseFileSystem.BaseDirIndex.TryGetValue(dirPath, out var names))
        {
            return false;
        }

        return names.Contains(childName);
    }

    private DirDelta GetOrCreateDelta(string dirPath)
    {
        if (!overlayDir.TryGetValue(dirPath, out var delta))
        {
            delta = new DirDelta();
            overlayDir[dirPath] = delta;
        }

        return delta;
    }

    private void CompactDelta(string dirPath, DirDelta delta)
    {
        if (delta.IsNeutral())
        {
            overlayDir.Remove(dirPath);
        }
    }

    private static string JoinPath(string dirPath, string childName)
    {
        return dirPath == "/" ? "/" + childName : dirPath + "/" + childName;
    }

    private static string GetParentPath(string path)
    {
        if (path == "/")
        {
            return "/";
        }

        var idx = path.LastIndexOf('/');
        if (idx <= 0)
        {
            return "/";
        }

        return path[..idx];
    }

    private static string GetName(string path)
    {
        if (path == "/")
        {
            return "/";
        }

        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }
}
