using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application;

namespace SeatReservation.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_returns_the_same_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var result = services.AddApplication();

        Assert.Same(services, result);
    }
}
