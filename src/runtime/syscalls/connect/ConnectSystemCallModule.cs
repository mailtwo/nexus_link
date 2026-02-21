namespace Uplink2.Runtime.Syscalls;

internal sealed class ConnectSystemCallModule : ISystemCallModule
{
    public void Register(SystemCallRegistry registry)
    {
        registry.Register(new ConnectCommandHandler());
        registry.Register(new DisconnectCommandHandler());
    }
}
