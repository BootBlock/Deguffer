using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every test class that makes a <see cref="ScratchSyncRoot"/> runs in the collection that runs them
/// one after another.
///
/// <para><b>Nothing at run time can hold the rule.</b> Windows shows a process every placeholder
/// undisguised while it is connected to any sync root, so a root that one class connects after another
/// class disconnected its own changes what that other class sees. Its test then fails now and then,
/// not every time, and fails on its own fixture check rather than on the code it tests.
/// <see cref="ScratchSyncRoot.Disconnect"/> refuses beside a root that is already connected, but no
/// guard sees a root connected afterwards. So the rule is checked here, where the class that would
/// break it is found before it runs.</para>
/// </summary>
public sealed class ScratchSyncRootCollectionTests
{
    /// <summary>
    /// The suite's own types and the shared test library, read from their IL. A creation in the shared
    /// library outside <see cref="ScratchSyncRoot"/> would make a root for any class that called it,
    /// out of sight of this check, so the library may make none.
    /// </summary>
    [Fact]
    public void EveryClassThatMakesAScratchSyncRootRunsInTheirCollection()
    {
        Assert.Empty(RunOutside(typeof(ScratchSyncRootCollectionTests).Assembly, typeof(ScratchSyncRoot), ScratchSyncRootTests.Collection));
        Assert.Empty(ObjectCreations.TypesCreating(typeof(ScratchSyncRoot).Assembly, typeof(ScratchSyncRoot)));
    }

    /// <summary>
    /// The rule judged as xUnit runs the classes, proved on types made for it. A class nested in one
    /// in the collection is not in it, a class that inherits the code that makes one is judged by its
    /// own collection, and a class that names none takes its base's.
    /// </summary>
    [Fact]
    public void AClassIsJudgedByTheCollectionXunitRunsItIn()
    {
        Assert.Equal(
            [typeof(KeptByAnInheritorElsewhere).FullName, typeof(KeptInTheCollection.NestedInIt).FullName, typeof(KeptOutsideIt).FullName],
            RunOutside(typeof(ScratchSyncRootCollectionTests).Assembly, typeof(Kept), SampleCollection));
    }

    /// <summary>
    /// The collection runs inside one test assembly, and the race is inside one process, so a root made
    /// in another test project would race the classes there with nothing to stop it. That project's IL is
    /// out of this assembly's reach, so its source is read for the name instead.
    /// </summary>
    [Fact]
    public void OnlyThisTestProjectMakesAScratchSyncRoot()
    {
        var elsewhere = TestProjects.Directories()
            .Where(project => Path.GetFileName(project) != typeof(ScratchSyncRootCollectionTests).Assembly.GetName().Name)
            .SelectMany(TestProjects.SourceFiles)
            .Where(file => File.ReadAllText(file).Contains(nameof(ScratchSyncRoot), StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file));

        Assert.Empty(elsewhere);
    }

    /// <summary>
    /// The finding the first test relies on, proved on types made for it. A creation the compiler moves
    /// into a type of its own still counts against the class it was written in, a field initialiser and
    /// a static constructor count, and naming the type without creating one does not.
    /// </summary>
    [Fact]
    public void TheScanFindsEachWayACreationIsWrittenAndNothingElse()
    {
        var makers = ObjectCreations.TypesCreating(typeof(ScratchSyncRootCollectionTests).Assembly, typeof(Made));

        Assert.Equal(
            [
                typeof(InAConstructor),
                typeof(InAFieldInitialiser),
                typeof(InALambda),
                typeof(InALocalFunction),
                typeof(InAStaticConstructor),
                typeof(InAnAsyncMethod),
                typeof(InAnIterator),
            ],
            makers);
    }

    /// <summary>
    /// The full name of every type in <paramref name="assembly"/> that runs code creating a
    /// <paramref name="created"/>, its own or inherited, outside <paramref name="collection"/>, in
    /// name order. Only an abstract class is left out, because xUnit runs only what derives from it.
    /// </summary>
    private static IReadOnlyList<string?> RunOutside(System.Reflection.Assembly assembly, Type created, string collection)
    {
        var makers = ObjectCreations.TypesCreating(assembly, created);

        return [.. assembly.GetTypes()
            .Where(type => !(type.IsAbstract && !type.IsSealed) && makers.Any(maker => maker.IsAssignableFrom(type)))
            .Where(type => CollectionOf(type) != collection)
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The name of the xUnit collection <paramref name="type"/> runs in, if it names one. xUnit reads
    /// the attribute from the class and the classes it derives from, never from a class it is nested in.
    /// </summary>
    private static string? CollectionOf(Type type)
    {
        for (Type? candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.GetCustomAttributesData().SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute)) is { } collection)
            {
                return collection.ConstructorArguments[0].Value as string;
            }
        }

        return null;
    }

    private sealed class Made;

    private sealed class InAConstructor
    {
        public InAConstructor() => Made = new Made();

        public Made Made { get; }
    }

    private sealed class InAFieldInitialiser
    {
        public Made Made { get; } = new();
    }

    private static class InAStaticConstructor
    {
        public static readonly Made Made = new();
    }

    private static class InALambda
    {
        public static Func<Made> Make() => () => new Made();
    }

    private static class InALocalFunction
    {
        public static Made Make()
        {
            return Local();

            static Made Local() => new();
        }
    }

    private static class InAnIterator
    {
        public static IEnumerable<Made> Make()
        {
            yield return new Made();
        }
    }

    private static class InAnAsyncMethod
    {
        public static async Task<Made> Make()
        {
            await Task.Yield();
            return new Made();
        }
    }

    private static class NamesTheTypeOnly
    {
        public static Made? Held { get; set; }

        public static Type Kind => typeof(Made);

        public static bool Is(object value) => value is Made;
    }

    private const string SampleCollection = "Samples of the scratch sync root rule";

    private sealed class Kept;

    [Collection(SampleCollection)]
    private sealed class KeptInTheCollection
    {
        public Kept Kept { get; } = new();

        public sealed class NestedInIt
        {
            public Kept Kept { get; } = new();
        }
    }

    private sealed class KeptOutsideIt
    {
        public Kept Kept { get; } = new();
    }

    [Collection(SampleCollection)]
    private class KeptByABase
    {
        public Kept Kept { get; } = new();
    }

    private sealed class KeptByAnInheritor : KeptByABase;

    [Collection("Another collection")]
    private sealed class KeptByAnInheritorElsewhere : KeptByABase;
}
