using System;
using System.Collections.Generic;
using Xunit;
using ForgeTekUpdatePackager.Helpers;

namespace ForgeTekUpdatePackager.Tests;

public class MacroEngineTests
{
    private static IReadOnlyDictionary<string, string> Vars()
        => MacroEngine.StandardVars("MyApp", "1.4.0", "Beta", "ForgeTek");

    [Fact]
    public void Resolve_SubstitutesStandardVariables()
    {
        var result = MacroEngine.Resolve("{AppName}_{Version}_{Channel}", Vars());
        Assert.Equal("MyApp_1.4.0_Beta", result);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var result = MacroEngine.Resolve("{appname}-{VERSION}", Vars());
        Assert.Equal("MyApp-1.4.0", result);
    }

    [Fact]
    public void Resolve_FormatsDateToken()
    {
        var when = new DateTime(2026, 6, 17, 9, 5, 0);
        Assert.Equal("2026-06-17", MacroEngine.Resolve("{Date}", Vars(), when));
        Assert.Equal("20260617", MacroEngine.Resolve("{Date:yyyyMMdd}", Vars(), when));
        Assert.Equal("2026", MacroEngine.Resolve("{Year}", Vars(), when));
    }

    [Fact]
    public void Resolve_LeavesUnknownTokensIntact()
    {
        var result = MacroEngine.Resolve("{AppName}-{Unknown}", Vars());
        Assert.Equal("MyApp-{Unknown}", result);
    }

    [Fact]
    public void Resolve_EmptyChannelProducesEmpty()
    {
        var vars = MacroEngine.StandardVars("MyApp", "1.0", channel: null, company: null);
        Assert.Equal("MyApp--", MacroEngine.Resolve("{AppName}-{Channel}-{Company}", vars));
    }

    [Fact]
    public void Resolve_NullOrEmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MacroEngine.Resolve(null, Vars()));
        Assert.Equal(string.Empty, MacroEngine.Resolve("", Vars()));
    }
}
