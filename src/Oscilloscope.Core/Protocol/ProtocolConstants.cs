namespace Oscilloscope.Core.Protocol;

public static class ProtocolConstants
{
    public const byte Magic0 = 0xC7;
    public const byte Magic1 = 0xE2;

    public const int FrameLength = 8;
    public const int RecordHeaderLength = 64;
    public const byte RecordTypeData = 0xD0;

    public const uint MaxSamplesPerChannel = 8192;

    // Zabezpieczenie przed zepsutym nagłówkiem: maksimum to 2 kanały po 8192 próbki (8 bit).
    public const uint MaxPayloadLength = 2 * MaxSamplesPerChannel;
}

public enum Command : byte
{
    Write = 0x01,
    Read = 0x02,
    ResponseWrite = 0x81,
    ResponseRead = 0x82,
    ResponseError = 0xFF,
}

public enum ErrorCode : byte
{
    UnknownCommand = 0x01,
    BadAddress = 0x02,
    ReadOnly = 0x03,
    WriteOnly = 0x04,
    BadValue = 0x05,
    NotReady = 0x06,
}

public enum AcquisitionState : uint
{
    Idle = 0,
    Armed = 1,
    Capture = 2,
    Ready = 3,
}

/// <summary>Adresy rejestrów (na razie tylko status "implemented").</summary>
public static class Registers
{
    public const byte DeviceId = 0x00;
    public const byte ProtocolVersion = 0x01;

    public const byte CommandArm = 0x11;
    public const byte CommandStop = 0x12;
    public const byte CommandForceTrigger = 0x13;
    public const byte CommandReadRecord = 0x17;

    public const byte AcquisitionState = 0xA0;
    public const byte AcquisitionRecordId = 0xA4;

    public const byte DebugScratch0 = 0xF0;
    public const byte DebugScratch1 = 0xF1;
    public const byte DebugLed = 0xF2;
    public const byte DebugConst = 0xF3;
    
    public const byte CommandCommit = 0x10;
    public const byte CommandClearCounters = 0x15;
    public const byte CommandResetConfig = 0x16;

    public const byte AcquisitionRecordLength = 0x31;
    public const byte AcquisitionPretrigger = 0x32;
    public const byte AcquisitionChannelMask = 0x34;

    public const byte TriggerSource = 0x40;
    public const byte TriggerEdge = 0x41;
    public const byte TriggerLevel = 0x42;
    public const byte TriggerHysteresis = 0x43;

    public const byte CounterFramesOk = 0x90;
    public const byte CounterBadMagic = 0x91;
    public const byte CounterUnknownCommand = 0x92;
    public const byte CounterBadAddress = 0x93;

    public const byte AcquisitionWritePointer = 0xA1;
    public const byte AcquisitionTriggerPointer = 0xA2; 
}
