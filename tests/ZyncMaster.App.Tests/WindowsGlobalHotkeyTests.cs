using System;
using System.Collections.Generic;
using FluentAssertions;
using ZyncMaster.App.Platform.Clipboard;
using Xunit;

namespace ZyncMaster.App.Tests;

// Exercises the parser (TryParse) and the fallback selection logic (SelectHotkey) WITHOUT calling the
// real Win32 RegisterHotKey — registration is faked via a delegate so we can simulate "already taken"
// (failure) and success deterministically. The thread-marshalling and actual P/Invoke remain the
// untested process boundary; this covers everything that has real branching logic.
public sealed class WindowsGlobalHotkeyTests
{
    // ---- TryParse ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Win+Q")]
    [InlineData("Ctrl+Shift+Q")]
    [InlineData("Ctrl+Alt+V")]
    [InlineData("Alt+Shift+F5")]
    [InlineData("ctrl + win + q")]
    public void TryParse_AcceptsValidCombos(string hotkey)
    {
        WindowsGlobalHotkey.TryParse(hotkey, out var modifiers, out var vk).Should().BeTrue();
        modifiers.Should().NotBe(0u);
        vk.Should().NotBe(0u);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q")]            // no modifier
    [InlineData("Ctrl+Shift")]  // no key
    [InlineData("Ctrl+Win+??")] // unknown key token
    public void TryParse_RejectsInvalidCombos(string hotkey)
    {
        WindowsGlobalHotkey.TryParse(hotkey, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_MapsLetterToVirtualKey()
    {
        WindowsGlobalHotkey.TryParse("Ctrl+Q", out var modifiers, out var vk).Should().BeTrue();
        modifiers.Should().Be(Win32Modifiers.Control);
        vk.Should().Be((uint)'Q');
    }

    // ---- SelectHotkey: fallback ordering ----------------------------------------------------

    [Fact]
    public void SelectHotkey_RegistersPrimary_WhenItSucceeds()
    {
        var attempted = new List<uint>();
        var result = WindowsGlobalHotkey.SelectHotkey(
            "Ctrl+Win+Z",
            WindowsGlobalHotkey.FallbackHotkeys,
            (mods, vk) =>
            {
                attempted.Add(vk);
                return new WindowsGlobalHotkey.RegisterAttempt(true, 0);
            });

        result.RegisteredHotkey.Should().Be("Ctrl+Win+Z");
        result.LastWin32Error.Should().Be(0);
        attempted.Should().HaveCount(1); // never reached the fallbacks
    }

    [Fact]
    public void SelectHotkey_FallsBackToFirstAvailable_WhenPrimaryTaken()
    {
        // Primary fails (1409 already-registered), first fallback succeeds.
        var calls = 0;
        var result = WindowsGlobalHotkey.SelectHotkey(
            "Ctrl+Win+Z",
            WindowsGlobalHotkey.FallbackHotkeys,
            (mods, vk) =>
            {
                calls++;
                return calls == 1
                    ? new WindowsGlobalHotkey.RegisterAttempt(false, 1409)
                    : new WindowsGlobalHotkey.RegisterAttempt(true, 0);
            });

        result.RegisteredHotkey.Should().Be(WindowsGlobalHotkey.FallbackHotkeys[0]);
        calls.Should().Be(2);
    }

    [Fact]
    public void SelectHotkey_WalksFurtherDownTheList_WhenEarlierFallbacksAlsoTaken()
    {
        // Primary + first two fallbacks fail; the third fallback registers.
        var succeedOn = WindowsGlobalHotkey.FallbackHotkeys[2];
        var result = WindowsGlobalHotkey.SelectHotkey(
            "Ctrl+Win+Z",
            WindowsGlobalHotkey.FallbackHotkeys,
            (mods, vk) =>
            {
                WindowsGlobalHotkey.TryParse(succeedOn, out var wantMods, out var wantVk);
                var ok = mods == wantMods && vk == wantVk;
                return new WindowsGlobalHotkey.RegisterAttempt(ok, ok ? 0 : 1409);
            });

        result.RegisteredHotkey.Should().Be(succeedOn);
    }

    [Fact]
    public void SelectHotkey_ReturnsNullAndLastError_WhenEverythingTaken()
    {
        var result = WindowsGlobalHotkey.SelectHotkey(
            "Ctrl+Win+Z",
            WindowsGlobalHotkey.FallbackHotkeys,
            (mods, vk) => new WindowsGlobalHotkey.RegisterAttempt(false, 1409));

        result.RegisteredHotkey.Should().BeNull();
        result.LastWin32Error.Should().Be(1409);
    }

    [Fact]
    public void SelectHotkey_SkipsUnparseablePrimary_AndUsesFallback()
    {
        // Garbage primary never reaches the attempt delegate; first fallback wins.
        var attemptedCombos = new List<(uint mods, uint vk)>();
        var result = WindowsGlobalHotkey.SelectHotkey(
            "this is not a hotkey",
            WindowsGlobalHotkey.FallbackHotkeys,
            (mods, vk) =>
            {
                attemptedCombos.Add((mods, vk));
                return new WindowsGlobalHotkey.RegisterAttempt(true, 0);
            });

        result.RegisteredHotkey.Should().Be(WindowsGlobalHotkey.FallbackHotkeys[0]);
        attemptedCombos.Should().HaveCount(1); // unparseable primary was skipped, not attempted
    }

    [Fact]
    public void SelectHotkey_DoesNotAttemptTheSameComboTwice()
    {
        // Primary equals the first fallback (case-insensitive); it must be tried once, not twice.
        var attempts = 0;
        var result = WindowsGlobalHotkey.SelectHotkey(
            "ctrl+win+q",
            WindowsGlobalHotkey.FallbackHotkeys, // contains "Ctrl+Win+Q"
            (mods, vk) =>
            {
                attempts++;
                return new WindowsGlobalHotkey.RegisterAttempt(false, 1409);
            });

        result.RegisteredHotkey.Should().BeNull();
        // 4 distinct combos total (primary dedupes against the identical fallback entry).
        attempts.Should().Be(WindowsGlobalHotkey.FallbackHotkeys.Length);
    }

    [Fact]
    public void SelectHotkey_ThrowsOnNullArguments()
    {
        Func<uint, uint, WindowsGlobalHotkey.RegisterAttempt> ok =
            (_, _) => new WindowsGlobalHotkey.RegisterAttempt(true, 0);

        Assert.Throws<ArgumentNullException>(() =>
            WindowsGlobalHotkey.SelectHotkey("Ctrl+Q", null!, ok));
        Assert.Throws<ArgumentNullException>(() =>
            WindowsGlobalHotkey.SelectHotkey("Ctrl+Q", WindowsGlobalHotkey.FallbackHotkeys, null!));
    }

    // Local copy of the modifier flags so the test asserts on concrete values without exposing the
    // internal Win32 P/Invoke surface.
    private static class Win32Modifiers
    {
        public const uint Alt = 0x0001;
        public const uint Control = 0x0002;
        public const uint Shift = 0x0004;
        public const uint Win = 0x0008;
    }
}
