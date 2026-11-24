using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Pages;

namespace YaesuWebControl.Tests;

/// <summary>
/// Every field on Application Setup must be bound, or Save quietly writes its
/// default. The Docker note (#389) put InContainer between [BindProperty] and
/// ShowApp1Button, so the WSJT-X Show switch always saved as off (#439).
/// </summary>
public class ApplicationSetupBindingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Each_app_card_is_bound(int n)
    {
        foreach (var name in new[] { $"ShowApp{n}Button", $"App{n}Name", $"App{n}CommandLine" })
            Assert.True(IsBound(name), $"{name} has no [BindProperty], so Save ignores it.");
    }

    [Fact]
    public void InContainer_is_not_bound()
        => Assert.False(IsBound(nameof(ApplicationSetupModel.InContainer)));

    private static bool IsBound(string property)
        => typeof(ApplicationSetupModel).GetProperty(property)!.GetCustomAttribute<BindPropertyAttribute>() is not null;
}
