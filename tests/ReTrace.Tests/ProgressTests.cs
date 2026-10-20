using ReTrace.Core;

namespace ReTrace.Tests;

internal static class ProgressTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }

        var progress = new LatestProgress<RecoveryProgress>();
        Assert(progress.Latest is null, "A new job does not inherit old progress.");
        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            for (int i = 0; i < 100_000; i++)
                progress.Report(new("scanning", i, 100_000));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert(context.Posts == 0, "Fast producers must not queue UI work for each progress event.");
        Assert(progress.Latest?.BytesDone == 99_999, "A slow consumer receives the latest snapshot.");

        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                progress.Report(new("worker", worker * 10_000 + i, 40_000));
                if (progress.Latest is not { Stage: "worker", BytesTotal: 40_000 })
                    throw new Exception("Concurrent readers must receive an intact published snapshot.");
            }
        })));
        var final = new RecoveryProgress("finished", 40_000, 40_000);
        progress.Report(final);
        Assert(ReferenceEquals(progress.Latest, final), "The terminal snapshot remains observable after all producers finish.");

        var nextJob = new LatestProgress<RecoveryProgress>();
        progress.Report(new("late_previous_job", 0, 0));
        Assert(nextJob.Latest is null, "Late progress from a finished job cannot leak into the next job.");
        bool rejected = false;
        try { nextJob.Report(null!); }
        catch (ArgumentNullException) { rejected = true; }
        Assert(rejected, "Null snapshots must not erase the progress contract.");
        return checks;
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts { get; private set; }
        public override void Post(SendOrPostCallback callback, object? state) => Posts++;
    }
}
