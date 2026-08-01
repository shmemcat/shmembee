namespace Shmembee.Core.Tests;

public class BootstrapTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("Shmembee.Core", typeof(Shmembee.Core.Class1).Assembly.GetName().Name);
    }
}
