using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Persistence.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HotelSaas.BuildingBlocks.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    // Registers the shared persistence plumbing every service needs.
    public static IServiceCollection AddHotelSaasPersistence(
        this IServiceCollection services,
        Action<ErrorLogWriterOptions> configureErrorLog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureErrorLog);

        services.TryAddSingleton<IClock, SystemClock>();

        services.Configure(configureErrorLog);

        // Singleton: the buffer must outlive any request, because the whole
        // point is that the request does not wait for the write.
        services.TryAddSingleton<ChannelErrorLogWriter>();
        services.TryAddSingleton<IErrorLogWriter>(sp => sp.GetRequiredService<ChannelErrorLogWriter>());
        services.AddHostedService<ErrorLogBackgroundWriter>();

        return services;
    }
}
