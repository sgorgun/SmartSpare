using System.Net;
using System.Text;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.UnitTests;

/// <summary>
/// The HTTP client of a Contracts query between services (ADR 0015 §4), on <see cref="HttpEmployeeDirectory"/>, the read
/// Inventory makes to Identity: it calls the internal endpoint, and a failing or unreachable service is a dependency error
/// (503), never an empty or wrong answer. Moved here from the warehouse client, removed with the warehouse copies (ADR 0029).
/// </summary>
public sealed class HttpEmployeeDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_client_posts_the_lookup_to_the_internal_endpoint()
    {
        var id = Guid.Parse("00000000-0000-4000-8000-000000000004");
        var handler = new StubHandler(HttpStatusCode.OK, $$"""[{"id":"{{id}}","code":"E000004","fullName":"J. B.","isActive":true}]""");
        var directory = new HttpEmployeeDirectory(new HttpClient(handler) { BaseAddress = new Uri("http://identity") });

        var found = await directory.FindAsync([id], Ct);

        found.ShouldBe([new EmployeeInfo(id, "E000004", "J. B.", true)]);
        handler.LastPath.ShouldBe(IdentityInternalApi.EmployeeLookup);
    }

    [Fact]
    public async Task No_ids_asks_nothing()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");
        var directory = new HttpEmployeeDirectory(new HttpClient(handler) { BaseAddress = new Uri("http://identity") });

        (await directory.FindAsync([], Ct)).ShouldBeEmpty();
        handler.LastPath.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_failing_identity_service_is_a_dependency_error_not_an_empty_list(HttpStatusCode status)
    {
        var directory = new HttpEmployeeDirectory(new HttpClient(new StubHandler(status, "{}")) { BaseAddress = new Uri("http://identity") });

        var error = await Should.ThrowAsync<DependencyUnavailableException>(() => directory.SearchAsync("jonas", 10, Ct));

        error.Service.ShouldBe("identity");
    }

    [Fact]
    public async Task An_unreachable_identity_service_is_a_dependency_error()
    {
        var directory = new HttpEmployeeDirectory(new HttpClient(new StubHandler(new HttpRequestException("refused"))) { BaseAddress = new Uri("http://identity") });

        await Should.ThrowAsync<DependencyUnavailableException>(() => directory.FindAccountOwnersAsync([Guid.NewGuid()], Ct));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception failure) => _failure = failure;

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return _failure is not null
                ? Task.FromException<HttpResponseMessage>(_failure)
                : Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
