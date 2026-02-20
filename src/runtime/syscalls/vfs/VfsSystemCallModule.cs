namespace Uplink2.Runtime.Syscalls;

internal sealed class VfsSystemCallModule : ISystemCallModule
{
    public void Register(SystemCallRegistry registry)
    {
        registry.Register(new PwdCommandHandler());
        registry.Register(new LsCommandHandler());
        registry.Register(new CdCommandHandler());
        registry.Register(new CatCommandHandler());
        registry.Register(new MkdirCommandHandler());
        registry.Register(new RmCommandHandler());
    }
}
