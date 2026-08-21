using Replanning.Domain;
using Voyage.Domain;
using Xunit;

namespace Logistics.IntegrationTests;

public class DomainConcurrencyTests
{
    [Fact]
    public void Completed_process_is_terminal()
    {
        var process = new ReplanningProcess(Guid.NewGuid(), "B001", Guid.NewGuid());

        process.ConfirmEligibility();
        process.SelectAlternative("V200");
        process.MarkCapacityReserved();
        process.MarkBookingReassigned("V200");
        process.Complete();

        Assert.Equal(ReplanningState.Completed, process.State);
        Assert.Throws<InvalidOperationException>(() => process.ScheduleRetry("late", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Voyage_disruption_is_idempotent_at_domain_level()
    {
        var voyage = new Voyage.Domain.Voyage("V100", 10);

        voyage.Disrupt();
        voyage.Disrupt();

        Assert.Equal(VoyageState.Disrupted, voyage.State);
    }
}
