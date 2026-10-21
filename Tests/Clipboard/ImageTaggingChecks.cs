using PersonalTools;
internal static class ImageTaggingChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var store = new ClipboardStore(new ClipboardSettings { ArchivePath = Path.Combine(root, "tag-queue"), AutoTagImages = true });
        string staging = Path.Combine(root, "tag-staging");
        var entry = (await store.SaveAsync(new("Image", "fixture", Image: [1, 3, 5])))!;
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var tags = new ClipImageTags(["빨간 원"], "도형", "TAG-CHECK", ClipboardSearch.Model, DateTimeOffset.UtcNow);
        await using (var worker = new ClipboardImageTagger(store, async (path, token) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult(path); await release.Task.WaitAsync(token); return tags;
        }, _ => { }, staging))
        {
            worker.Enqueue(entry); worker.Enqueue(entry);
            string path = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await File.ReadAllBytesAsync(path)).SequenceEqual(new byte[] { 1, 3, 5 }), "tag worker receives only staged image bytes");
            release.SetResult();
            await Until(() => store.Entries.Single().ImageTagState == "complete");
            check(calls == 1, "duplicate image jobs are coalesced");
            check(store.Entries.Single().ImageTags?.Text == "TAG-CHECK", "background image analysis persists results");
            check((await store.FindAsync("빨간", default)).Single().Id == entry.Id && (await store.FindAsync("tag-check", default)).Length == 1, "image tags and recognized text are searchable locally");
            var reloaded = new ClipboardStore(store.Settings); await reloaded.LoadAsync();
            check(reloaded.Entries.Single().ImageTags?.Tags.SequenceEqual(["빨간 원"]) == true && reloaded.Entries.Single().ImageTagState == "complete", "image tags survive restart");
            await Until(() => !File.Exists(path));
            check(!Directory.EnumerateFileSystemEntries(staging).Any(), "temporary image cleaned after successful analysis");
        }
        var deleted = (await store.SaveAsync(new("Image", "fixture", Image: [2, 4, 6])))!;
        var startedDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var worker = new ClipboardImageTagger(store, async (_, token) =>
        {
            startedDelete.SetResult(); await releaseDelete.Task.WaitAsync(token); return tags;
        }, _ => { }, staging))
        {
            worker.Enqueue(deleted); await startedDelete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.DeleteAsync(deleted); releaseDelete.SetResult();
            await Until(() => !Directory.EnumerateFileSystemEntries(staging).Any());
            check(!store.Entries.Any(x => x.Id == deleted.Id), "late image result cannot resurrect a deleted record");
        }
        var pending = (await store.SaveAsync(new("Image", "fixture", Image: [7, 8, 9])))!;
        var startedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelWorker = new ClipboardImageTagger(store, async (_, token) =>
        {
            startedCancel.SetResult(); await Task.Delay(Timeout.Infinite, token); return tags;
        }, _ => { }, staging);
        cancelWorker.Enqueue(pending); await startedCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelWorker.DisposeAsync();
        check(!Directory.EnumerateFileSystemEntries(staging).Any(), "shutdown cancels analysis and cleans staged image");
        check(store.Entries.Single(x => x.Id == pending.Id).ImageTagState == "pending", "interrupted image remains pending for restart");
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
