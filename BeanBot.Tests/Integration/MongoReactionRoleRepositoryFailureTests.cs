using System.Net;
using System.Net.Sockets;
using BeanBot.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;

namespace BeanBot.Tests.Integration;

public sealed class MongoReactionRoleRepositoryFailureTests
{
    [Fact]
    public async Task GetRoleSetting_PropagatesBoundedMongoInfrastructureFailure()
    {
        using var nonMongoEndpoint = new TcpListener(IPAddress.Loopback, 0);
        nonMongoEndpoint.Start();
        var endpoint = (IPEndPoint)nonMongoEndpoint.LocalEndpoint;
        var settings = MongoClientSettings.FromConnectionString($"mongodb://127.0.0.1:{endpoint.Port}");
        settings.ConnectTimeout = TimeSpan.FromMilliseconds(250);
        settings.SocketTimeout = TimeSpan.FromMilliseconds(250);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        var repository = new ReactionRoleRepository(
            new MongoClient(settings).GetDatabase($"BeanBotIntegration_{Guid.NewGuid():N}"),
            NullLogger<ReactionRoleRepository>.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => repository.GetRoleSetting(42UL, cancellation.Token));

        Assert.Contains("selecting a server", exception.Message, StringComparison.Ordinal);
    }
}
