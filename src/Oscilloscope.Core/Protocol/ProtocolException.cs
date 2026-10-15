namespace Oscilloscope.Core.Protocol;

/// <summary>Dane od urządzenia łamią protokół. Połączenie należy zamknąć.</summary>
public sealed class ProtocolException(string message) : Exception(message);
