using System.Diagnostics;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Protocol;

// Użycie: SmokeTest [regs|record] [host] [port]
string mode = args.Length > 0 ? args[0] : "regs";
string host = args.Length > 1 ? args[1] : "192.168.1.50";
int port = args.Length > 2 ? int.Parse(args[2]) : 5000;

await using var device = new DeviceClient();
Console.WriteLine($"Łączenie z {host}:{port} ...");
await device.ConnectAsync(host, port);
Console.WriteLine("Połączono.");

switch (mode)
{
    case "regs": await RunRegisters(); break;
    case "record": await RunRecord(); break;
    case "config": await RunConfig(); break;
    case "commit": await RunCommit(); break;
    default: Console.WriteLine("Nieznany tryb. Użyj: regs albo record."); break;
}

async Task Show(string name, byte address) =>
    Console.WriteLine($"{name,-20} = 0x{await device.ReadRegisterAsync(address):X8}");

async Task RunRegisters()
{
    await Show("DEVICE_ID", Registers.DeviceId);
    await Show("PROTOCOL_VERSION", Registers.ProtocolVersion);
    await Show("DEBUG_CONST", Registers.DebugConst);          // oczekiwane 0xDEADBEEF
    await Show("ACQUISITION_STATE", Registers.AcquisitionState);

    Console.WriteLine("Zapis/odczyt DEBUG_SCRATCH0 ...");
    await device.WriteRegisterAsync(Registers.DebugScratch0, 0x12345678);
    await Show("DEBUG_SCRATCH0", Registers.DebugScratch0);    // oczekiwane 0x12345678

    Console.WriteLine("Zapis do rejestru RO (oczekiwany błąd READ_ONLY) ...");
    try
    {
        await device.WriteRegisterAsync(Registers.DeviceId, 1);
    }
    catch (DeviceErrorException ex)
    {
        Console.WriteLine($"OK, błąd: {ex.Code}");
    }
}

async Task RunConfig()
{
    await Show("RECORD_LENGTH", Registers.AcquisitionRecordLength);
    await Show("PRETRIGGER", Registers.AcquisitionPretrigger);
    await Show("CHANNEL_MASK", Registers.AcquisitionChannelMask);
    await Show("TRIGGER_SOURCE", Registers.TriggerSource);
    await Show("TRIGGER_EDGE", Registers.TriggerEdge);
    await Show("TRIGGER_LEVEL", Registers.TriggerLevel);
    await Show("TRIGGER_HYSTERESIS", Registers.TriggerHysteresis);
}

async Task RunCommit()
{
    Console.WriteLine("Pending config before:");
    await RunConfig();

    Console.WriteLine("COMMIT ...");
    try
    {
        await device.SendCommandAsync(Registers.CommandCommit);
        Console.WriteLine("COMMIT OK.");
    }
    catch (DeviceErrorException ex)
    {
        Console.WriteLine($"COMMIT rejected: {ex.Code}");
    }
}

async Task<AcquisitionState> GetState() =>
    (AcquisitionState)await device.ReadRegisterAsync(Registers.AcquisitionState);

async Task WaitForState(AcquisitionState wanted, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (true)
    {
        var state = await GetState();
        if (state == wanted) return;
        if (sw.Elapsed > timeout)
            throw new TimeoutException($"Stan {wanted} nie nastąpił w {timeout.TotalSeconds:0.#} s (jest {state}).");
        await Task.Delay(20);
    }
}

async Task RunRecord()
{
    Console.WriteLine($"Stan początkowy: {await GetState()}");

    Console.WriteLine("STOP -> ARM ...");
    await device.SendCommandAsync(Registers.CommandStop);
    await device.SendCommandAsync(Registers.CommandArm);
    await WaitForState(AcquisitionState.Armed, TimeSpan.FromSeconds(1));
    Console.WriteLine($"Stan: {await GetState()}");

    Console.WriteLine("FORCE_TRIGGER ...");
    await device.SendCommandAsync(Registers.CommandForceTrigger);
    await WaitForState(AcquisitionState.Ready, TimeSpan.FromSeconds(3));
    Console.WriteLine($"Stan: {await GetState()}");

    uint recordId = await device.ReadRegisterAsync(Registers.AcquisitionRecordId);
    Console.WriteLine($"ACQUISITION_RECORD_ID = {recordId}");

    Console.WriteLine("READ_RECORD ...");
    var sw = Stopwatch.StartNew();
    var record = await device.ReadRecordAsync();
    sw.Stop();

    if (record is null)
    {
        Console.WriteLine("Urządzenie zwróciło NOT_READY.");
        return;
    }

    var h = record.Header;
    Console.WriteLine($"Pobrano {record.Samples.Length} B w {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  RecordId          = {h.RecordId}");
    Console.WriteLine($"  ChannelMask       = 0x{h.ChannelMask:X2} ({h.ChannelCount} kan.)");
    Console.WriteLine($"  SamplesPerChannel = {h.SamplesPerChannel}");
    Console.WriteLine($"  SampleBits        = {h.SampleBits}");
    Console.WriteLine($"  Overrange         = {h.Overrange}");

    for (int ch = 1; ch <= 2; ch++)
    {
        if ((h.ChannelMask & (1 << (ch - 1))) == 0) continue;
        var s = record.ExtractChannel(ch);
        Console.WriteLine($"  CH{ch}: min={s.Min()} max={s.Max()} avg={s.Select(b => (int)b).Average():F1}");
        Console.WriteLine($"       pierwsze: {string.Join(' ', s.Take(16).Select(b => b.ToString("X2")))}");
    }

    // Powtórny odczyt ma dać ten sam rekord (odczyt nie zmienia stanu).
    var again = await device.ReadRecordAsync();
    Console.WriteLine(again is not null && again.Header.RecordId == h.RecordId
        ? "Powtórny odczyt: ten sam RECORD_ID, OK."
        : "Powtórny odczyt: inny wynik albo NOT_READY.");
}
