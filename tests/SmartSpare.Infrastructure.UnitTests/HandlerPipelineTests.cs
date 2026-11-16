using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.UnitTests;

public sealed class HandlerPipelineTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHandlersFromAssembly(typeof(HandlerPipelineTests).Assembly);
        services.AddUnitOfWork<RecordingUnitOfWork>(typeof(HandlerPipelineTests).Assembly);
        services.DecorateHandlers();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task ValidQuery_ReachesHandler()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<Greet, string>>();

        var result = await handler.Handle(new Greet("Fleet"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("Hello, Fleet");
    }

    [Fact]
    public async Task InvalidQuery_IsRejectedBeforeHandler()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<Greet, string>>();

        var result = await handler.Handle(new Greet(string.Empty), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.ShouldBeOfType<ValidationError>();
        error.Errors.ShouldContainKey(nameof(Greet.Name));
    }

    [Fact]
    public async Task Command_IsDecoratedAndHandled()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<Rename>>();

        handler.GetType().Name.ShouldStartWith("Logging");

        var ok = await handler.Handle(new Rename("new name"), TestContext.Current.CancellationToken);
        var invalid = await handler.Handle(new Rename(string.Empty), TestContext.Current.CancellationToken);

        ok.IsSuccess.ShouldBeTrue();
        invalid.Error.ShouldBeOfType<ValidationError>();
    }

    internal sealed record Greet(string Name) : IQuery<string>;

    internal sealed class GreetHandler : IQueryHandler<Greet, string>
    {
        public Task<Result<string>> Handle(Greet query, CancellationToken cancellationToken) =>
            Task.FromResult<Result<string>>($"Hello, {query.Name}");
    }

    internal sealed class GreetValidator : AbstractValidator<Greet>
    {
        public GreetValidator() => RuleFor(q => q.Name).NotEmpty();
    }

    internal sealed record Rename(string Name) : ICommand;

    internal sealed class RenameHandler : ICommandHandler<Rename>
    {
        public Task<Result> Handle(Rename command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    internal sealed class RenameValidator : AbstractValidator<Rename>
    {
        public RenameValidator() => RuleFor(c => c.Name).NotEmpty();
    }
}
