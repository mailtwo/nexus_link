namespace Uplink2.Runtime.Syscalls;

internal sealed class VfsSystemCallModule : ISystemCallModule
{
    private readonly bool enableDebugCommands;

    internal VfsSystemCallModule(bool enableDebugCommands = false)
    {
        this.enableDebugCommands = enableDebugCommands;
    }

    public void Register(SystemCallRegistry registry)
    {
        registry.Register(new HelpCommandHandler());
        registry.Register(new PwdCommandHandler());
        registry.Register(new LsCommandHandler());
        registry.Register(new CdCommandHandler());
        registry.Register(new CatCommandHandler());
        registry.Register(new EditCommandHandler());
        registry.Register(new MkdirCommandHandler());
        registry.Register(new CpCommandHandler());
        registry.Register(new MvCommandHandler());
        if (enableDebugCommands)
        {
            registry.Register(new DebugMiniScriptCommandHandler());
        }

        registry.Register(new RmCommandHandler());
    }
}
