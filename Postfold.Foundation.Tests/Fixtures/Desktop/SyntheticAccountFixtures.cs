using Postfold.Desktop.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

internal static class SyntheticAccountFixtures
{
    internal static Task SeedSyntheticAsync(this DesktopMailRuntime host, int count, CancellationToken cancellationToken)
    {
        return new SyntheticProfileSeeder(host).SeedSyntheticAsync(count, cancellationToken);
    }

    internal static Task SeedDesignFixtureAsync(this DesktopMailRuntime host, CancellationToken cancellationToken)
    {
        return new SyntheticProfileSeeder(host).SeedDesignFixtureAsync(cancellationToken);
    }

    internal static Task SeedReadingFixtureAsync(this DesktopMailRuntime host, CancellationToken cancellationToken)
    {
        return new SyntheticProfileSeeder(host).SeedReadingFixtureAsync(cancellationToken);
    }
}
