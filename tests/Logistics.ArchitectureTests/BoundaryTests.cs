using System.Reflection;
using Xunit;

namespace Logistics.ArchitectureTests;

public sealed class BoundaryTests
{
    [Fact]
    public void Domain_projects_do_not_reference_EfCore_or_broker_clients()
    {
        var assemblies = new[]
        {
            typeof(Booking.Domain.Booking).Assembly,
            typeof(Voyage.Domain.Voyage).Assembly,
            typeof(Replanning.Domain.ReplanningProcess).Assembly
        };

        var forbidden = new[] { "Microsoft.EntityFrameworkCore", "RabbitMQ", "Confluent.Kafka", "Camunda" };

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(x => x.Name ?? string.Empty);
            Assert.DoesNotContain(references, name => forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void Domain_assemblies_do_not_reference_infrastructure_or_api()
    {
        var assemblies = new[]
        {
            typeof(Booking.Domain.Booking).Assembly,
            typeof(Voyage.Domain.Voyage).Assembly,
            typeof(Replanning.Domain.ReplanningProcess).Assembly
        };

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(x => x.Name ?? string.Empty).ToArray();
            Assert.DoesNotContain(references, name => name.Contains("Logistics.Infrastructure", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, name => name.Contains("Logistics.Api", StringComparison.OrdinalIgnoreCase));
        }
    }
}
