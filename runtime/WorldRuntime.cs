using Godot;
using Uplink2.Vfs;

namespace Uplink2.Runtime;

/// <summary>
/// Global runtime root for world-level state.
/// Register this class as an Autoload to keep one instance across scenes.
/// </summary>
public partial class WorldRuntime : Node
{
    /// <summary>
    /// Global singleton-like access point (when used as Autoload).
    /// </summary>
    public static WorldRuntime Instance { get; private set; }

    /// <summary>
    /// Shared blob store for file contents.
    /// </summary>
    public BlobStore BlobStore { get; private set; }

    /// <summary>
    /// Shared immutable base filesystem image.
    /// </summary>
    public BaseFileSystem BaseFileSystem { get; private set; }

    /// <summary>
    /// Assigns global instance when added to the scene tree.
    /// </summary>
    public override void _EnterTree()
    {
        Instance = this;
    }

    /// <summary>
    /// Initializes runtime services and seeds the base OS image.
    /// </summary>
    public override void _Ready()
    {
        BlobStore = new BlobStore();
        BaseFileSystem = new BaseFileSystem(BlobStore);
        BuildBaseOsImage();
    }

    /// <summary>
    /// Creates a minimal shared base image used by all server nodes.
    /// </summary>
    private void BuildBaseOsImage()
    {
        BaseFileSystem.AddDirectory("/bin");
        BaseFileSystem.AddDirectory("/etc");
        BaseFileSystem.AddDirectory("/home/player");
        BaseFileSystem.AddDirectory("/var/log");

        BaseFileSystem.AddFile("/system.bin", "uplink2-base-system");
        BaseFileSystem.AddFile("/etc/motd", "Welcome to Uplink2 runtime.");
        BaseFileSystem.AddFile("/bin/help.ms", "print \"help: ls, cd, cat, vim\"");
        BaseFileSystem.AddFile("/bin/ls.ms", "print \"ls (base stub)\"");
        BaseFileSystem.AddFile("/home/player/.profile", "export TERM=uplink2");
    }
}
