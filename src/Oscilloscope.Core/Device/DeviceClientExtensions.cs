using System.Diagnostics;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Device;

public static class DeviceClientExtensions
{
    public static async Task<AcquisitionState> GetStateAsync(
        this DeviceClient device, CancellationToken ct = default) =>
        (AcquisitionState)await device.ReadRegisterAsync(Registers.AcquisitionState, ct);

    public static async Task WaitForStateAsync(
        this DeviceClient device, AcquisitionState wanted, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var state = await device.GetStateAsync(ct);
            if (state == wanted) return;
            if (sw.Elapsed > timeout)
                throw new TimeoutException(
                    $"Stan {wanted} nie nastąpił w {timeout.TotalSeconds:0.#} s (jest {state}).");
            await Task.Delay(10, ct);
        }
    }

    /// <summary>STOP, ARM, FORCE_TRIGGER, czekanie na READY i pobranie rekordu.</summary>
    public static async Task<AcquisitionRecord?> AcquireSingleAsync(
        this DeviceClient device, CancellationToken ct = default)
    {
        await device.SendCommandAsync(Registers.CommandStop, ct);
        await device.SendCommandAsync(Registers.CommandArm, ct);
        await device.WaitForStateAsync(AcquisitionState.Armed, TimeSpan.FromSeconds(1), ct);
        await device.SendCommandAsync(Registers.CommandForceTrigger, ct);
        await device.WaitForStateAsync(AcquisitionState.Ready, TimeSpan.FromSeconds(3), ct);
        return await device.ReadRecordAsync(ct);
    }
}
