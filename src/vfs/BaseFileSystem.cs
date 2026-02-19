using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Uplink2.Vfs;

/// <summary>
/// VFS entry kind.
/// </summary>
public enum VfsEntryType
{
    /// <summary>
    /// Regular file entry.
    /// </summary>
    File,
    /// <summary>
    /// Directory entry.
    /// </summary>
    Dir,
}

/// <summary>
/// Metadata for a single VFS entry.
/// </summary>
public sealed class VfsEntryMeta
{
    /// <summary>
    /// Entry kind (file or directory).
    /// </summary>
    public VfsEntryType Type { get; }
    /// <summary>
    /// Content id in <see cref="BlobStore"/> for file entries.
    /// Empty for directories.
    /// </summary>
    public string ContentId { get; }
    /// <summary>
    /// UTF-8 byte length for file entries.
    /// Zero for directories.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Internal constructor for metadata creation.
    /// </summary>
    private VfsEntryMeta(VfsEntryType type, string contentId, long size)
    {
        Type = type;
        ContentId = contentId;
        Size = size;
    }

    /// <summary>
    /// Creates directory metadata.
    /// </summary>
    public static VfsEntryMeta CreateDir()
    {
        return new VfsEntryMeta(VfsEntryType.Dir, string.Empty, 0);
    }

    /// <summary>
    /// Creates file metadata.
    /// </summary>
    public static VfsEntryMeta CreateFile(string contentId, long size)
    {
        return new VfsEntryMeta(VfsEntryType.File, contentId, size);
    }
}

/// <summary>
/// Global content store with deduplication and reference counting.
/// </summary>
public sealed class BlobStore
{
    /// <summary>
    /// Maps content id to actual text payload.
    /// </summary>
    private readonly Dictionary<string, string> blobs = new(StringComparer.Ordinal);
    /// <summary>
    /// Mutable reference counts (primarily for overlay-managed content).
    /// </summary>
    private readonly Dictionary<string, int> refCount = new(StringComparer.Ordinal);
    /// <summary>
    /// Content ids that should never be garbage-collected by <see cref="Release"/>.
    /// </summary>
    private readonly HashSet<string> pinnedContentIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Stores content and increments ref count.
    /// </summary>
    public string Put(string content)
    {
        var contentId = ComputeContentId(content);
        blobs[contentId] = content;
        refCount[contentId] = refCount.GetValueOrDefault(contentId) + 1;
        return contentId;
    }

    /// <summary>
    /// Stores content as pinned (base content).
    /// Pinned content is not removed by <see cref="Release"/>.
    /// </summary>
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

    /// <summary>
    /// Increments ref count for an existing content id.
    /// </summary>
    public bool Retain(string contentId)
    {
        if (!blobs.ContainsKey(contentId))
        {
            return false;
        }

        refCount[contentId] = refCount.GetValueOrDefault(contentId) + 1;
        return true;
    }

    /// <summary>
    /// Decrements ref count; removes unpinned content when count reaches zero.
    /// </summary>
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

    /// <summary>
    /// Resolves content by id.
    /// </summary>
    public bool TryGet(string contentId, out string content)
    {
        return blobs.TryGetValue(contentId, out content!);
    }

    /// <summary>
    /// Returns current ref count for a content id.
    /// </summary>
    public int GetRefCount(string contentId)
    {
        return refCount.GetValueOrDefault(contentId);
    }

    /// <summary>
    /// Returns true when a content id is pinned.
    /// </summary>
    public bool IsPinned(string contentId)
    {
        return pinnedContentIds.Contains(contentId);
    }

    /// <summary>
    /// Computes deterministic content id from UTF-8 text bytes.
    /// </summary>
    private static string ComputeContentId(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Immutable/shared base filesystem model (no overlay behavior).
/// </summary>
public sealed class BaseFileSystem
{
    /// <summary>
    /// Backing blob store for file payloads.
    /// </summary>
    private readonly BlobStore blobStore;
    /// <summary>
    /// Full-path entry lookup table.
    /// </summary>
    private readonly Dictionary<string, VfsEntryMeta> baseEntries = new(StringComparer.Ordinal);
    /// <summary>
    /// Directory index: dir path -&gt; child names.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> baseDirIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes an empty base filesystem with root directory.
    /// </summary>
    public BaseFileSystem(BlobStore blobStore)
    {
        this.blobStore = blobStore;
        baseEntries["/"] = VfsEntryMeta.CreateDir();
        baseDirIndex["/"] = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Exposes base entries by full path.
    /// </summary>
    public IReadOnlyDictionary<string, VfsEntryMeta> BaseEntries => baseEntries;

    /// <summary>
    /// Exposes directory child index.
    /// </summary>
    public IReadOnlyDictionary<string, HashSet<string>> BaseDirIndex => baseDirIndex;

    /// <summary>
    /// Ensures directory exists (including missing parents) and returns normalized path.
    /// </summary>
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

    /// <summary>
    /// Adds or replaces a file entry and stores its content in the blob store.
    /// </summary>
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

    /// <summary>
    /// Resolves an entry from base filesystem by path.
    /// </summary>
    public bool TryResolveEntry(string path, out VfsEntryMeta entry)
    {
        return baseEntries.TryGetValue(NormalizePath("/", path), out entry!);
    }

    /// <summary>
    /// Lists direct child names of a directory in ordinal sorted order.
    /// </summary>
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

    /// <summary>
    /// Finds entries under a directory whose child name contains the pattern.
    /// </summary>
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

    /// <summary>
    /// Reads file text content from base filesystem through blob store.
    /// </summary>
    public bool TryReadFileText(string path, out string content)
    {
        content = string.Empty;
        if (!TryResolveEntry(path, out var entry) || entry.Type != VfsEntryType.File || string.IsNullOrEmpty(entry.ContentId))
        {
            return false;
        }

        return blobStore.TryGet(entry.ContentId, out content);
    }

    /// <summary>
    /// Normalizes a path against cwd (supports '.', '..', absolute/relative input).
    /// </summary>
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

    /// <summary>
    /// Joins a directory and child name into a full path.
    /// </summary>
    private static string JoinPath(string dirPath, string childName)
    {
        return dirPath == "/" ? "/" + childName : dirPath + "/" + childName;
    }

    /// <summary>
    /// Gets parent directory path for a full path.
    /// </summary>
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

    /// <summary>
    /// Gets last path segment from a full path.
    /// </summary>
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
