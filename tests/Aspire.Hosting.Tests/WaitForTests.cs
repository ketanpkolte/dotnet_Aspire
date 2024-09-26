// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.Tests;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Hosting.Tests;

public class WaitForTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void ResourceCannotWaitForItself()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new CustomResource("test"));

        var waitForEx = Assert.Throws<DistributedApplicationException>(() =>
        {
            resource.WaitFor(resource);
        });

        Assert.Equal("The 'test' resource cannot wait for itself.", waitForEx.Message);

        var waitForCompletionEx = Assert.Throws<DistributedApplicationException>(() =>
        {
            resource.WaitForCompletion(resource);
        });

        Assert.Equal("The 'test' resource cannot wait for itself.", waitForCompletionEx.Message);
    }

    [Fact]
    public void ResourceCannotWaitForItsParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parentResourceBuilder = builder.AddResource(new CustomResource("parent"));
        var childResourceBuilder = builder.AddResource(new CustomChildResource("child", parentResourceBuilder.Resource));

        var waitForEx = Assert.Throws<DistributedApplicationException>(() =>
        {
            childResourceBuilder.WaitFor(parentResourceBuilder);
        });

        Assert.Equal("The 'child' resource cannot wait for its parent 'parent'.", waitForEx.Message);

        var waitForCompletionEx = Assert.Throws<DistributedApplicationException>(() =>
        {
            childResourceBuilder.WaitForCompletion(parentResourceBuilder);
        });

        Assert.Equal("The 'child' resource cannot wait for its parent 'parent'.", waitForCompletionEx.Message);
    }

    [Fact]
    [RequiresDocker]
    public async Task EnsureDependentResourceMovesIntoWaitingState()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var dependency = builder.AddResource(new CustomResource("test"));
        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitFor(dependency);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Running state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, "Waiting", waitingStateCts.Token);

        // Now that we know we successfully entered the Waiting state, we can swap
        // the dependency into a running state which will unblock startup and
        // we can continue executing.
        await rns.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Running
        });

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task WaitForCompletionWaitsForTerminalStateOfDependencyResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var dependency = builder.AddResource(new CustomResource("test"));
        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitForCompletion(dependency);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Finished state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.Waiting, waitingStateCts.Token);

        // Now that we know we successfully entered the Waiting state, we can swap
        // the dependency into a running state which will unblock startup and
        // we can continue executing.
        await rns.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Finished,
            ExitCode = 0
        });

        // This time we want to wait for Nginx to move into a Running state to verify that
        // it successfully started after we moved the dependency resource into the Finished, but
        // we need to give it more time since we have to download the image in CI.
        var runningStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.Running, runningStateCts.Token);

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task WaitForThrowsIfResourceMovesToTerminalStateBeforeRunning()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var dependency = builder.AddResource(new CustomResource("test"));
        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitFor(dependency);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Finished state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, "Waiting", waitingStateCts.Token);

        // Now that we know we successfully entered the Waiting state, we can swap
        // the dependency into a running state which will unblock startup and
        // we can continue executing.
        await rns.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Finished,
            ExitCode = 0
        });

        // This time we want to wait for Nginx to move into a Running state to verify that
        // it successfully started after we moved the dependency resource into the Finished, but
        // we need to give it more time since we have to download the image in CI.
        var runningStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.FailedToStart, runningStateCts.Token);

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task EnsureDependencyResourceThatReturnsNonMatchingExitCodeResultsInDependentResourceFailingToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        
        var dependency = builder.AddResource(new CustomResource("test"));
        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitForCompletion(dependency, exitCode: 2);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Finished state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.Waiting, waitingStateCts.Token);

        // Now that we know we successfully entered the Waiting state, we can swap
        // the dependency into a finished state which will unblock startup and
        // we can continue executing.
        await rns.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Finished,
            ExitCode = 3 // Exit code does not match expected exit code above intentionally.
        });

        // This time we want to wait for Nginx to move into a FailedToStart state to verify that
        // it didn't start if the dependency resource didn't finish with the correct exit code.
        var runningStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.FailedToStart, runningStateCts.Token);

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task DependencyWithGreaterThan1ReplicaAnnotationCausesDependentResourceToFailToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var dependency = builder.AddResource(new CustomResource("test"))
                                .WithAnnotation(new ReplicaAnnotation(2));

        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitForCompletion(dependency);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Finished state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, "FailedToStart", waitingStateCts.Token);

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    public async Task ChildResourceGetsParentHealthChecks()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var healthCheck = () => HealthCheckResult.Healthy();

        builder.Services.AddHealthChecks().AddCheck("parent_check", healthCheck);

        var parent = builder.AddResource(new CustomResource("parent"))
                            .WithHealthCheck("parent_check");

        var child = builder.AddResource(new CustomChildResource("child", parent.Resource));

        var waiter = builder.AddResource(new CustomResource("waiter"))
                            .WaitFor(child);

        var assertBlock = new TaskCompletionSource();

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(child.Resource, (@event, ct) =>
        {
            // By the time this runs it means that the health check should have already
            // been copied from the parent to the child. Doing this so we don't have to
            // slap an sleep in the test to make execution as fast as possible.
            assertBlock.SetResult();
            return Task.CompletedTask;
        });

        using var app = builder.Build();

        var pendingStart = app.StartAsync();

        Assert.True(child.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var childHealthCheckAnnotations));
        Assert.Collection(childHealthCheckAnnotations, a => Assert.Equal("parent_check", a.Key));

        await pendingStart;
        await app.StopAsync();
    }

    [Fact]
    public async Task ChildResourceFollowsParentStatus()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var desiredStatus = HealthCheckResult.Unhealthy();
        var healthCheck = () => desiredStatus;
        builder.Services.AddHealthChecks().AddCheck("parent_check", healthCheck);

        var parent = builder.AddResource(new CustomResource("parent"))
                            .WithHealthCheck("parent_check");

        var child = builder.AddResource(new CustomChildResource("child", parent.Resource));

        var waiter = builder.AddResource(new CustomResource("waiter"))
                            .WaitFor(child);

        using var app = builder.Build();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        builder.Eventing.Subscribe<AfterEndpointsAllocatedEvent>(async (@event, ct) =>
        {
            // Make the parent and child resources go into a running state.
            await rns.PublishUpdateAsync(parent.Resource, s => s with { State = KnownResourceStates.Running });
            await rns.PublishUpdateAsync(child.Resource, s => s with { State = KnownResourceStates.Running });
        });

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // More than enough time, should take just a few seconds
                                                                         // for the first health checks to run.

        var pendingStart = app.StartAsync(cts.Token);

        await rns.WaitForResourceAsync(parent.Resource.Name, (re) => re.Snapshot.HealthStatus == HealthStatus.Unhealthy, cts.Token);
        await rns.WaitForResourceAsync(child.Resource.Name, (re) => re.Snapshot.HealthStatus == HealthStatus.Unhealthy, cts.Token);

        desiredStatus = HealthCheckResult.Healthy();

        await rns.WaitForResourceAsync(parent.Resource.Name, (re) => re.Snapshot.HealthStatus == HealthStatus.Healthy, cts.Token);
        await rns.WaitForResourceAsync(child.Resource.Name, (re) => re.Snapshot.HealthStatus == HealthStatus.Healthy, cts.Token);

        await pendingStart;
        await app.StopAsync();
    }

    [Fact]
    [RequiresDocker]
    public async Task WaitForCompletionSucceedsIfDependentResourceEntersTerminalStateWithoutAnExitCode()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var dependency = builder.AddResource(new CustomResource("test"));

        var nginx = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                           .WithReference(dependency)
                           .WaitForCompletion(dependency);

        using var app = builder.Build();

        // StartAsync will currently block until the dependency resource moves
        // into a Finished state, so rather than awaiting it we'll hold onto the
        // task so we can inspect the state of the Nginx resource which should
        // be in a waiting state if everything is working correctly.
        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var startTask = app.StartAsync(startupCts.Token);

        // We don't want to wait forever for Nginx to move into a waiting state,
        // it should be super quick, but we'll allow 60 seconds just in case the
        // CI machine is chugging (also useful when collecting code coverage).
        var waitingStateCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.Waiting, waitingStateCts.Token);

        // Now that we know we successfully entered the Waiting state, we can end the dependency
        await rns.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Finished
        });

        await rns.WaitForResourceAsync(nginx.Resource.Name, KnownResourceStates.Running, waitingStateCts.Token);

        await startTask;

        await app.StopAsync();
    }

    [Fact]
    public void WaitForOnChildResourceAddsWaitAnnotationPointingToParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parentResource = builder.AddResource(new CustomResource("parent"));
        var childResource = builder.AddResource(new CustomChildResource("child", parentResource.Resource));
        var containerResource = builder.AddContainer("container", "image", "tag")
                                       .WaitFor(childResource);

        Assert.True(containerResource.Resource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations));

        Assert.Collection(
            waitAnnotations,
            a => Assert.Equal(a.Resource, parentResource.Resource),
            a => Assert.Equal(a.Resource, childResource.Resource)
            );
    }

    private sealed class CustomChildResource(string name, CustomResource parent) : Resource(name), IResourceWithParent<CustomResource>
    {
        public CustomResource Parent => parent;
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"foo");
    }
}
