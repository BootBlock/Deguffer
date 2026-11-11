using System.Runtime.InteropServices;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The host's conversation with a handler, through a recording stand-in rather than Windows' own: which
/// calls it makes, in which order, with which volume, and what it reads from each answer.
///
/// <para>Asking <em>Previous Installations</em> to measure walks the whole of <c>Windows.old</c>, about
/// 100 s of processor time, and no outcome shows that it happened. So the calls themselves are what
/// these assert.</para>
/// </summary>
public sealed class DiskCleanupHandlersTests
{
    private const string Handler = "Previous Installations";
    private const string Volume = @"C:\";

    [Fact]
    public void ASurveyAskingWhetherAHandlerStartsNeverAsksItToMeasure()
    {
        var cache = new RecordingVolumeCache { Measured = 82_106_208_256 };
        var loader = new RecordingLoader(cache);

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.HasSomething, survey.Answer);
        Assert.Equal(["Initialize", "Deactivate"], cache.Calls);
        Assert.Equal(Volume, cache.Volume);
        Assert.Equal(1, loader.Released);
    }

    [Theory]
    [InlineData(DiskCleanupQuestion.Starts)]
    [InlineData(DiskCleanupQuestion.Measures)]
    public void AHandlerThatStartsWithNothingToDeleteWithholdsTheStep(DiskCleanupQuestion question)
    {
        var cache = new RecordingVolumeCache { Initialised = DiskCleanupNative.S_FALSE };
        var loader = new RecordingLoader(cache);

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, question, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.NothingToClear, survey.Answer);
        Assert.False(survey.MayOffer);
        Assert.Equal(["Initialize"], cache.Calls);
        Assert.Equal(1, loader.Released);
    }

    /// <summary>
    /// The driver-package handler's start promises nothing: it was observed to start and then measure
    /// 0 bytes. Asked how much, a measured zero withholds the step.
    /// </summary>
    [Fact]
    public void ASurveyAskingHowMuchReadsAMeasuredZeroAsNothingToClear()
    {
        var cache = new RecordingVolumeCache { Measured = 0 };

        var survey = new DiskCleanupHandlers(new RecordingLoader(cache))
            .Survey(Handler, Volume, DiskCleanupQuestion.Measures, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.NothingToClear, survey.Answer);
        Assert.Equal(["Initialize", "GetSpaceUsed", "Deactivate"], cache.Calls);
    }

    [Fact]
    public void ASurveyAskingHowMuchOffersWhatTheHandlerMeasures()
    {
        var cache = new RecordingVolumeCache { Measured = 4096 };

        var survey = new DiskCleanupHandlers(new RecordingLoader(cache))
            .Survey(Handler, Volume, DiskCleanupQuestion.Measures, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.HasSomething, survey.Answer);
    }

    /// <summary>A run still measures before it purges, and asks for exactly what was measured.</summary>
    [Fact]
    public void ARunMeasuresAndThenPurgesWhatItMeasured()
    {
        var cache = new RecordingVolumeCache { Measured = 4096 };
        var loader = new RecordingLoader(cache);

        var outcome = new DiskCleanupHandlers(loader).Run(Handler, Volume, CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(["Initialize", "GetSpaceUsed", "Purge", "Deactivate"], cache.Calls);
        Assert.Equal(4096UL, cache.AskedToPurge);
        Assert.Equal(Volume, cache.Volume);
        Assert.Equal(1, loader.Released);
    }

    /// <summary>
    /// A handler with the second interface is told the registration's name, as Disk Cleanup tells it.
    /// </summary>
    [Fact]
    public void AHandlerWithTheSecondInterfaceIsToldItsRegistrationsName()
    {
        var cache = new RecordingVolumeCache2();

        new DiskCleanupHandlers(new RecordingLoader(cache)).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(["InitializeEx", "Deactivate"], cache.Calls);
        Assert.Equal(Handler, cache.KeyName);
    }

    [Fact]
    public void AnUnelevatedSurveyNeverLoadsTheHandler()
    {
        var loader = new RecordingLoader(new RecordingVolumeCache()) { IsElevated = false };

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.NeedsElevation, survey.Answer);
        Assert.Equal(0, loader.Loads);
    }

    [Fact]
    public void AHandlerThatIsNotRegisteredIsUnavailableAndNeverLoaded()
    {
        var loader = new RecordingLoader(new RecordingVolumeCache()) { Registered = false };

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.Unavailable, survey.Answer);
        Assert.Contains("no longer registers", survey.Message, StringComparison.Ordinal);
        Assert.Equal(0, loader.Loads);
    }

    [Fact]
    public void AHandlerThatWillNotLoadIsUnavailableWithItsNumber()
    {
        var loader = new RecordingLoader(null, failure: unchecked((int)0x80070005));

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.Unavailable, survey.Answer);
        Assert.Contains("would not load (0x80070005)", survey.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandlerThatWillNotStartIsUnavailableWithItsNumberAndReleased()
    {
        var cache = new RecordingVolumeCache { Initialised = unchecked((int)0x80004005) };
        var loader = new RecordingLoader(cache);

        var survey = new DiskCleanupHandlers(loader).Survey(Handler, Volume, DiskCleanupQuestion.Starts, CancellationToken.None);

        Assert.Equal(DiskCleanupAnswer.Unavailable, survey.Answer);
        Assert.Contains("would not start (0x80004005)", survey.Message, StringComparison.Ordinal);
        Assert.Equal(["Initialize"], cache.Calls);
        Assert.Equal(1, loader.Released);
    }

    [Fact]
    public void AQuestionThatIsNotOneIsRefusedBeforeAnythingLoads()
    {
        var loader = new RecordingLoader(new RecordingVolumeCache());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiskCleanupHandlers(loader).Survey(Handler, Volume, (DiskCleanupQuestion)99, CancellationToken.None));
        Assert.Equal(0, loader.Loads);
    }

    private sealed class RecordingLoader(object? instance, int failure = DiskCleanupNative.S_OK) : IVolumeCacheLoader
    {
        public bool IsElevated { get; init; } = true;

        public bool Registered { get; init; } = true;

        public int Loads { get; private set; }

        public int Released { get; private set; }

        public bool IsRegistered(string handler) => Registered;

        public LoadedVolumeCache? Load(string handler, out int failed)
        {
            Loads++;
            failed = failure;
            return instance is null ? null : new LoadedVolumeCache(instance, IntPtr.Zero, () => Released++);
        }
    }

    /// <summary>A handler with only the first interface, as <c>setupcln.dll</c> is.</summary>
    private class RecordingVolumeCache : IEmptyVolumeCache
    {
        public int Initialised { get; init; } = DiskCleanupNative.S_OK;

        public ulong Measured { get; init; } = 1;

        public List<string> Calls { get; } = [];

        public string? Volume { get; protected set; }

        public ulong? AskedToPurge { get; private set; }

        public int Initialize(IntPtr key, string volume, out IntPtr displayName, out IntPtr description, ref uint flags)
        {
            Calls.Add("Initialize");
            return Started(volume, out displayName, out description);
        }

        public int GetSpaceUsed(out ulong spaceUsed, IEmptyVolumeCacheCallBack callback)
        {
            Calls.Add("GetSpaceUsed");
            spaceUsed = Measured;
            return DiskCleanupNative.S_OK;
        }

        public int Purge(ulong spaceToFree, IEmptyVolumeCacheCallBack callback)
        {
            Calls.Add("Purge");
            AskedToPurge = spaceToFree;
            return DiskCleanupNative.S_OK;
        }

        public int ShowProperties(IntPtr window) => DiskCleanupNative.S_OK;

        public int Deactivate(out uint flags)
        {
            Calls.Add("Deactivate");
            flags = 0;
            return DiskCleanupNative.S_OK;
        }

        /// <summary>The strings a real handler allocates for the host to free, so the host's frees are exercised.</summary>
        protected int Started(string volume, out IntPtr displayName, out IntPtr description)
        {
            Volume = volume;
            displayName = Marshal.StringToCoTaskMemUni("display");
            description = Marshal.StringToCoTaskMemUni("description");
            return Initialised;
        }
    }

    /// <summary>A handler with the second interface as well, as <c>pnpclean.dll</c> is.</summary>
    private sealed class RecordingVolumeCache2 : RecordingVolumeCache, IEmptyVolumeCache2
    {
        public string? KeyName { get; private set; }

        public int InitializeEx(
            IntPtr key,
            string volume,
            string keyName,
            out IntPtr displayName,
            out IntPtr description,
            out IntPtr buttonText,
            ref uint flags)
        {
            Calls.Add("InitializeEx");
            KeyName = keyName;
            buttonText = Marshal.StringToCoTaskMemUni("button");
            return Started(volume, out displayName, out description);
        }
    }
}
