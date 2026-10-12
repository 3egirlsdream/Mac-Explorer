using Avalonia.Headless;
using MacExplorer.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
// Each UI test owns its dispatcher; other collections must not race the reset.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
