using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.ViewModels;

/// <summary>One polled register shown in the debug window.</summary>
public partial class DebugRow(string name, byte address, Func<uint, string> format) : ObservableObject
{
    public string Name { get; } = name;
    public byte Address { get; } = address;
    public Func<uint, string> Format { get; } = format;

    [ObservableProperty]
    public partial string Value { get; set; } = "-";
}

public partial class DebugViewModel : ViewModelBase, IDisposable
{
    private readonly Func<DeviceClient?> _getDevice;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<DebugRow> InfoRows { get; }
    public ObservableCollection<DebugRow> StatusRows { get; }
    public ObservableCollection<DebugRow> CounterRows { get; }

    [ObservableProperty]
    public partial string PollStatus { get; set; } = "";

    [ObservableProperty]
    public partial string ConsoleAddress { get; set; } = "F0";

    [ObservableProperty]
    public partial string ConsoleValue { get; set; } = "00000000";

    [ObservableProperty]
    public partial string ConsoleResult { get; set; } = "";

    public DebugViewModel(Func<DeviceClient?> getDevice)
    {
        _getDevice = getDevice;

        InfoRows =
        [
            new("DEVICE_ID", Registers.DeviceId, Hex),
            new("PROTOCOL_VERSION", Registers.ProtocolVersion, Dec),
        ];
        StatusRows =
        [
            new("ACQUISITION_STATE", Registers.AcquisitionState, State),
            new("RECORD_ID", Registers.AcquisitionRecordId, Dec),
            new("WRITE_POINTER", Registers.AcquisitionWritePointer, Dec),
            new("TRIGGER_POINTER", Registers.AcquisitionTriggerPointer, Dec),
        ];
        CounterRows =
        [
            new("FRAMES_OK", Registers.CounterFramesOk, Dec),
            new("BAD_MAGIC", Registers.CounterBadMagic, Dec),
            new("UNKNOWN_COMMAND", Registers.CounterUnknownCommand, Dec),
            new("BAD_ADDRESS", Registers.CounterBadAddress, Dec),
        ];

        // Started from the UI thread, so the continuations stay on it.
        _ = PollAsync();
    }

    private static string Hex(uint v) => $"0x{v:X8}";
    private static string Dec(uint v) => v.ToString(CultureInfo.InvariantCulture);
    private static string State(uint v) =>
        Enum.IsDefined(typeof(AcquisitionState), v) ? ((AcquisitionState)v).ToString() : Hex(v);

    /// <summary>
    /// Polls twice a second. The token is only checked between exchanges: cancelling in the
    /// middle of one would make DeviceClient drop the connection.
    /// </summary>
    private async Task PollAsync()
    {
        List<DebugRow> all = InfoRows.Concat(StatusRows).Concat(CounterRows).ToList();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var device = _getDevice();
                if (device is null || !device.IsConnected)
                {
                    PollStatus = "Not connected.";
                }
                else
                {
                    foreach (var row in all)
                    {
                        if (_cts.IsCancellationRequested) return;
                        try
                        {
                            row.Value = row.Format(await device.ReadRegisterAsync(row.Address));
                        }
                        catch (DeviceErrorException ex)
                        {
                            row.Value = $"error {ex.Code}";
                        }
                    }
                    PollStatus = $"Updated {DateTime.Now:HH:mm:ss}.";
                }

                await Task.Delay(500, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PollStatus = $"Polling stopped: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearCountersAsync()
    {
        await RunOnDeviceAsync(async device =>
        {
            await device.SendCommandAsync(Registers.CommandClearCounters);
            return "Counters cleared.";
        });
    }

    [RelayCommand]
    private async Task ReadRegisterAsync()
    {
        if (!TryParseHex(ConsoleAddress, out uint address) || address > 0xFF)
        {
            ConsoleResult = "Address must be a hex byte (00..FF).";
            return;
        }

        await RunOnDeviceAsync(async device =>
        {
            uint value = await device.ReadRegisterAsync((byte)address);
            ConsoleValue = $"{value:X8}";
            return $"0x{address:X2} = 0x{value:X8}";
        });
    }

    [RelayCommand]
    private async Task WriteRegisterAsync()
    {
        if (!TryParseHex(ConsoleAddress, out uint address) || address > 0xFF)
        {
            ConsoleResult = "Address must be a hex byte (00..FF).";
            return;
        }
        if (!TryParseHex(ConsoleValue, out uint value))
        {
            ConsoleResult = "Value must be a hex number (up to 8 digits).";
            return;
        }

        await RunOnDeviceAsync(async device =>
        {
            await device.WriteRegisterAsync((byte)address, value);
            return $"0x{address:X2} <- 0x{value:X8} written.";
        });
    }

    private async Task RunOnDeviceAsync(Func<DeviceClient, Task<string>> action)
    {
        var device = _getDevice();
        if (device is null || !device.IsConnected)
        {
            ConsoleResult = "Not connected.";
            return;
        }

        try
        {
            ConsoleResult = await action(device);
        }
        catch (DeviceErrorException ex)
        {
            ConsoleResult = $"Device error: {ex.Code}.";
        }
        catch (Exception ex)
        {
            ConsoleResult = $"Error: {ex.Message}";
        }
    }

    private static bool TryParseHex(string text, out uint value)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose() => _cts.Cancel();
}
