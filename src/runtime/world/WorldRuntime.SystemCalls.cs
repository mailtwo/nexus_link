using System;
using Uplink2.Runtime.Syscalls;

namespace Uplink2.Runtime;

public partial class WorldRuntime
{
    /// <summary>Initializes system-call modules and command dispatch processor.</summary>
    private void InitializeSystemCalls()
    {
        ISystemCallModule[] modules =
        {
            new VfsSystemCallModule(),
        };

        systemCallProcessor = new SystemCallProcessor(this, modules);
    }

    /// <summary>Executes a terminal system call against world runtime state.</summary>
    public SystemCallResult ExecuteSystemCall(SystemCallRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (systemCallProcessor is null)
        {
            return SystemCallResultFactory.Failure(
                SystemCallErrorCode.InternalError,
                "system call processor is not initialized.");
        }

        return systemCallProcessor.Execute(request);
    }
}
