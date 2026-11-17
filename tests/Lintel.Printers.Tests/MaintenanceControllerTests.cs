// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Permissions;

namespace Lintel.Printers.Tests;

public sealed class MaintenanceControllerTests
{
    private readonly FakeTime time = new();
    private readonly FakeDevice device = new();
    private readonly HashSet<string> granted = [PermissionIds.PrinterMaintenance, PermissionIds.PrinterPadReset];
    private static readonly PrinterInfo Printer = new("EPSON", "EPSON L3250 Series", "USB001", PortKind.Usb, PrinterState.Ready, true, 0);

    private MaintenanceController Make() => new(time, device, granted.Contains);

    [Theory]
    [InlineData(PrinterAction.NozzleCheck)]
    [InlineData(PrinterAction.HeadClean)]
    [InlineData(PrinterAction.PowerClean)]
    public async Task ReportsRunningActionUntilLockoutExpires(PrinterAction action)
    {
        var c = Make();
        Assert.Null(c.Running());
        await c.ClickAsync(action, Printer, false);
        if (action != PrinterAction.NozzleCheck)
        {
            Assert.Null(c.Running()); // arming does not start maintenance
            await c.ClickAsync(action, Printer, false);
        }
        var duration = MaintenanceController.Lockout(action);
        Assert.Equal((Printer.Name, action, duration), c.Running()!.Value);
        Assert.Equal($"Busy: {PrinterText.ActionName(action)} running", c.StateOf(PrinterAction.NozzleCheck, Printer, false).Reason);
        time.Advance(duration - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), c.Running()!.Value.Remaining);
        Assert.Empty(c.TakeFinished());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(c.Running());
        Assert.Null(c.Running(Printer.Name));
        Assert.Equal((Printer.Name, action), Assert.Single(c.TakeFinished()));
        Assert.Empty(c.TakeFinished());
    }

    [Fact]
    public async Task RunningFindsAnotherPrinterAndSkipsResetPad()
    {
        var c = Make();
        await c.ClickAsync(PrinterAction.ResetPad, Printer, false);
        await c.ClickAsync(PrinterAction.ResetPad, Printer, false);
        Assert.Null(c.Running());
        var other = Printer with { Name = "Other" };
        await c.ClickAsync(PrinterAction.HeadClean, other, false);
        await c.ClickAsync(PrinterAction.HeadClean, other, false);
        Assert.Equal((other.Name, PrinterAction.HeadClean, TimeSpan.FromMinutes(3)), c.Running()!.Value);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(c.Running());
        Assert.Equal(2, c.TakeFinished().Count);
        Assert.Empty(c.TakeFinished());
    }

    [Fact]
    public async Task HeadCleanNeedsASecondClickWithinFourSeconds()
    {
        var c = Make();
        Assert.Equal(ClickResult.Armed, await c.ClickAsync(PrinterAction.HeadClean, Printer, false));
        Assert.Empty(device.Sent);
        Assert.True(c.IsArmed("EPSON", PrinterAction.HeadClean));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(c.IsArmed("EPSON", PrinterAction.HeadClean));
        Assert.Equal(ClickResult.Armed, await c.ClickAsync(PrinterAction.HeadClean, Printer, false));   // expired: arms again, sends nothing
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ClickResult.Done, await c.ClickAsync(PrinterAction.HeadClean, Printer, false));
        Assert.Equal(["HeadClean"], device.Sent);
    }

    [Fact]
    public async Task NozzleCheckNeedsNoConfirm()
    {
        var c = Make();
        Assert.Equal(ClickResult.Done, await c.ClickAsync(PrinterAction.NozzleCheck, Printer, false));
        Assert.Equal(["NozzleCheck"], device.Sent);
    }

    [Fact]
    public async Task ClickingAnotherButtonMovesTheConfirm()
    {
        var c = Make();
        await c.ClickAsync(PrinterAction.HeadClean, Printer, false);
        await c.ClickAsync(PrinterAction.PowerClean, Printer, false);
        Assert.False(c.IsArmed("EPSON", PrinterAction.HeadClean));
        Assert.True(c.IsArmed("EPSON", PrinterAction.PowerClean));
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task LockoutMatchesTheCleaningDuration()
    {
        var c = Make();
        await c.ClickAsync(PrinterAction.PowerClean, Printer, false);
        await c.ClickAsync(PrinterAction.PowerClean, Printer, false);
        Assert.Equal(TimeSpan.FromMinutes(8), c.Running("EPSON")!.Value.Remaining);
        Assert.False(c.StateOf(PrinterAction.NozzleCheck, Printer, false).Enabled);
        Assert.Equal(ClickResult.Refused, await c.ClickAsync(PrinterAction.HeadClean, Printer, false));
        time.Advance(TimeSpan.FromMinutes(7));
        Assert.False(c.StateOf(PrinterAction.HeadClean, Printer, false).Enabled);
        time.Advance(TimeSpan.FromMinutes(1.1));
        Assert.True(c.StateOf(PrinterAction.HeadClean, Printer, false).Enabled);
        Assert.Equal(TimeSpan.FromMinutes(3), MaintenanceController.Lockout(PrinterAction.HeadClean));
    }

    [Fact]
    public void DisabledWhileAJobIsActiveOrOfflineOrUnsupportedOrNotConsented()
    {
        var c = Make();
        Assert.False(c.StateOf(PrinterAction.HeadClean, Printer, jobActive: true).Enabled);
        Assert.False(c.StateOf(PrinterAction.HeadClean, Printer with { State = PrinterState.Offline }, false).Enabled);
        Assert.True(c.StateOf(PrinterAction.HeadClean, Printer with { State = PrinterState.Printing }, false).Enabled);
        device.Supported = false;
        Assert.Equal("Maintenance commands are not available for this printer", c.StateOf(PrinterAction.HeadClean, Printer, false).Reason);
        device.Supported = true;
        granted.Remove(PermissionIds.PrinterMaintenance);
        Assert.False(c.StateOf(PrinterAction.NozzleCheck, Printer, false).Enabled);
        Assert.True(c.StateOf(PrinterAction.ResetPad, Printer, false).Enabled);   // its own permission
    }

    [Fact]
    public async Task FailedSendReleasesTheLockAndReports()
    {
        var c = Make();
        ActionDone? done = null;
        c.Completed += d => done = d;
        device.SendOk = false;
        await c.ClickAsync(PrinterAction.HeadClean, Printer, false);
        Assert.Equal(ClickResult.Failed, await c.ClickAsync(PrinterAction.HeadClean, Printer, false));
        Assert.False(done!.Success);
        Assert.Null(c.Running("EPSON"));
        Assert.Null(c.Running());
        time.Advance(MaintenanceController.Lockout(PrinterAction.HeadClean));
        Assert.Empty(c.TakeFinished());
    }

    [Fact]
    public void ConfirmTextsWarnAboutInk()
    {
        Assert.Contains("Uses ink", MaintenanceController.ConfirmText(PrinterAction.HeadClean));
        var power = MaintenanceController.ConfirmText(PrinterAction.PowerClean);
        Assert.Contains("lot of ink", power);
        Assert.Contains("3 head cleans", power);
        Assert.Contains("replaced or cleaned", MaintenanceController.ConfirmText(PrinterAction.ResetPad));
        Assert.Contains("overflow", MaintenanceController.ConfirmText(PrinterAction.ResetPad));
    }

    [Fact]
    public async Task PadResetIsTwoStepAndNeedsModelSupport()
    {
        var c = Make();
        Assert.Equal(ClickResult.Armed, await c.ClickAsync(PrinterAction.ResetPad, Printer, false));
        time.Advance(TimeSpan.FromSeconds(8));   // longer window than a cleaning confirm
        Assert.True(c.IsArmed("EPSON", PrinterAction.ResetPad));
        Assert.Equal(ClickResult.Done, await c.ClickAsync(PrinterAction.ResetPad, Printer, false));
        Assert.Equal(["ResetPad"], device.Sent);

        var other = Make();
        device.Reset = false;
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ClickResult.Refused, await other.ClickAsync(PrinterAction.ResetPad, Printer, false));
    }

    [Fact]
    public async Task PadResetIsBlockedDuringJobs()
    {
        var c = Make();
        Assert.Equal(ClickResult.Refused, await c.ClickAsync(PrinterAction.ResetPad, Printer, true));
        Assert.Empty(device.Sent);
    }
}
