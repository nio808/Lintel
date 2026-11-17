// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Permissions;

namespace Lintel.Printers;

public enum PrinterAction { NozzleCheck, HeadClean, PowerClean, ResetPad }

public sealed record ButtonState(bool Enabled, bool Armed, string? Reason);

public enum ClickResult { Armed, Done, Refused, Failed }

public sealed record ActionDone(string Printer, PrinterAction Action, bool Success);

/// <summary>
/// Two-step confirms, lockout while the printer is busy cleaning and the enable rules of the buttons. No WPF, no clock of its own,
/// no printer access except through <see cref="IPrinterMaintenance"/>, so it is fully testable.
/// </summary>
public sealed class MaintenanceController(TimeProvider time, IPrinterMaintenance device, Func<string, bool> isGranted)
{
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ResetConfirmWindow = TimeSpan.FromSeconds(12);

    public const string HeadCleanConfirm = "Uses ink · Confirm?";
    public const string PowerCleanConfirm = "Uses a lot of ink and fills the ink pad. Only use it if 3 head cleans did not help. Confirm?";
    public const string ResetConfirm = "Only after the ink pads were replaced or cleaned. Resetting without doing that can let waste ink overflow inside the printer. Confirm reset?";

    private (string Printer, PrinterAction Action, DateTimeOffset Until)? armed;
    private readonly Dictionary<string, (PrinterAction Action, DateTimeOffset Until)> locks = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ActionDone>? Completed;

    /// <summary>The printer is physically busy for about this long after the command is sent.</summary>
    public static TimeSpan Lockout(PrinterAction a) => a switch
    {
        PrinterAction.NozzleCheck => TimeSpan.FromSeconds(30),
        PrinterAction.HeadClean => TimeSpan.FromMinutes(3),
        PrinterAction.PowerClean => TimeSpan.FromMinutes(8),
        _ => TimeSpan.FromSeconds(5),
    };

    public static string ConfirmText(PrinterAction a) => a switch
    {
        PrinterAction.HeadClean => HeadCleanConfirm, PrinterAction.PowerClean => PowerCleanConfirm, PrinterAction.ResetPad => ResetConfirm, _ => "",
    };

    public (PrinterAction Action, TimeSpan Remaining)? Running(string printer)
    {
        if (locks.TryGetValue(printer, out var l) && l.Until > time.GetUtcNow())
            return (l.Action, l.Until - time.GetUtcNow());
        return null;
    }

    public bool IsArmed(string printer, PrinterAction action) =>
        armed is { } a && a.Printer == printer && a.Action == action && a.Until > time.GetUtcNow();

    public ButtonState StateOf(PrinterAction action, PrinterInfo printer, bool jobActive)
    {
        var armedNow = IsArmed(printer.Name, action);
        var permission = action == PrinterAction.ResetPad ? PermissionIds.PrinterPadReset : PermissionIds.PrinterMaintenance;
        if (!device.IsSupported(printer.Driver))
            return new(false, false, "Maintenance commands are not available for this printer");
        if (!isGranted(permission))
            return new(false, false, "Turn this permission on in Settings > Permissions");
        if (action == PrinterAction.ResetPad && !device.CanResetPad(printer.Driver))
            return new(false, false, "Resetting the counter is not available for this model");
        if (Running(printer.Name) is { } r)
            return new(false, false, $"Busy: {r.Action} running");
        if (jobActive)
            return new(false, false, "Disabled while a job is printing");
        if (!printer.IsOnline)
            return new(false, false, "The printer is offline");
        return new(true, armedNow, null);
    }

    public async Task<ClickResult> ClickAsync(PrinterAction action, PrinterInfo printer, bool jobActive, CancellationToken ct = default)
    {
        var state = StateOf(action, printer, jobActive);
        if (!state.Enabled)
        {
            armed = null;
            return ClickResult.Refused;
        }
        if (action != PrinterAction.NozzleCheck && !state.Armed)
        {
            armed = (printer.Name, action, time.GetUtcNow() + (action == PrinterAction.ResetPad ? ResetConfirmWindow : ConfirmWindow));
            return ClickResult.Armed;
        }
        armed = null;
        // Lock before awaiting: a second click while the command is in flight must not send another one.
        locks[printer.Name] = (action, time.GetUtcNow() + Lockout(action));
        var ok = action switch
        {
            PrinterAction.ResetPad => await device.ResetPadAsync(printer.Name, ct).ConfigureAwait(true),
            _ => await device.RunAsync(printer.Name, action switch
            {
                PrinterAction.NozzleCheck => MaintenanceAction.NozzleCheck,
                PrinterAction.HeadClean => MaintenanceAction.HeadClean,
                _ => MaintenanceAction.PowerClean,
            }, ct).ConfigureAwait(true),
        };
        if (!ok)
            locks.Remove(printer.Name);
        Completed?.Invoke(new ActionDone(printer.Name, action, ok));
        return ok ? ClickResult.Done : ClickResult.Failed;
    }

    public void Disarm() => armed = null;
}
