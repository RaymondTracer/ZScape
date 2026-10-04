using System.Text.Json;
using ZScape.Services;

internal static class DomainThreadChecks
{
    public static void Run()
    {
        CheckIdentityAndSnapshots();
        CheckEditorMergeAndAdaptiveUpdates();
        CheckBudgetsAndValidation();
        CheckConcurrentPersistence();
    }

    private static void CheckIdentityAndSnapshots()
    {
        var originalValue = new DomainSettings { MaxThreads = 16, MinSegmentSizeKb = 512 };
        var backing = new Dictionary<string, DomainSettings>
        {
            ["  HTTPS://EXAMPLE.COM/wads "] = new DomainSettings { MaxThreads = 8 },
            ["example.com"] = originalValue,
            ["  "] = new DomainSettings { MaxThreads = 99 }
        };
        var saves = 0;
        var config = new DomainThreadConfig(backing, () => new AppSettings(), () => saves++);
        RegressionAssert.That(config.GetAllSettings().Count == 1 &&
            config.GetThreadCount(" https://EXAMPLE.com/other ") == 16,
            "Saved URLs, host casing and whitespace share one domain identity; the last duplicate wins");
        RegressionAssert.That(saves == 0, "Reading and normalizing domain settings does not save them");

        var one = config.GetSettings("example.com")!;
        var all = config.GetAllSettings();
        one.MaxThreads = 100;
        all["example.com"].MaxThreads = 200;
        originalValue.MaxThreads = 300;
        RegressionAssert.That(config.GetThreadCount("example.com") == 16,
            "Returned domain settings and constructor input values cannot mutate owned state");
    }

    private static void CheckEditorMergeAndAdaptiveUpdates()
    {
        var config = new DomainThreadConfig(new Dictionary<string, DomainSettings>
        {
            ["mirror.example"] = new DomainSettings { MaxThreads = 4, MinSegmentSizeKb = 256 },
            ["removed.example"] = new DomainSettings { MaxThreads = 8 }
        }, () => new AppSettings(), () => { });
        var baseline = config.GetAllSettings();
        var edited = config.GetAllSettings().ToDictionary(pair => pair.Key, pair => pair.Value);
        config.UpdateThreadCount("https://MIRROR.example/wads", 32);
        config.UpdateThreadCount("learned.example", 16);

        edited["mirror.example"].MinSegmentSizeKb = 1024;
        edited.Remove("removed.example");
        config.ApplyConfiguredChanges(edited, baseline);
        RegressionAssert.That(config.GetSettings("mirror.example") is { MaxThreads: 32, MinSegmentSizeKb: 1024 },
            "Editing a segment size preserves thread capacity learned while the settings dialog was open");
        RegressionAssert.That(config.GetThreadCount("learned.example") == 16 &&
            config.GetThreadCount("removed.example") is null,
            "Explicit removals apply without deleting domains learned after the editor snapshot");

        baseline = config.GetAllSettings();
        edited = config.GetAllSettings().ToDictionary(pair => pair.Key, pair => pair.Value);
        config.UpdateThreadCount("mirror.example", 64);
        edited["mirror.example"].MaxThreads = 12;
        edited["mirror.example"].AdaptiveLearning = false;
        config.ApplyConfiguredChanges(edited, baseline);
        config.UpdateThreadCount("mirror.example", 128);
        RegressionAssert.That(config.ReduceThreadCount("mirror.example", 12) == 12 &&
            config.GetThreadCount("mirror.example") == 12,
            "Explicit thread edits win and disabling adaptive learning prevents growth and backoff");

        baseline = config.GetAllSettings();
        edited = config.GetAllSettings().ToDictionary(pair => pair.Key, pair => pair.Value);
        edited["mirror.example"].AdaptiveLearning = true;
        config.ApplyConfiguredChanges(edited, baseline);
        config.UpdateThreadCount("mirror.example", 32);
        RegressionAssert.That(config.ReduceThreadCount("mirror.example", 32) == 16,
            "Re-enabling adaptive learning retains successful growth and failure backoff");

        baseline = config.GetAllSettings();
        edited = config.GetAllSettings().ToDictionary(pair => pair.Key, pair => pair.Value);
        config.ReduceThreadCount("mirror.example", 16);
        config.ApplyConfiguredChanges(edited, baseline);
        RegressionAssert.That(config.GetThreadCount("mirror.example") == 8,
            "Saving an untouched domain preserves adaptive backoff that occurred after the editor snapshot");

        var added = new DomainSettings { MaxThreads = 48, MinSegmentSizeKb = 512 };
        baseline = config.GetAllSettings();
        edited = config.GetAllSettings().ToDictionary(pair => pair.Key, pair => pair.Value);
        edited[" HTTPS://ADDED.example/wads "] = added;
        config.ApplyConfiguredChanges(edited, baseline);
        added.MaxThreads = 999;
        RegressionAssert.That(config.GetThreadCount("added.example") == 48,
            "Explicit domain additions use normalized identity and detach the editor's settings values");
    }

    private static void CheckBudgetsAndValidation()
    {
        var global = new AppSettings { MaxThreadsPerFile = 4, DefaultMinSegmentSizeKb = 768 };
        var config = new DomainThreadConfig(new Dictionary<string, DomainSettings>(), () => global, () => { });
        var fallback = config.GetEffectiveDomainBudgetSettings("unknown.example");
        RegressionAssert.That(fallback is { MaxThreads: 32, MinSegmentSizeKb: 768, ShouldProbe: true, AdaptiveLearning: true },
            "Unknown domains retain the automatic budget, global segment size and adaptive defaults");

        config.ApplyConfiguredChanges(new Dictionary<string, DomainSettings>
        {
            ["mirror.example"] = new DomainSettings { MaxThreads = 128, MinSegmentSizeKb = 0 },
            ["small.example"] = new DomainSettings { MaxThreads = 2 },
            ["invalid.example"] = new DomainSettings { MaxThreads = -3, MinSegmentSizeKb = 1 },
            ["large.example"] = new DomainSettings { MaxThreads = 5000, MinSegmentSizeKb = 9999 }
        }, config.GetAllSettings());
        RegressionAssert.That(config.GetEffectiveDomainBudgetSettings("mirror.example").MaxThreads == 128 &&
            config.GetEffectiveThreadSettings("mirror.example").MaxThreads == 4 &&
            config.GetEffectiveThreadSettings("mirror.example").MinSegmentSizeKb == 768,
            "The global per-file cap leaves the shared domain budget intact and zero segment size uses the global value");
        RegressionAssert.That(config.GetEffectiveDomainBudgetSettings("small.example").ShouldProbe,
            "Known small adaptive budgets retain probing behavior");
        RegressionAssert.That(config.GetSettings("invalid.example") is { MaxThreads: 0, MinSegmentSizeKb: 64 } &&
            config.GetSettings("large.example") is { MaxThreads: 5000, MinSegmentSizeKb: 4096 },
            "Owner validation matches existing editor numeric limits without imposing a thread-cap ceiling");
        config.UpdateThreadCount("  ", 8);
        config.ReduceThreadCount("  ", 8);
        RegressionAssert.That(!config.GetAllSettings().ContainsKey(string.Empty),
            "Empty domain names cannot create runtime settings entries");
    }

    private static void CheckConcurrentPersistence()
    {
        var backing = new Dictionary<string, DomainSettings>();
        var saves = 0;
        var activeSaves = 0;
        var overlappingSave = 0;
        var config = new DomainThreadConfig(backing, () => new AppSettings(), () =>
        {
            if (Interlocked.Increment(ref activeSaves) != 1)
                Interlocked.Exchange(ref overlappingSave, 1);
            try
            {
                _ = JsonSerializer.Serialize(backing);
                Interlocked.Increment(ref saves);
            }
            finally
            {
                Interlocked.Decrement(ref activeSaves);
            }
        });
        var emptyBaseline = config.GetAllSettings();
        Parallel.For(0, 128, index =>
        {
            config.UpdateThreadCount($"learned-{index}.example", 16);
            config.ApplyConfiguredChanges(new Dictionary<string, DomainSettings>
            {
                [$"edited-{index}.example"] = new DomainSettings { MaxThreads = 24 }
            }, emptyBaseline);
            _ = config.GetAllSettings();
        });
        RegressionAssert.That(config.GetAllSettings().Count == 256 && saves == 256 && overlappingSave == 0,
            "Concurrent editor additions, adaptive updates, snapshots and persistence retain every domain under one lock");
    }
}
