using SmartSpare.ApiContracts;
using System.Net.Http.Json;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client.Features.Identity;

/// <summary>The <c>/bff</c> endpoints (STAGE_3_PLAN 1.2): sign-in, sign-out and the current user.</summary>
public class IdentityClient(HttpClient http) : ApiClientBase(http)
{
    public virtual Task<ApiResult<NoContent>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<NoContent>(
            new HttpRequestMessage(HttpMethod.Post, "bff/login") { Content = JsonContent.Create(request) }, cancellationToken);
    }

    public virtual Task<ApiResult<NoContent>> LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NoContent>(new HttpRequestMessage(HttpMethod.Post, "bff/logout"), cancellationToken);

    public virtual Task<ApiResult<CurrentUserResponse>> MeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<CurrentUserResponse>(new HttpRequestMessage(HttpMethod.Get, "bff/me"), cancellationToken);

    public virtual Task<ApiResult<PagedResponse<UserSummary>>> ListUsersAsync(string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        return SendAsync<PagedResponse<UserSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/users?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<UserFormOptions>> GetUserFormOptionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<UserFormOptions>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/users/form-options"), cancellationToken);

    /// <summary>unlock, deactivate or anonymise.</summary>
    public virtual Task<ApiResult<UserSummary>> UserActionAsync(Guid id, string action, CancellationToken cancellationToken = default) =>
        SendAsync<UserSummary>(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/users/{id}/{action}"), cancellationToken);

    // ---- Organisation (ADR 0026): positions and employees; writes send the version read as If-Match --------------

    public virtual Task<ApiResult<PagedResponse<PositionSummary>>> ListPositionsAsync(
        string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResponse<PositionSummary>>(
            new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/positions?" + Query(search, archived, page, pageSize)), cancellationToken);

    public virtual Task<ApiResult<PositionSummary>> CreatePositionAsync(CreatePositionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PositionSummary>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/identity/positions") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);

    public virtual Task<ApiResult<PositionSummary>> UpdatePositionAsync(
        short id, UpdatePositionRequest request, string version, CancellationToken cancellationToken = default) =>
        SendAsync<PositionSummary>(
            WithVersion(new HttpRequestMessage(HttpMethod.Put, $"api/v1/identity/positions/{id}") { Content = System.Net.Http.Json.JsonContent.Create(request) }, version),
            cancellationToken);

    /// <summary>archive or restore.</summary>
    public virtual Task<ApiResult<PositionSummary>> PositionActionAsync(short id, string action, string version, CancellationToken cancellationToken = default) =>
        SendAsync<PositionSummary>(WithVersion(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/positions/{id}/{action}"), version), cancellationToken);

    public virtual Task<ApiResult<PagedResponse<EmployeeSummary>>> ListEmployeesAsync(
        string? search, short? positionId, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = Query(search, archived, page, pageSize);
        if (positionId is { } position)
        {
            query += string.Create(System.Globalization.CultureInfo.InvariantCulture, $"&positionId={position}");
        }

        return SendAsync<PagedResponse<EmployeeSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/employees?" + query), cancellationToken);
    }

    public virtual Task<ApiResult<EmployeeSummary>> CreateEmployeeAsync(CreateEmployeeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/identity/employees") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);

    public virtual Task<ApiResult<EmployeeSummary>> UpdateEmployeeAsync(
        Guid id, UpdateEmployeeRequest request, string version, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(
            WithVersion(new HttpRequestMessage(HttpMethod.Put, $"api/v1/identity/employees/{id}") { Content = System.Net.Http.Json.JsonContent.Create(request) }, version),
            cancellationToken);

    public virtual Task<ApiResult<EmployeeSummary>> ArchiveEmployeeAsync(
        Guid id, ArchiveEmployeeRequest request, string version, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(
            WithVersion(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/employees/{id}/archive") { Content = System.Net.Http.Json.JsonContent.Create(request) }, version),
            cancellationToken);

    public virtual Task<ApiResult<EmployeeSummary>> RestoreEmployeeAsync(Guid id, string version, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(WithVersion(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/employees/{id}/restore"), version), cancellationToken);

    public virtual Task<ApiResult<EmployeeSummary>> CreateAccountAsync(Guid id, CreateAccountRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(
            new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/employees/{id}/account") { Content = System.Net.Http.Json.JsonContent.Create(request) },
            cancellationToken);

    public virtual Task<ApiResult<EmployeeSummary>> AnonymiseEmployeeAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<EmployeeSummary>(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/employees/{id}/anonymise"), cancellationToken);

    private static string Query(string? search, bool archived, int page, int pageSize)
    {
        var query = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"page={page}&pageSize={pageSize}&archived={(archived ? "true" : "false")}");
        return string.IsNullOrWhiteSpace(search) ? query : query + "&search=" + Uri.EscapeDataString(search.Trim());
    }

    /// <summary>Every role with its permissions (ADR 0028).</summary>
    public virtual Task<ApiResult<List<RoleSummary>>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<RoleSummary>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/roles"), cancellationToken);

    /// <summary>The permission catalogue.</summary>
    public virtual Task<ApiResult<List<PermissionDto>>> ListPermissionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<PermissionDto>>(new HttpRequestMessage(HttpMethod.Get, "api/v1/identity/permissions"), cancellationToken);

    public virtual Task<ApiResult<RoleSummary>> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RoleSummary>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/identity/roles") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);

    public virtual Task<ApiResult<RoleSummary>> UpdateRoleAsync(short id, UpdateRoleRequest request, string version, CancellationToken cancellationToken = default) =>
        SendAsync<RoleSummary>(
            WithVersion(new HttpRequestMessage(HttpMethod.Put, $"api/v1/identity/roles/{id}") { Content = System.Net.Http.Json.JsonContent.Create(request) }, version),
            cancellationToken);

    /// <summary>Restores a seeded role's shipped permissions.</summary>
    public virtual Task<ApiResult<RoleSummary>> ResetRoleAsync(short id, string version, CancellationToken cancellationToken = default) =>
        SendAsync<RoleSummary>(WithVersion(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/roles/{id}/reset"), version), cancellationToken);

    /// <summary>Confirms the permissions an upgrade granted to a customised role (#211).</summary>
    public virtual Task<ApiResult<RoleSummary>> ConfirmRoleUpgradeAsync(short id, string version, CancellationToken cancellationToken = default) =>
        SendAsync<RoleSummary>(WithVersion(new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/roles/{id}/confirm-upgrade"), version), cancellationToken);

    /// <summary>Creates an editable copy of a role.</summary>
    public virtual Task<ApiResult<RoleSummary>> CopyRoleAsync(short id, CopyRoleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RoleSummary>(
            new HttpRequestMessage(HttpMethod.Post, $"api/v1/identity/roles/{id}/copy") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);

    private static HttpRequestMessage WithVersion(HttpRequestMessage message, string version)
    {
        message.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return message;
    }

    public virtual Task<ApiResult<NoContent>> ChangeOwnPasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NoContent>(
            new HttpRequestMessage(HttpMethod.Post, "api/v1/identity/me/password") { Content = System.Net.Http.Json.JsonContent.Create(request) }, cancellationToken);
}
