using Microsoft.Extensions.DependencyInjection;
using Replanning.Application;
using Logistics.Infrastructure.Consumers;

namespace Logistics.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLogisticsInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IReplanningProcessRepository, ReplanningProcessRepository>();
        services.AddScoped<IBookingGateway, BookingGateway>();
        services.AddScoped<IVoyageGateway, VoyageGateway>();
        services.AddSingleton<IReplanningClock, SystemClock>();
        services.AddSingleton<IRetryPolicy, ExponentialRetryPolicy>();
        services.AddSingleton<IWorkflowFailureInjector, NoopWorkflowFailureInjector>();
        services.AddScoped<ReplanningOrchestrator>();
        services.AddScoped<VoyageDisruptedConsumer>();
        services.AddScoped<IMessagePublisher, InProcessMessagePublisher>();
        services.AddHostedService<OutboxPublisherWorker>();
        return services;
    }
}
