function Start-CrimsonBuildPowerRequest {
    if (-not ('CrimsonBuildPower' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class CrimsonBuildPower
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    public static bool Acquire() => SetThreadExecutionState(0x80000001u) != 0;
    public static void Release() => SetThreadExecutionState(0x80000000u);
}
'@
    }
    # System-required only: automatic display-off and explicit user sleep remain available.
    return [CrimsonBuildPower]::Acquire()
}

function Stop-CrimsonBuildPowerRequest {
    if ('CrimsonBuildPower' -as [type]) { [CrimsonBuildPower]::Release() }
}
