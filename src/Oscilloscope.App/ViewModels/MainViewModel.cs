using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private DeviceClient? _device;
    private CancellationTokenSource? _runCts;

    /// <summary>Wywoływane na wątku UI po pobraniu rekordu. Widok rysuje wykres.</summary>
    public event Action<AcquisitionRecord>? RecordReceived;

    [ObservableProperty]
    public partial string Host { get; set; } = "192.168.1.50";

    [ObservableProperty]
    public partial int Port { get; set; } = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Niepołączony.";

    [ObservableProperty]
    public partial string RecordInfo { get; set; } = "";

    public bool CanAcquire => IsConnected && !IsRunning && !IsBusy;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Status = $"Łączenie z {Host}:{Port} ...";
        var device = new DeviceClient();
        try
        {
            await device.ConnectAsync(Host, Port);
            uint id = await device.ReadRegisterAsync(Registers.DeviceId);
            uint version = await device.ReadRegisterAsync(Registers.ProtocolVersion);

            _device = device;
            IsConnected = true;
            Status = $"Połączono. DEVICE_ID = 0x{id:X8}, protokół v{version}.";
        }
        catch (Exception ex)
        {
            await device.DisposeAsync();
            Status = $"Błąd połączenia: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_device is not null)
            await _device.DisposeAsync();
        _device = null;
        IsConnected = false;
        Status = "Rozłączono.";
    }

    [RelayCommand]
    private async Task SingleAsync()
    {
        IsBusy = true;
        try
        {
            await AcquireOnceAsync();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartRunAsync()
    {
        if (IsRunning) return;

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        IsRunning = true;
        Status = "Akwizycja ciągła.";
        try
        {
            while (!token.IsCancellationRequested)
            {
                await AcquireOnceAsync();
                // Ograniczenie tempa odświeżania do ok. 30 klatek/s.
                await Task.Delay(30, token);
            }
            Status = "Zatrzymano.";
        }
        catch (OperationCanceledException)
        {
            Status = "Zatrzymano.";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void StopRun() => _runCts?.Cancel();

    private async Task AcquireOnceAsync()
    {
        var device = _device ?? throw new InvalidOperationException("Brak połączenia.");

        // Bez tokenu anulowania: przerwanie wymiany w połowie ramki zrywa połączenie
        // (płytka nie resynchronizuje danych). Stop działa między akwizycjami.
        var sw = Stopwatch.StartNew();
        var record = await device.AcquireSingleAsync();
        sw.Stop();

        if (record is null)
        {
            Status = "Urządzenie nie ma gotowego rekordu.";
            return;
        }

        var h = record.Header;
        RecordInfo = $"Rekord #{h.RecordId}, {h.SamplesPerChannel} próbek/kanał, cykl {sw.ElapsedMilliseconds} ms";
        RecordReceived?.Invoke(record);
    }

    private void HandleError(Exception ex)
    {
        Status = ex switch
        {
            DeviceErrorException e => $"Błąd urządzenia: {e.Code} (adres 0x{e.Address:X2}).",
            TimeoutException => $"Timeout: {ex.Message}",
            _ => $"Błąd: {ex.Message}",
        };

        // DeviceClient sam zamyka gniazdo po błędzie transportu lub protokołu.
        if (_device is { IsConnected: false })
        {
            _ = _device.DisposeAsync();
            _device = null;
            IsConnected = false;
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _ = _device?.DisposeAsync();
    }
}
