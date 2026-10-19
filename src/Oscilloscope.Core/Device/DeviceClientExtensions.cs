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
                    $"State {wanted} not reached within {timeout.TotalSeconds:0.#} s (currently {state}).");
            await Task.Delay(10, ct);
        }
    }

    /// <summary>STOP, ARM, FORCE_TRIGGER, wait for READY, then read the record.</summary>
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

    /// <summary>
    /// STOP, ARM, wait for the trigger (state READY) and read the record.
    /// The wait has no time limit: the scope waits for an event. Returns null when
    /// interrupted through <paramref name="stop"/> (the device is then stopped).
    /// Cancellation is only checked between complete exchanges, so it never cuts the connection.
    /// <para>
    /// Configuration changes: when <paramref name="hasPendingConfig"/> reports a queued change,
    /// the device is stopped, <paramref name="applyPendingConfig"/> runs (the device is IDLE,
    /// so COMMIT is never sent during a capture) and the device is armed again.
    /// </para>
    /// </summary>
    public static async Task<AcquisitionRecord?> AcquireTriggeredAsync(
        this DeviceClient device,
        CancellationToken stop,
        Func<bool>? hasPendingConfig = null,
        Func<Task>? applyPendingConfig = null)
    {
        bool CanApply() => hasPendingConfig?.Invoke() == true && applyPendingConfig is not null;

        await device.SendCommandAsync(Registers.CommandStop);
        if (CanApply())
            await applyPendingConfig!();
        await device.SendCommandAsync(Registers.CommandArm);

        while (true)
        {
            var state = await device.GetStateAsync();
            if (state == AcquisitionState.Ready) break;

            if (stop.IsCancellationRequested)
            {
                await device.SendCommandAsync(Registers.CommandStop);
                return null;
            }

            if (CanApply())
            {
                await device.SendCommandAsync(Registers.CommandStop);
                await applyPendingConfig!();
                await device.SendCommandAsync(Registers.CommandArm);
                continue;
            }

            await Task.Delay(10);
        }

        return await device.ReadRecordAsync()
            ?? throw new ProtocolException("Device reported READY but returned NOT_READY.");
    }
}