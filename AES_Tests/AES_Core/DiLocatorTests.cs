using System;
using System.Collections.Generic;
using Autofac;
using AES_Core.DI;
using Xunit;

namespace AES_Core.Tests;

public sealed class DiLocatorTests : IDisposable
{
    public DiLocatorTests()
    {
        ResetDiLocator();
    }

    public void Dispose()
    {
        ResetDiLocator();
    }

    [Fact]
    public void ApplyRegistrations_AppliesRegisteredCallbacks()
    {
        ResetDiLocator();
        DiLocator.AddRegistration(builder => builder.RegisterType<TestService>().As<ITestService>().SingleInstance());

        var builder = new ContainerBuilder();
        DiLocator.ApplyRegistrations(builder);
        using var container = builder.Build();

        var resolved = container.Resolve<ITestService>();
        Assert.NotNull(resolved);
        Assert.IsType<TestService>(resolved);
    }

    [Fact]
    public void ResolveViewModel_ReturnsNullWhenNotConfigured()
    {
        ResetDiLocator();

        var vm = DiLocator.ResolveViewModel<TestViewModel>();
        Assert.Null(vm);
    }

    [Fact]
    public void ResolveViewModel_ReturnsInstanceWhenConfigured()
    {
        ResetDiLocator();
        // Register a viewmodel in the container and configure it
        DiLocator.AddRegistration(builder => builder.RegisterType<TestViewModel>().AsSelf().SingleInstance());

        DiLocator.ConfigureContainer();

        var vm = DiLocator.ResolveViewModel<TestViewModel>();
        Assert.NotNull(vm);
        Assert.IsType<TestViewModel>(vm);
    }

    private static void ResetDiLocator()
    {
        // Clear registrations and existing scope
        var type = typeof(DiLocator);
        var registrationsField = type.GetField("Registrations", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var scopeField = type.GetField("_scope", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var builderActionField = type.GetField("_builderAction", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        if (registrationsField?.GetValue(null) is List<Action<ContainerBuilder>> registrations)
        {
            registrations.Clear();
        }

        if (scopeField != null)
        {
            var scope = scopeField.GetValue(null) as IDisposable;
            scope?.Dispose();
            scopeField.SetValue(null, null);
        }

        builderActionField?.SetValue(null, null);
    }

    private interface ITestService { }
    private sealed class TestService : ITestService { }

    private sealed class TestViewModel { }
}
