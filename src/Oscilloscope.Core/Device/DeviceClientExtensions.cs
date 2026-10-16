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

    /// <summary>
    /// STOP, ARM, czekanie na wyzwolenie (stan READY) i pobranie rekordu.
    /// Czekanie nie ma limitu czasu: oscyloskop czeka na zdarzenie. Zwraca null,
    /// gdy przerwano przez <paramref name="stop"/> (wtedy wysyła STOP).
    /// Anulowanie sprawdzane jest tylko między pełnymi wymianami, więc nie zrywa połączenia.
    /// </summary>
    public static async Task<AcquisitionRecord?> AcquireTriggeredAsync(
        this DeviceClient device, CancellationToken stop)
    {
        await device.SendCommandAsync(Registers.CommandStop);
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
            await Task.Delay(10);
        }

        return await device.ReadRecordAsync()
               ?? throw new ProtocolException("Device reported READY but returned NOT_READY.");
    }
}