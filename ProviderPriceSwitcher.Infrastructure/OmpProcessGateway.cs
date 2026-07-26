using System.Diagnostics;

namespace ProviderPriceSwitcher.Infrastructure;

public interface IOmpProcessGateway
{
    IReadOnlyList<Process> GetProcessesByName(string processName);
    Process? Start(ProcessStartInfo startInfo);
}

public sealed class SystemOmpProcessGateway : IOmpProcessGateway
{
    public IReadOnlyList<Process> GetProcessesByName(string processName) => Process.GetProcessesByName(processName);

    public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
