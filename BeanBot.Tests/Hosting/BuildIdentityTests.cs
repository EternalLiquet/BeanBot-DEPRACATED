using System.Reflection;
using BeanBot.Hosting;
using Xunit;

namespace BeanBot.Tests.Hosting;

public class BuildIdentityTests
{
    [Fact]
    public void Current_UsesGeneratedNonSecretBuildMetadata()
    {
        Assert.Equal("0.0.0-local", BuildIdentity.Current.Version);
        Assert.Equal("unknown", BuildIdentity.Current.CommitSha);
    }

    [Fact]
    public void FromAssembly_MissingMetadata_UsesSafeUnknownValues()
    {
        var identity = BuildIdentity.FromAssembly(typeof(BuildIdentityTests).Assembly);

        Assert.Equal("unknown", identity.Version);
        Assert.Equal("unknown", identity.CommitSha);
    }
}
