// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Printers.Tests;

public sealed class RulesTests
{
    private static PrinterInfo P(string name, string driver, string port, bool def = false) =>
        new(name, driver, port, PrinterRules.KindOf(port, false), PrinterState.Ready, def, 0);

    [Theory]
    [InlineData("Microsoft Print to PDF", "Microsoft Print To PDF", "PORTPROMPT:")]
    [InlineData("Microsoft XPS Document Writer", "Microsoft XPS Document Writer v4", "PORTPROMPT:")]
    [InlineData("OneNote (Desktop)", "Send to Microsoft OneNote 16 Driver", "nul:")]
    [InlineData("Fax", "Microsoft Shared Fax Driver", "SHRFAX:")]
    [InlineData("Whatever", "Some Driver", "FILE:")]
    public void VirtualPrintersAreRecognised(string name, string driver, string port) => Assert.True(PrinterRules.IsVirtual(name, driver, port));

    [Fact]
    public void PhysicalListDropsVirtualAndPutsDefaultFirst()
    {
        var list = PrinterRules.Physical([
            P("Microsoft Print to PDF", "Microsoft Print To PDF", "PORTPROMPT:"),
            P("Zebra", "ZDesigner", "USB002"),
            P("EPSON L3250 Series", "EPSON L3250 Series", "USB001", def: true),
            P("Brother", "Brother HL", "IP_10.0.0.5")]);
        Assert.Equal(["EPSON L3250 Series", "Brother", "Zebra"], list.Select(p => p.Name));
    }

    [Theory]
    [InlineData("USB001", false, PortKind.Usb)]
    [InlineData("IP_192.168.1.5", false, PortKind.Network)]
    [InlineData("WSD-1234", false, PortKind.Network)]
    [InlineData("192.168.1.9", false, PortKind.Network)]
    [InlineData("LPT1:", false, PortKind.Other)]
    [InlineData("COM1:", true, PortKind.Network)]
    public void PortKinds(string port, bool network, PortKind kind) => Assert.Equal(kind, PrinterRules.KindOf(port, network));

    [Theory]
    [InlineData(0u, 0u, PrinterState.Ready)]
    [InlineData(0x400u, 0u, PrinterState.Printing)]
    [InlineData(0x1u, 0u, PrinterState.Paused)]
    [InlineData(0x80u, 0u, PrinterState.Offline)]
    [InlineData(0u, 0x400u, PrinterState.Offline)]
    [InlineData(0x10u, 0u, PrinterState.PaperOut)]
    [InlineData(0x8u, 0u, PrinterState.Error)]
    [InlineData(0x400u | 0x10u, 0u, PrinterState.PaperOut)]
    public void StateMapping(uint status, uint attributes, PrinterState expected) => Assert.Equal(expected, PrinterRules.StateOf(status, attributes));
}
