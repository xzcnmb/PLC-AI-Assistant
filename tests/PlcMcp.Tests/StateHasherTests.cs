using PlcMcp.Contracts.Models;
using PlcMcp.Runtime.Policy;

namespace PlcMcp.Tests;

public class StateHasherTests
{
    [Fact]
    public void Compute_IsStableRegardlessOfOrder()
    {
        var now = DateTimeOffset.UtcNow;

        var tagA = new TagValue("PressureActual", 2.5f, PlcDataType.Real, "bar", QualityCode.Good, now, "VD200");
        var tagB = new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, now, "M0.0");
        var tagC = new TagValue("CycleTimeMs", 1000, PlcDataType.Int32, "ms", QualityCode.Good, now, "VD130");

        var list1 = new[] { tagA, tagB, tagC };
        var list2 = new[] { tagC, tagA, tagB };
        var list3 = new[] { tagB, tagC, tagA };

        var hash1 = StateHasher.Compute(list1);
        var hash2 = StateHasher.Compute(list2);
        var hash3 = StateHasher.Compute(list3);

        Assert.NotNull(hash1);
        Assert.NotEmpty(hash1);
        Assert.Equal(hash1, hash2);
        Assert.Equal(hash2, hash3);
    }

    [Fact]
    public void Compute_ProducesDifferentHashWhenValuesDiffer()
    {
        var now = DateTimeOffset.UtcNow;

        var list1 = new[]
        {
            new TagValue("PressureSetpoint", 2.5f, PlcDataType.Real, "bar", QualityCode.Good, now),
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, now)
        };

        var list2 = new[]
        {
            new TagValue("PressureSetpoint", 3.0f, PlcDataType.Real, "bar", QualityCode.Good, now),
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, now)
        };

        var hash1 = StateHasher.Compute(list1);
        var hash2 = StateHasher.Compute(list2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Compute_ProducesDifferentHashWhenQualityDiffers()
    {
        var now = DateTimeOffset.UtcNow;

        var list1 = new[]
        {
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, now)
        };

        var list2 = new[]
        {
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Bad, now)
        };

        var hash1 = StateHasher.Compute(list1);
        var hash2 = StateHasher.Compute(list2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Compute_IgnoresTimestampAndNativeAddressInCanonicalHash()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMinutes(10);

        var list1 = new[]
        {
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, t1, "M0.0")
        };

        var list2 = new[]
        {
            new TagValue("RunMode", true, PlcDataType.Bool, null, QualityCode.Good, t2, "DifferentNativeAddress")
        };

        var hash1 = StateHasher.Compute(list1);
        var hash2 = StateHasher.Compute(list2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Compute_EmptyList_ReturnsConsistentHash()
    {
        var hash1 = StateHasher.Compute(Array.Empty<TagValue>());
        var hash2 = StateHasher.Compute(new List<TagValue>());

        Assert.NotEmpty(hash1);
        Assert.Equal(hash1, hash2);
    }
}
