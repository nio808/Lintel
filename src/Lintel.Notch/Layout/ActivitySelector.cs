// SPDX-License-Identifier: GPL-3.0-or-later
using Lintel.Core.Notch;

namespace Lintel.Notch.Layout;

/// <summary>Which view of which activity sits in a wing.</summary>
public readonly record struct WingSlot(CompactActivity Activity, bool Trailing);

/// <summary>0 active → idle; 1 → its leading + trailing; ≥ 2 → the two highest priorities' leading views.</summary>
public static class ActivitySelector
{
    /// <param name="activities">In registration order; ties keep that order.</param>
    /// <param name="hasTrailing">Whether an activity created a trailing view.</param>
    public static (WingSlot? Left, WingSlot? Right) Select(IEnumerable<CompactActivity> activities, Func<CompactActivity, bool> hasTrailing)
    {
        var top = activities.Where(a => a.IsActive).OrderByDescending(a => a.Priority).Take(2).ToList();   // OrderBy is stable
        return top.Count switch
        {
            0 => (null, null),
            1 => (new WingSlot(top[0], false), hasTrailing(top[0]) ? new WingSlot(top[0], true) : null),
            _ => (new WingSlot(top[0], false), new WingSlot(top[1], false)),
        };
    }
}
