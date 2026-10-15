# Arty Scope Desktop App

Desktop application for a custom two-channel oscilloscope built on the Digilent Arty A7 100T. Displays captured waveforms and controls the device over Ethernet (TCP).

## Hardware

* FPGA board: Digilent Arty A7 100T (Artix-7 - xc7a100tcsg324-1)
* Custom scope board containing:
  * ADC: AD9288-100 (dual 8-bit, 100 MS/s)
  * PGA: AD8370 (variable gain amplifier)
  * DAC: MCP4822 (DC offset)

## Related repositories

* FPGA design: [arty-scope-hdl](https://github.com/Filip-Kubecki/arty-scope-hdl)
* PCB: [arty-scope-expansion-board](https://github.com/Filip-Kubecki/arty-scope-expansion-board)

## Tech stack

* C# / .NET 8 (target `net8.0`)
* [Avalonia UI](https://avaloniaui.net/) 12
* [ScottPlot](https://scottplot.net/) 5
* MVVM with CommunityToolkit.Mvvm

## Protocol

The register map, protocol constants and error codes are described in `regmap.yaml`. It is the single source of truth shared with the FPGA design. If it changes, update the copy in this repository.

Frames start with the magic bytes `0xC7 0xE2`. Supported commands are register write and read, plus a record read that returns the last complete acquisition.

## Repository structure

```
src/
  Oscilloscope.App/    Avalonia application (UI)
  Oscilloscope.Core/   transport, protocol, device layer (planned)
tests/
  Oscilloscope.Tests/  unit tests (planned)
regmap.yaml            Register map
```

## Getting started

Requirements:

* .NET SDK 10. The projects target `net8.0`, but Avalonia 12 and CommunityToolkit.Mvvm need a newer compiler than the one shipped with SDK 8.
* Linux, Windows

Clone and run:

```
git clone https://github.com/Filip-Kubecki/OscilloscopeApp.git
cd OscilloscopeApp
dotnet build
dotnet run --project src/Oscilloscope.App
```

The repository can be opened directly in JetBrains Rider (open `Oscilloscope.sln`).

## Status

Work in progress.
