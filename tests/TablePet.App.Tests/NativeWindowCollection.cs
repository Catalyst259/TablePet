using Xunit;

namespace TablePet.App.Tests;

// HWND ownership and Topmost changes mutate the process-wide desktop Z order,
// even when the test windows live at different offscreen coordinates.
[CollectionDefinition("Native decoration windows", DisableParallelization = true)]
public sealed class NativeWindowCollection;
