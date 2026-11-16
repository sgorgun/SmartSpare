using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging.Decorators;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.Messaging;

public static class HandlerRegistration
{
    /// <summary>
    /// Registers every command handler, query handler, integration event consumer and FluentValidation validator in
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
                    typeof(IQueryHandler<,>),
                    typeof(IIntegrationEventConsumer<>)),
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
        EnsureEveryCommandHasAUnitOfWork(services);

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

    /// <summary>
    /// The transaction decorator resolves a command's module by the command's assembly; a command
    /// whose assembly registered no unit of work must fail at start-up, not on its first call.
    /// </summary>
    private static void EnsureEveryCommandHasAUnitOfWork(IServiceCollection services)
    {
        var registered = services
            .Where(d => d.ServiceType == typeof(UnitOfWorkRegistration) && d.ImplementationInstance is UnitOfWorkRegistration)
            .Select(d => ((UnitOfWorkRegistration)d.ImplementationInstance!).ModuleAssembly)
            .ToHashSet();

        var missing = services
            .Where(d => d.ServiceType.IsGenericType
                && (d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<>)
                    || d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
            .Select(d => d.ServiceType.GetGenericArguments()[0].Assembly)
            .Where(a => !registered.Contains(a))
            .Select(a => a.GetName().Name)
            .Distinct()
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Commands without a unit of work in: " + string.Join(", ", missing)
                + ". Register the module's persistence (AddModulePersistence) or a unit of work (AddUnitOfWork).");
        }
    }
}
