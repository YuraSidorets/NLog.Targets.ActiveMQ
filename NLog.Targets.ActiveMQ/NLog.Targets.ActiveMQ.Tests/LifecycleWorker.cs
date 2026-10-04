using FluentAssertions;

namespace NLog.Targets.ActiveMQ.Tests;

internal sealed class LifecycleWorker
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public LifecycleWorker(Action action)
    {
        _thread = new Thread(() =>
        {
            try
            {
                action();
                _completion.SetResult();
            }
            catch (Exception exception)
            {
                _completion.SetException(exception);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public void WaitUntilBlocked()
    {
        SpinWait.SpinUntil(() => Completion.IsCompleted || IsWaiting, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the worker should reach the contended lifecycle operation");
        Completion.IsCompleted.Should().BeFalse("the operation must wait for the in-flight send");
        IsWaiting.Should().BeTrue();
    }

    private bool IsWaiting => (_thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
}
