using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure;

/// <summary>Pieces of the master-data mechanism (ADR 0025) that need no database.</summary>
public sealed class MasterDataTests
{
    [Theory]
    [InlineData("Rasa Kazlauskienė", "Rasa K.")]
    [InlineData("  Jonas   Balčiūnas  ", "Jonas B.")]
    [InlineData("Tomas Jonas Radzevičius", "Tomas R.")]
    [InlineData("Admin", "Admin")]
    [InlineData("", "")]
    public void The_history_names_people_by_first_name_and_initial(string fullName, string shortName) =>
        RecordHistory.ShortName(fullName).ShouldBe(shortName);

    [Fact]
    public async Task An_archive_refusal_lists_its_reasons_in_the_problem_details()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await CatalogErrors.ArchiveBlocked(["on hand 12 at KLP", "3 open transfers"]).ToProblem().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\"code\":\"SS501\"");
        body.ShouldContain("\"reasons\":[\"on hand 12 at KLP\",\"3 open transfers\"]");
    }

    [Fact]
    public void The_shared_errors_have_stable_codes_and_statuses()
    {
        CatalogErrors.Archived("brand").Code.ShouldBe("SS500");
        ResultHttpExtensions.StatusCodeFor(CatalogErrors.Archived("brand").Type).ShouldBe(StatusCodes.Status422UnprocessableEntity);
        ResultHttpExtensions.StatusCodeFor(CatalogErrors.CodeTaken.Type).ShouldBe(StatusCodes.Status409Conflict);
        ResultHttpExtensions.StatusCodeFor(CatalogErrors.CodeFixed.Type).ShouldBe(StatusCodes.Status422UnprocessableEntity);
        ResultHttpExtensions.StatusCodeFor(CatalogErrors.VersionRequired.Type).ShouldBe(StatusCodes.Status400BadRequest);
    }
}
