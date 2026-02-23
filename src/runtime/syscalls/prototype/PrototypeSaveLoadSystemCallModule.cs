namespace Uplink2.Runtime.Syscalls;

internal sealed class PrototypeSaveLoadSystemCallModule : ISystemCallModule
{
    public void Register(SystemCallRegistry registry)
    {
        registry.Register(new PrototypeSaveCommandHandler());
        registry.Register(new PrototypeLoadCommandHandler());
    }
}
