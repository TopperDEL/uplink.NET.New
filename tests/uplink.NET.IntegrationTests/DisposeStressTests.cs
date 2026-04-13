using uplink.NET.IntegrationTests.Infrastructure;
using uplink.NET.Models;
using uplink.NET.Services;

namespace uplink.NET.IntegrationTests;

/// <summary>
/// Targeted stress tests for the suspected Access / ProjectLease disposal
/// races behind intermittent CI "Test host process crashed" failures.
///
/// These exercise specific patterns that the regular integration tests only
/// hit by accident:
///   1. Rapid create-use-dispose Access cycles (the inter-test transition).
///   2. Many concurrent operations under one Access then mid-flight Dispose.
///   3. Tight loop of dispose-mid-transfer immediately followed by reopen.
///   4. Finalizer-driven cleanup (forces Dispose(false) on the GC thread).
///   5. Burst AcquireProjectLease racing with Dispose.
///
/// Tests assert "no host crash" — most also tolerate ObjectDisposedException
/// and other operation-level errors. The point is that the *process* must not
/// die.
///
/// Opt in with <c>UPLINK_RUN_STRESS=1</c> so they don't slow normal CI.
/// </summary>
public class DisposeStressTests
{
    /// <summary>
    /// Rapid create-use-dispose loop. Reproduces the inter-test handoff
    /// pattern (each test creates a fresh Access; the previous one's native
    /// resources may still be tearing down). If background uplink-c
    /// goroutines from the closed project touch freed memory, they'll do it
    /// while the next iteration is opening its own project.
    /// </summary>
    [StorjStressFact]
    public async Task RapidCreateUseDispose_DoesNotCrash()
    {
        const int iterations = 40;

        for (var i = 0; i < iterations; i++)
        {
            using var context = IntegrationTestEnvironment.CreateContext();
            var bucketService = new BucketService(context.Access);
            await bucketService.EnsureBucketAsync(context.BucketName);
            // Implicit Dispose at end-of-using; immediately a new context next iter.
        }
    }

    /// <summary>
    /// Many concurrent uploads under one Access, with Dispose called
    /// mid-flight. Stresses the lease refcount + deferred-dispose path under
    /// contention. All upload tasks must complete (success, failure, or
    /// cancellation are all acceptable) without a process crash.
    /// </summary>
    [StorjStressFact]
    public async Task ConcurrentOperations_DisposedMidFlight_DoNotCrash()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        await bucketService.EnsureBucketAsync(context.BucketName);
        var objectService = new ObjectService(context.Access);

        const int parallel = 8;
        var prefix = StorjTestHelper.CreatePrefix("stress-concurrent-dispose");
        var payload = IntegrationTestEnvironment.CreatePayload(64 * 1024);

        var tasks = new Task[parallel];
        for (var i = 0; i < parallel; i++)
        {
            var key = prefix + Guid.NewGuid().ToString("N") + ".bin";
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var upload = await objectService.UploadObjectAsync(
                        context.BucketName, key, payload, startImmediately: false);
                    var uploadTask = upload.StartUploadAsync();
                    if (uploadTask is not null)
                        await uploadTask;
                }
                catch (ObjectDisposedException) { /* expected when Dispose wins */ }
                catch (Exception)              { /* expected; must not crash process */ }
            });
        }

        // Brief delay so tasks have time to acquire leases and start native work.
        await Task.Delay(50);
        context.Access.Dispose();

        // Must not throw or crash; awaits all in-flight tasks to completion.
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Tight loop of dispose-mid-transfer followed immediately by a fresh
    /// Access opening a new project. Exercises the exact transition the CI
    /// crash pattern points at (last test in batch X dispose, first test in
    /// batch Y open).
    /// </summary>
    [StorjStressFact]
    public async Task DisposeMidTransfer_ThenReopen_Loop_DoesNotCrash()
    {
        // Use 6 MiB to mirror the existing dispose-mid-transfer integration tests.
        var payload = IntegrationTestEnvironment.CreatePayload(6 * 1024 * 1024);
        const int iterations = 12;

        for (var i = 0; i < iterations; i++)
        {
            using var context = IntegrationTestEnvironment.CreateContext();
            var bucketService = new BucketService(context.Access);
            await bucketService.EnsureBucketAsync(context.BucketName);
            var objectService = new ObjectService(context.Access);
            var key = StorjTestHelper.CreateObjectKey($"stress-dispose-loop-{i}");

            var upload = await objectService.UploadObjectAsync(
                context.BucketName, key, payload, startImmediately: false);
            var uploadTask = upload.StartUploadAsync();

            await StorjTestHelper.WaitUntilAsync(
                () => upload.BytesSent > 0 || upload.Completed,
                TimeSpan.FromSeconds(30),
                "upload did not start in time");

            context.Access.Dispose();

            try
            {
                if (uploadTask is not null)
                    await uploadTask;
            }
            catch
            {
                // Expected — upload may fault when its project is yanked.
            }

            // No cleanup of the partial object — best-effort only. Next iteration
            // immediately opens a fresh Access. This is the suspected crash window.
        }
    }

    /// <summary>
    /// Leak Access references and force GC so the finalizer must run
    /// Dispose(false) on the GC thread. Surfaces races on the finalizer
    /// path that the standard <c>using</c> pattern hides.
    /// </summary>
    [StorjStressFact]
    public async Task FinalizerDriven_Cleanup_DoesNotCrash()
    {
        const int iterations = 10;

        for (var i = 0; i < iterations; i++)
        {
            await OpenAndLeakOneAccess();

            // Force collection so the Access finalizer has to run now, not
            // at some random later time inside another test.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        static async Task OpenAndLeakOneAccess()
        {
            // Local scope so the Access becomes unreachable on return.
            var context = IntegrationTestEnvironment.CreateContext();
            var bucketService = new BucketService(context.Access);
            await bucketService.EnsureBucketAsync(context.BucketName);
            // Deliberately do NOT dispose context. Finalizers handle cleanup.
        }
    }

    /// <summary>
    /// Many threads acquire and release ProjectLeases concurrently while
    /// another thread races to call Dispose on the same Access. Stresses
    /// the <c>_lifetimeSync</c> contention path inside Access.Dispose /
    /// AcquireProjectLease / ReleaseProjectLease.
    /// </summary>
    [StorjStressFact]
    public async Task BurstAcquireProjectLease_RaceWithDispose_DoesNotCrash()
    {
        using var context = IntegrationTestEnvironment.CreateContext();
        var bucketService = new BucketService(context.Access);
        await bucketService.EnsureBucketAsync(context.BucketName);

        const int parallel = 16;
        var startGate = new TaskCompletionSource();
        var tasks = new Task[parallel];

        for (var i = 0; i < parallel; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await startGate.Task;
                for (var j = 0; j < 20; j++)
                {
                    try
                    {
                        // EnsureBucketAsync internally calls AcquireProjectLease →
                        // OpenProjectHandle (network dial) → release.
                        await bucketService.EnsureBucketAsync(context.BucketName);
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (Exception)              { return; }
                }
            });
        }

        startGate.SetResult();
        // Race: dispose the Access while the workers are spinning lease cycles.
        await Task.Delay(75);
        context.Access.Dispose();

        await Task.WhenAll(tasks);
    }
}
