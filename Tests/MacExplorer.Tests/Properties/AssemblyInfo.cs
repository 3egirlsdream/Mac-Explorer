using Avalonia.Headless;
using MacExplorer.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
// Headless tests reset the process-wide dispatcher; other collections must not race that reset.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
