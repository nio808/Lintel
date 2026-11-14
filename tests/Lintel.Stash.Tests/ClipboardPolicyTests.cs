// SPDX-License-Identifier: GPL-3.0-or-later
namespace Lintel.Stash.Tests;

public sealed class ClipboardPolicyTests
{
    private static ClipboardChange Change(uint seq = 10, bool exclude = false, int? can = null, string? owner = "notepad") => new(seq, exclude, can, owner);

    [Fact]
    public void OrdinaryCopyIsCaptured() => Assert.Equal(CaptureDecision.Capture, new ClipboardPolicy().Evaluate(Change()));

    [Fact]
    public void PasswordManagerExclusionFormatIsSkipped()
    {
        var policy = new ClipboardPolicy();
        Assert.Equal(CaptureDecision.SensitiveFormat, policy.Evaluate(Change(exclude: true)));
        Assert.Equal(CaptureDecision.SensitiveFormat, policy.Evaluate(Change(can: 0)));
        Assert.Equal(CaptureDecision.Capture, policy.Evaluate(Change(can: 1)));
    }

    [Theory]
    [InlineData("1Password")]
    [InlineData("keepassxc")]
    [InlineData("Bitwarden.exe")]
    public void DefaultExcludedAppsAreSkipped(string owner) =>
        Assert.Equal(CaptureDecision.ExcludedApp, new ClipboardPolicy().Evaluate(Change(owner: owner)));

    [Fact]
    public void CustomExcludedAppsReplaceTheDefaults()
    {
        var policy = new ClipboardPolicy { ExcludedApps = ["secretapp.exe"] };
        Assert.Equal(CaptureDecision.ExcludedApp, policy.Evaluate(Change(owner: "SecretApp")));
        Assert.Equal(CaptureDecision.Capture, policy.Evaluate(Change(owner: "1Password")));
    }

    [Fact]
    public void PausedCapturesNothing() =>
        Assert.Equal(CaptureDecision.Paused, new ClipboardPolicy { Paused = true }.Evaluate(Change()));

    [Fact]
    public void OwnWritesAreIgnoredButLaterCopiesAreNot()
    {
        var policy = new ClipboardPolicy();
        policy.NoteOwnWrite(50);
        Assert.Equal(CaptureDecision.OwnWrite, policy.Evaluate(Change(seq: 49)));
        Assert.Equal(CaptureDecision.OwnWrite, policy.Evaluate(Change(seq: 50)));
        Assert.Equal(CaptureDecision.Capture, policy.Evaluate(Change(seq: 51)));
    }

    [Fact]
    public void UnknownOwnerIsStillCaptured() => Assert.Equal(CaptureDecision.Capture, new ClipboardPolicy().Evaluate(Change(owner: null)));
}
