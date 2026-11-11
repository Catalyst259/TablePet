using System.IO;
using TablePet.App.Services;
using Xunit;

namespace TablePet.App.Tests;

public sealed class ApplicationDataLocationTests
{
    [Fact]
    public void DirectorySpellingAliasesUseTheSameFilesAndSingleInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), "TablePet-Preview");
        var original = ApplicationDataLocation.Resolve(path);
        foreach (var alias in new[] { path + "\\", path.ToUpperInvariant(), path + "\\sub\\..", path.Replace('\\', '/') })
        {
            var resolved = ApplicationDataLocation.Resolve(alias);
            Assert.True(StringComparer.OrdinalIgnoreCase.Equals(original.DirectoryPath, resolved.DirectoryPath));
            Assert.Equal(original.InstanceSuffix, resolved.InstanceSuffix);
        }
    }

    [Fact]
    public void ExplicitDefaultDirectorySharesTheInstalledInstanceSignals()
    {
        var implicitDefault = ApplicationDataLocation.Resolve(null);
        var explicitDefault = ApplicationDataLocation.Resolve(ApplicationDataLocation.DefaultDirectory + "\\");
        Assert.Equal(implicitDefault.DirectoryPath, explicitDefault.DirectoryPath);
        Assert.Empty(implicitDefault.InstanceSuffix);
        Assert.Empty(explicitDefault.InstanceSuffix);
    }

    [Fact]
    public void DistinctDirectoriesRemainIndependent()
    {
        var first = ApplicationDataLocation.Resolve(Path.Combine(Path.GetTempPath(), "TablePet-Preview-A"));
        var second = ApplicationDataLocation.Resolve(Path.Combine(Path.GetTempPath(), "TablePet-Preview-B"));
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.NotEqual(first.InstanceSuffix, second.InstanceSuffix);
    }
}
