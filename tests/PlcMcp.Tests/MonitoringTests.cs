using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;
using PlcMcp.Runtime;
using PlcMcp.Runtime.Clients;
using PlcMcp.Runtime.Monitoring;

namespace PlcMcp.Tests;

public class MonitoringTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validation_RejectsInvalidTargetId(string invalidTarget)
    {
        var request = new MonitoringRequest
        {
            TargetId = invalidTarget,
            Tags = ["Tag1"]
        };

        var options = new MonitoringOptions();
        var ex = Assert.Throws<ArgumentException>(() => request.Validate(options));
        Assert.Equal("TargetId", ex.ParamName);
    }

    [Fact]
    public void Validation_RejectsEmptyOrNullTags()
    {
        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = []
        };

        var options = new MonitoringOptions();
        var ex = Assert.Throws<ArgumentException>(() => request.Validate(options));
        Assert.Equal("Tags", ex.ParamName);
    }

    [Fact]
    public void Validation_RejectsMoreThan128Tags()
    {
        var tags = Enumerable.Range(1, 129).Select(i => $"Tag{i}").ToArray();
        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = tags
        };

        var options = new MonitoringOptions();
        var ex = Assert.Throws<ArgumentException>(() => request.Validate(options));
        Assert.Equal("Tags", ex.ParamName);
    }

    [Fact]
    public void Validation_RejectsIntervalOutOfRange()
    {
        var options = new MonitoringOptions();

        var requestBelow = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            Interval = TimeSpan.FromMilliseconds(20) // below 50ms default min
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestBelow.Validate(options));

        var requestAbove = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            Interval = TimeSpan.FromMinutes(2) // above 1m default max
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestAbove.Validate(options));
    }

    [Fact]
    public void Validation_RejectsDurationOutOfRange()
    {
        var options = new MonitoringOptions();

        var requestZero = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            Duration = TimeSpan.Zero
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestZero.Validate(options));

        var requestExcessive = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            Duration = TimeSpan.FromMinutes(20) // above 10m max
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestExcessive.Validate(options));
    }

    [Fact]
    public void Validation_RejectsMaxSamplesOutOfRange()
    {
        var options = new MonitoringOptions();

        var requestZero = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            MaxSamples = 0
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestZero.Validate(options));

        var requestExcessive = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Tag1"],
            MaxSamples = 501 // above 500 limit
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => requestExcessive.Validate(options));
    }

    [Fact]
    public void CanonicalTags_ResolvesAliasesToCanonicalNames()
    {
        var timeProvider = new TestAutoTimeProvider();
        var tagManifest = new List<TagDefinition>
        {
            new("TemperatureSensor", "DB1.DBD0", PlcDataType.Real, "C", Aliases: ["Temp", "T1", "TempSensor"]),
            new("PressureActual", "DB1.DBD4", PlcDataType.Real, "bar", Aliases: ["Pressure", "P1"])
        };

        var service = new MonitoringService(
            readDelegate: (_, tags, _) => Task.FromResult<IReadOnlyList<TagValue>>(
                tags.Select(t => new TagValue(t, 25.0f, PlcDataType.Real, null, QualityCode.Good, DateTimeOffset.UtcNow)).ToArray()),
            tagResolver: _ => tagManifest,
            timeProvider: timeProvider);

        var canonical = service.ResolveCanonicalTags("target-1", ["Temp", "Pressure", "UnknownTag"]);

        Assert.Equal(3, canonical.Count);
        Assert.Equal("TemperatureSensor", canonical[0]);
        Assert.Equal("PressureActual", canonical[1]);
        Assert.Equal("UnknownTag", canonical[2]);
    }

    [Fact]
    public async Task SampleWindow_CollectsExactRequestedNumberOfSamples()
    {
        var timeProvider = new TestAutoTimeProvider();
        var readCount = 0;

        var service = new MonitoringService(
            readDelegate: (_, tags, _) =>
            {
                readCount++;
                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("Count", readCount, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "sim-target",
            Tags = ["Count"],
            Interval = TimeSpan.FromMilliseconds(100),
            MaxSamples = 5,
            Duration = TimeSpan.FromSeconds(10)
        };

        var result = await service.SampleWindowAsync(request);

        Assert.NotNull(result);
        Assert.Equal("sim-target", result.TargetId);
        Assert.Equal(5, result.SampleCount);
        Assert.Equal(5, result.Samples.Count);
        Assert.Equal(5, readCount);
        Assert.Equal(MonitoringStatus.Completed, result.Status);
        Assert.Equal(SnapshotGuarantee.None, result.SnapshotGuarantee);
    }

    [Fact]
    public async Task SampleWindow_DetectsChangedTags_AndSummarizesDistinctChanges()
    {
        var timeProvider = new TestAutoTimeProvider();
        var step = 0;

        var service = new MonitoringService(
            readDelegate: (_, tags, _) =>
            {
                step++;
                // step 1: Temp=20, Speed=100
                // step 2: Temp=20, Speed=105 (Speed changed)
                // step 3: Temp=25, Speed=105 (Temp changed)
                // step 4: Temp=25, Speed=105 (none changed)
                var temp = step >= 3 ? 25.0f : 20.0f;
                var speed = step >= 2 ? 105 : 100;

                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("Temperature", temp, PlcDataType.Real, "C", QualityCode.Good, timeProvider.GetUtcNow()),
                    new TagValue("Speed", speed, PlcDataType.Int32, "rpm", QualityCode.Good, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Temperature", "Speed"],
            Interval = TimeSpan.FromMilliseconds(100),
            MaxSamples = 4
        };

        var result = await service.SampleWindowAsync(request);

        Assert.Equal(4, result.Samples.Count);

        // Sample 0: initial baseline, no changes reported
        Assert.Empty(result.Samples[0].ChangedTags);
        Assert.False(result.Samples[0].Values.Single(v => v.Name == "Temperature").HasChanged);
        Assert.False(result.Samples[0].Values.Single(v => v.Name == "Speed").HasChanged);

        // Sample 1: Speed changed from 100 to 105
        Assert.Single(result.Samples[1].ChangedTags);
        Assert.Contains("Speed", result.Samples[1].ChangedTags);
        Assert.False(result.Samples[1].Values.Single(v => v.Name == "Temperature").HasChanged);
        Assert.True(result.Samples[1].Values.Single(v => v.Name == "Speed").HasChanged);

        // Sample 2: Temperature changed from 20 to 25
        Assert.Single(result.Samples[2].ChangedTags);
        Assert.Contains("Temperature", result.Samples[2].ChangedTags);
        Assert.True(result.Samples[2].Values.Single(v => v.Name == "Temperature").HasChanged);
        Assert.False(result.Samples[2].Values.Single(v => v.Name == "Speed").HasChanged);

        // Sample 3: neither changed
        Assert.Empty(result.Samples[3].ChangedTags);
        Assert.False(result.Samples[3].Values.Single(v => v.Name == "Temperature").HasChanged);
        Assert.False(result.Samples[3].Values.Single(v => v.Name == "Speed").HasChanged);

        // ChangedTagsSummary contains distinct changed tags
        Assert.Equal(2, result.ChangedTagsSummary.Count);
        Assert.Contains("Speed", result.ChangedTagsSummary);
        Assert.Contains("Temperature", result.ChangedTagsSummary);
    }

    [Fact]
    public async Task SampleWindow_TracksStalenessAccuratelyAcrossSamples()
    {
        var timeProvider = new TestAutoTimeProvider();
        var step = 0;

        var service = new MonitoringService(
            readDelegate: (_, tags, _) =>
            {
                step++;
                // Temp changes on step 3 and stays at 30; Pressure never changes
                var temp = step >= 3 ? 30.0f : 20.0f;
                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("Temp", temp, PlcDataType.Real, "C", QualityCode.Good, timeProvider.GetUtcNow()),
                    new TagValue("Pressure", 5.0f, PlcDataType.Real, "bar", QualityCode.Good, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Temp", "Pressure"],
            Interval = TimeSpan.FromMilliseconds(100),
            MaxSamples = 4
        };

        var result = await service.SampleWindowAsync(request);

        // Sample 0: initial staleness is 0
        Assert.Equal(TimeSpan.Zero, result.Samples[0].Values.Single(v => v.Name == "Temp").Staleness);
        Assert.Equal(TimeSpan.Zero, result.Samples[0].Values.Single(v => v.Name == "Pressure").Staleness);

        // Sample 1: 100ms passed, neither changed -> staleness is 100ms
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Samples[1].Values.Single(v => v.Name == "Temp").Staleness);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Samples[1].Values.Single(v => v.Name == "Pressure").Staleness);

        // Sample 2: Temp changed at sample 2! Staleness reset to 0; Pressure staleness is now 200ms
        Assert.Equal(TimeSpan.Zero, result.Samples[2].Values.Single(v => v.Name == "Temp").Staleness);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.Samples[2].Values.Single(v => v.Name == "Pressure").Staleness);

        // Sample 3: Temp didn't change at sample 3 -> staleness 100ms; Pressure staleness 300ms
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Samples[3].Values.Single(v => v.Name == "Temp").Staleness);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.Samples[3].Values.Single(v => v.Name == "Pressure").Staleness);
    }

    [Fact]
    public async Task SampleWindow_DetectsQualityCodeChanges()
    {
        var timeProvider = new TestAutoTimeProvider();
        var step = 0;

        var service = new MonitoringService(
            readDelegate: (_, tags, _) =>
            {
                step++;
                // Value stays at 10, but quality degrades from Good to Bad on step 2
                var quality = step >= 2 ? QualityCode.Bad : QualityCode.Good;
                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("Sensor", 10, PlcDataType.Int32, null, quality, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["Sensor"],
            Interval = TimeSpan.FromMilliseconds(100),
            MaxSamples = 3
        };

        var result = await service.SampleWindowAsync(request);

        Assert.Equal(3, result.Samples.Count);
        Assert.Empty(result.Samples[0].ChangedTags);
        Assert.Contains("Sensor", result.Samples[1].ChangedTags); // quality transition detected as change
        Assert.True(result.Samples[1].Values[0].HasChanged);
        Assert.Equal(QualityCode.Bad, result.Samples[1].Values[0].Quality);
    }

    [Fact]
    public async Task SampleWindow_TerminatesWhenDurationExpires()
    {
        var timeProvider = new TestAutoTimeProvider();

        var service = new MonitoringService(
            readDelegate: (_, tags, _) => Task.FromResult<IReadOnlyList<TagValue>>(
            [
                new TagValue("V", 1, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
            ]),
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["V"],
            Interval = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(350), // should allow at most 4 samples (t=0, 100, 200, 300)
            MaxSamples = 500
        };

        var result = await service.SampleWindowAsync(request);

        Assert.True(result.Samples.Count <= 4);
        Assert.Equal(MonitoringStatus.Completed, result.Status);
    }

    [Fact]
    public async Task SampleWindow_HonorsCancellation_TerminatesEarly()
    {
        var timeProvider = new TestAutoTimeProvider();
        using var cts = new CancellationTokenSource();

        var sampleCount = 0;
        var service = new MonitoringService(
            readDelegate: (_, tags, _) =>
            {
                sampleCount++;
                if (sampleCount == 2)
                {
                    cts.Cancel();
                }
                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("V", sampleCount, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["V"],
            Interval = TimeSpan.FromMilliseconds(100),
            MaxSamples = 10
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SampleWindowAsync(request, cts.Token));
        Assert.True(sampleCount >= 2);
    }

    [Fact]
    public async Task MonitoringService_IsReadOnly_NeverCallsWrite()
    {
        var fakeClient = new FakePlcRuntimeClient();
        var target = new TargetProfile(
            Id: "target-1",
            Vendor: PlcVendor.Siemens,
            Family: "S7-1200",
            Model: "CPU 1214C",
            Firmware: "V4.4",
            Endpoint: new EndpointProfile("[IP]", 102, TransportKind.S7Comm),
            RuntimeProtocols: [TransportKind.S7Comm],
            EngineeringBackend: null,
            Capabilities: new CapabilitySet([]),
            PolicyId: "default");

        var manifests = new Dictionary<string, IReadOnlyList<TagDefinition>>
        {
            ["target-1"] = [new TagDefinition("MotorSpeed", "DB1.DBD0", PlcDataType.Real, CanRead: true, CanWrite: false)]
        };

        var timeProvider = new TestAutoTimeProvider();
        var service = new MonitoringService(fakeClient, [target], manifests, timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["MotorSpeed"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 3
        };

        var result = await service.SampleWindowAsync(request);

        Assert.Equal(3, result.SampleCount);
        Assert.Equal(3, fakeClient.ReadCallCount);
        Assert.Equal(0, fakeClient.WriteCallCount); // Verified: NO write is ever attempted
    }

    [Fact]
    public async Task PerTargetSerialization_PreventsConcurrentReadsToSameTarget()
    {
        var timeProvider = new TestAutoTimeProvider();
        var concurrentCount = 0;
        var maxConcurrent = 0;

        var service = new MonitoringService(
            readDelegate: async (_, _, _) =>
            {
                var cur = Interlocked.Increment(ref concurrentCount);
                if (cur > maxConcurrent) maxConcurrent = cur;

                await Task.Yield(); // yield to give other tasks chance to run
                Interlocked.Decrement(ref concurrentCount);

                return
                [
                    new TagValue("Tag", 1, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
                ];
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-shared",
            Tags = ["Tag"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 3
        };

        // Run two concurrent monitoring sessions for the same target
        var task1 = service.SampleWindowAsync(request);
        var task2 = service.SampleWindowAsync(request);

        await Task.WhenAll(task1, task2);

        // Per-target throttler guarantees that at any point in time, concurrency is at most 1
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public void BoundedBuffer_DropsOldestSamplesWhenCapacityExceeded()
    {
        var buffer = new BoundedSampleBuffer(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(new MonitoringSample(
                SampleIndex: i,
                Timestamp: DateTimeOffset.UtcNow,
                Values: [],
                ChangedTags: []));
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.TotalAdded);
        Assert.Equal(5, buffer.Latest?.SampleIndex);

        var snapshot = buffer.ToArray();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(3, snapshot[0].SampleIndex);
        Assert.Equal(4, snapshot[1].SampleIndex);
        Assert.Equal(5, snapshot[2].SampleIndex);
    }

    [Fact]
    public async Task MonitoringSession_CanStartAndStopGracefully()
    {
        // Use real system time provider with 100ms interval so session remains active while we verify and stop it
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>(
            [
                new TagValue("TagA", 42, PlcDataType.Int32, null, QualityCode.Good, DateTimeOffset.UtcNow)
            ]),
            timeProvider: TimeProvider.System);

        var request = new MonitoringRequest
        {
            TargetId = "target-1",
            Tags = ["TagA"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 500,
            Duration = TimeSpan.FromMinutes(1)
        };

        var session = service.StartSession(request);
        Assert.NotNull(session);
        Assert.NotEmpty(session.SessionId);
        Assert.Equal(MonitoringStatus.Active, session.Status);

        // Wait briefly for at least one sample
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (session.GetBufferedSamples().Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(session.GetBufferedSamples());

        // Stop session
        service.StopSession(session.SessionId);
        await session.Completion;

        Assert.Equal(MonitoringStatus.Cancelled, session.Status);
        Assert.NotNull(session.Info.CompletedAt);
    }

    [Fact]
    public async Task StreamSamplesAsync_YieldsSamplesAsTheyAreCollected()
    {
        var timeProvider = new TestAutoTimeProvider();
        var index = 0;

        var service = new MonitoringService(
            readDelegate: (_, _, _) =>
            {
                index++;
                return Task.FromResult<IReadOnlyList<TagValue>>(
                [
                    new TagValue("Counter", index, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
                ]);
            },
            timeProvider: timeProvider);

        var request = new MonitoringRequest
        {
            TargetId = "target-stream",
            Tags = ["Counter"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 4
        };

        var collected = new List<MonitoringSample>();
        await foreach (var sample in service.StreamSamplesAsync(request))
        {
            collected.Add(sample);
        }

        Assert.Equal(4, collected.Count);
        Assert.Equal(1, Convert.ToInt32(collected[0].Values[0].Value));
        Assert.Equal(4, Convert.ToInt32(collected[3].Values[0].Value));
    }

    [Fact]
    public async Task Integration_WorksWithRealPlcComposition()
    {
        var host = DefaultPlcComposition.Create();
        var targetId = "sim-siemens";

        // MonitoringService accepts PlcRuntimeService directly!
        var service = new MonitoringService(host.Service);

        var request = new MonitoringRequest
        {
            TargetId = targetId,
            Tags = ["PressureSetpoint", "PressureActual"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 3,
            Duration = TimeSpan.FromSeconds(5)
        };

        var result = await service.SampleWindowAsync(request);

        Assert.NotNull(result);
        Assert.Equal(targetId, result.TargetId);
        Assert.Equal(3, result.SampleCount);
        Assert.All(result.Samples, s =>
        {
            Assert.Equal(SnapshotGuarantee.None, s.SnapshotGuarantee);
            Assert.Equal(2, s.Values.Count);
            Assert.All(s.Values, v => Assert.Equal(QualityCode.Simulated, v.Quality));
        });
    }

    [Fact]
    public void Disposal_CleansUpSessionsAndResources()
    {
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([]));

        var session = service.StartSession(new MonitoringRequest
        {
            TargetId = "t1",
            Tags = ["tag"]
        });

        Assert.Single(service.ListSessions());

        service.Dispose();

        // After service disposal, attempts to sample should throw ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(() => service.StartSession(new MonitoringRequest
        {
            TargetId = "t1",
            Tags = ["tag"]
        }));
    }

    [Fact]
    public void StartSession_EnforcesPerTargetQuota_NegativeTest()
    {
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([]),
            maxActiveSessionsPerTarget: 2);

        var req1 = new MonitoringRequest { TargetId = "target-A", Tags = ["Tag1"] };
        var req2 = new MonitoringRequest { TargetId = "target-A", Tags = ["Tag2"] };
        var req3 = new MonitoringRequest { TargetId = "target-A", Tags = ["Tag3"] };

        var s1 = service.StartSession(req1);
        var s2 = service.StartSession(req2);
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        var ex = Assert.Throws<InvalidOperationException>(() => service.StartSession(req3));
        Assert.Contains("target 'target-A' reached", ex.Message);

        // Different target should still succeed
        var reqB = new MonitoringRequest { TargetId = "target-B", Tags = ["Tag1"] };
        var sB = service.StartSession(reqB);
        Assert.NotNull(sB);
    }

    [Fact]
    public void StartSession_EnforcesGlobalQuota_NegativeTest()
    {
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([]),
            maxActiveSessionsPerTarget: 10,
            maxActiveSessionsGlobal: 3);

        service.StartSession(new MonitoringRequest { TargetId = "t1", Tags = ["Tag1"] });
        service.StartSession(new MonitoringRequest { TargetId = "t2", Tags = ["Tag1"] });
        service.StartSession(new MonitoringRequest { TargetId = "t3", Tags = ["Tag1"] });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.StartSession(new MonitoringRequest { TargetId = "t4", Tags = ["Tag1"] }));
        Assert.Contains("Global active monitoring session limit reached", ex.Message);
    }

    [Fact]
    public async Task StartSession_ReusesQuota_AfterActiveSessionCompletesOrCancels()
    {
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([]),
            maxActiveSessionsPerTarget: 1);

        var s1 = service.StartSession(new MonitoringRequest { TargetId = "t1", Tags = ["Tag1"] });
        Assert.Throws<InvalidOperationException>(() =>
            service.StartSession(new MonitoringRequest { TargetId = "t1", Tags = ["Tag2"] }));

        // Stop session s1 so its status transitions to Cancelled (terminal)
        service.StopSession(s1.SessionId);
        await s1.Completion;
        Assert.Equal(MonitoringStatus.Cancelled, s1.Status);

        // Now target quota allows a new session
        var s2 = service.StartSession(new MonitoringRequest { TargetId = "t1", Tags = ["Tag2"] });
        Assert.NotNull(s2);
    }

    [Fact]
    public async Task TerminalSessions_RetainedTemporarilyForQuery_AndCleanedUpAfterExpiry()
    {
        var timeProvider = new TestAutoTimeProvider();
        var retention = TimeSpan.FromSeconds(30);

        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([
                new TagValue("V", 1, PlcDataType.Int32, null, QualityCode.Good, timeProvider.GetUtcNow())
            ]),
            timeProvider: timeProvider,
            terminalRetentionPeriod: retention);

        var session = service.StartSession(new MonitoringRequest
        {
            TargetId = "t1",
            Tags = ["V"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 1,
            Duration = TimeSpan.FromSeconds(1)
        });

        // Wait for session to complete
        await session.Completion;
        Assert.Equal(MonitoringStatus.Completed, session.Status);

        // Right after completion, session is in terminal state but still queryable
        Assert.NotNull(service.GetSession(session.SessionId));
        Assert.Contains(service.ListSessions(), s => s.SessionId == session.SessionId);

        // Fast forward time past retention period
        using (timeProvider.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(35), Timeout.InfiniteTimeSpan))
        {
        }

        // Querying or calling CleanupTerminalSessions removes expired terminal session
        var removed = service.CleanupTerminalSessions();
        Assert.Equal(1, removed);
        Assert.Null(service.GetSession(session.SessionId));
        Assert.DoesNotContain(service.ListSessions(), s => s.SessionId == session.SessionId);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForRunningSessionCompletion_Gracefully()
    {
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCleanedUp = false;

        var service = new MonitoringService(
            readDelegate: async (_, _, ct) =>
            {
                runStarted.TrySetResult();
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                finally
                {
                    runCleanedUp = true;
                }
                return [new TagValue("V", 1, PlcDataType.Int32, null, QualityCode.Good, DateTimeOffset.UtcNow)];
            },
            timeProvider: TimeProvider.System);

        var session = service.StartSession(new MonitoringRequest
        {
            TargetId = "t1",
            Tags = ["V"],
            Interval = TimeSpan.FromMilliseconds(50),
            MaxSamples = 100,
            Duration = TimeSpan.FromSeconds(10)
        });

        await runStarted.Task;

        // DisposeAsync should stop session and await completion
        await service.DisposeAsync();

        Assert.True(runCleanedUp);
        Assert.Equal(MonitoringStatus.Cancelled, session.Status);
        Assert.Empty(service.ListSessions());
    }

    [Fact]
    public async Task ConcurrentStartSession_UnderQuotaContention_MaintainsStrictLimit()
    {
        var quota = 2;
        var service = new MonitoringService(
            readDelegate: (_, _, _) => Task.FromResult<IReadOnlyList<TagValue>>([]),
            maxActiveSessionsPerTarget: quota);

        var started = new List<IMonitoringSession>();
        var rejectedCount = 0;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                var s = service.StartSession(new MonitoringRequest
                {
                    TargetId = "target-heavy",
                    Tags = [$"Tag{i}"]
                });
                lock (lockObj)
                {
                    started.Add(s);
                }
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejectedCount);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(quota, started.Count);
        Assert.Equal(8, rejectedCount);
    }

    private sealed class FakePlcRuntimeClient : IPlcRuntimeClient
    {
        public int ReadCallCount { get; private set; }
        public int WriteCallCount { get; private set; }
        public int ProbeCallCount { get; private set; }

        public Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default)
        {
            ProbeCallCount++;
            return Task.FromResult(new ProbeResult(target.Id, true, target.IsSimulation, "Running", "Connected", DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<TagValue>> ReadAsync(
            TargetProfile target,
            IReadOnlyList<TagDefinition> tags,
            CancellationToken cancellationToken = default)
        {
            ReadCallCount++;
            return Task.FromResult<IReadOnlyList<TagValue>>(
                tags.Select(t => new TagValue(
                    t.Name,
                    123,
                    t.DataType,
                    t.Unit,
                    QualityCode.Good,
                    DateTimeOffset.UtcNow,
                    t.NativeAddress)).ToArray());
        }

        public Task<TagValue> WriteAsync(
            TargetProfile target,
            TagDefinition tag,
            object value,
            CancellationToken cancellationToken = default)
        {
            WriteCallCount++;
            throw new InvalidOperationException("Write operations are strictly prohibited during monitoring.");
        }
    }

    private sealed class TestAutoTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        private readonly object _lock = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _utcNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                if (dueTime > TimeSpan.Zero)
                {
                    _utcNow += dueTime;
                }
            }

            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private bool _disposed;

            public ImmediateTimer(TimerCallback callback, object? state)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (!_disposed)
                    {
                        callback(state);
                    }
                });
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
