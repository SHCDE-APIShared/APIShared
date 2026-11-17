using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class WorkshopApplyCoordinatorTests
{
    private static Task<WorkshopOperation> Failed(WorkshopOperation operation)
    {
        operation.Set(WorkshopOperationState.Failed, "handoff failed");
        return Task.FromResult(operation);
    }

    [TestMethod]
    public async Task DuplicateAttemptDoesNotOverwriteOperationOrBlockNewPlan()
    {
        var coordinator = new WorkshopApplyCoordinator();
        var first = await coordinator.ExecuteAsync("first", Failed);
        bool duplicateRan = false;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.ExecuteAsync("first", operation => {
            duplicateRan = true; return Failed(operation);
        }));
        Assert.IsFalse(duplicateRan);
        Assert.AreEqual(WorkshopOperationState.Failed, first.State);
        Assert.AreEqual("handoff failed", first.Diagnostic);
        Assert.AreEqual("next", (await coordinator.ExecuteAsync("next", Failed)).OperationId);
    }

    [TestMethod]
    public async Task RegistrationAndAsynchronousStageErrorsReleaseGate()
    {
        var coordinator = new WorkshopApplyCoordinator();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => coordinator.ExecuteAsync(null!, Failed));
        var error = new IOException("stage failed");
        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<IOException>(() =>
            coordinator.ExecuteAsync("stage", _ => Task.FromException<WorkshopOperation>(error))));
        Assert.AreEqual("after-stage", (await coordinator.ExecuteAsync("after-stage", Failed)).OperationId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync("dispatch", _ =>
            throw new InvalidOperationException("dispatcher unavailable")));
        Assert.AreEqual("after-dispatch", (await coordinator.ExecuteAsync("after-dispatch", Failed)).OperationId);
    }

    [TestMethod]
    public async Task CompetingAttemptCannotReleaseGateAndHandoffKeepsIt()
    {
        var coordinator = new WorkshopApplyCoordinator();
        var completion = new TaskCompletionSource<WorkshopOperation>();
        WorkshopOperation running = null;
        Task<WorkshopOperation> first = coordinator.ExecuteAsync("running", operation => {
            running = operation; return completion.Task;
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync("competitor", Failed));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync("another", Failed));
        running!.Set(WorkshopOperationState.AwaitingRestart, "handoff complete");
        completion.SetResult(running);
        Assert.AreSame(running, await first);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync("after-handoff", Failed));
    }

    [TestMethod]
    public async Task FailedHandoffReleasesGateAfterCompetingAttempt()
    {
        var coordinator = new WorkshopApplyCoordinator();
        var completion = new TaskCompletionSource<WorkshopOperation>();
        WorkshopOperation running = null;
        Task<WorkshopOperation> first = coordinator.ExecuteAsync("running", operation => {
            running = operation; return completion.Task;
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ExecuteAsync("competitor", Failed));
        running!.Set(WorkshopOperationState.Failed, "launch failed");
        completion.SetResult(running);
        Assert.AreSame(running, await first);
        Assert.AreEqual("next", (await coordinator.ExecuteAsync("next", Failed)).OperationId);
    }
}
