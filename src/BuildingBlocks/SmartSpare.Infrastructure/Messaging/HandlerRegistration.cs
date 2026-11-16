using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging.Decorators;

namespace SmartSpare.Infrastructure.Messaging;

public static class HandlerRegistration
{
    /// <summary>
    /// Registers every command handler, query handler and FluentValidation validator in
    /// <paramref name="assembly"/>, including internal types. Called by each module installer.
    /// </summary>
    public static IServiceCollection AddHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(scan => scan
            .FromAssemblies(assembly)
            .AddClasses(
                classes => classes.AssignableToAny(
                    typeof(ICommandHandler<>),
                    typeof(ICommandHandler<,>),
                    typeof(IQueryHandler<,>)),
                publicOnly: false)
            .AsImplementedInterfaces()
            .WithScopedLifetime());

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }

    /// <summary>
    /// Wraps all registered handlers in the pipeline logging → validation → transaction → handler.
    /// Must be called once, by the host, after every module has registered its handlers;
    /// calling it per module would wrap earlier modules' handlers twice.
    /// </summary>
    public static IServiceCollection DecorateHandlers(this IServiceCollection services)
    {
        // Scrutor applies decorators inside-out: the last one registered is the outermost.
        services.TryDecorate(typeof(ICommandHandler<>), typeof(TransactionCommandDecorator<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionCommandDecorator<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(ValidationCommandDecorator<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationCommandDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationQueryDecorator<,>));

        services.TryDecorate(typeof(ICommandHandler<>), typeof(LoggingCommandDecorator<>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(LoggingCommandDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(LoggingQueryDecorator<,>));

        return services;
    }
}
