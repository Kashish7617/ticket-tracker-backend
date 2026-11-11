using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace TicketTracker.Api.Tests;

public sealed class TicketsApiTests : IDisposable
{
    // Mirrors the API's wire format: camelCase properties, snake_case enum strings.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly TicketApiFactory _factory = new();
    private readonly HttpClient _client;

    public TicketsApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // DTOs as a client would see them (deliberately not reusing the API's own types).
    private sealed record TicketDto(
        int Id, string Title, string? Description, string Status, string Priority,
        DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record PageDto(List<TicketDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);

    private sealed record SummaryDto(int Total, Dictionary<string, int> ByStatus, Dictionary<string, int> ByPriority);

    private async Task<TicketDto> CreateAsync(string title, string priority = "medium", string? status = null)
    {
        var response = await _client.PostAsJsonAsync("/api/tickets", new { title, priority, status });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TicketDto>(Json))!;
    }

    private async Task<PageDto> ListAsync(string query = "")
    {
        var page = await _client.GetFromJsonAsync<PageDto>($"/api/tickets{query}", Json);
        return page!;
    }

    // ---- Create -------------------------------------------------------------------------

    [Fact]
    public async Task Create_ValidTicket_Returns201WithLocationAndDefaults()
    {
        var response = await _client.PostAsJsonAsync("/api/tickets", new
        {
            title = "  Cannot log in  ",
            description = "Customer gets a 500 on the login page.",
            priority = "high",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await response.Content.ReadFromJsonAsync<TicketDto>(Json);
        Assert.NotNull(ticket);
        Assert.True(ticket.Id > 0);
        Assert.Equal("Cannot log in", ticket.Title); // trimmed
        Assert.Equal("open", ticket.Status); // default
        Assert.Equal("high", ticket.Priority);
        Assert.Equal(DateTimeKind.Utc, ticket.CreatedAt.Kind);
        Assert.Equal(ticket.CreatedAt, ticket.UpdatedAt);
        Assert.Equal($"/api/tickets/{ticket.Id}", response.Headers.Location?.AbsolutePath);

        var fetched = await _client.GetFromJsonAsync<TicketDto>(response.Headers.Location, Json);
        Assert.Equal(ticket, fetched);
    }

    // ---- Validation ---------------------------------------------------------------------

    public static TheoryData<object, string> InvalidCreateRequests => new()
    {
        { new { title = "", priority = "low" }, "Title" },
        { new { title = "   ", priority = "low" }, "Title" },
        { new { title = new string('x', 121), priority = "low" }, "Title" },
        { new { title = "Valid title" }, "Priority" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreateRequests))]
    public async Task Create_InvalidInput_Returns400ValidationProblem(object body, string expectedField)
    {
        var response = await _client.PostAsJsonAsync("/api/tickets", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains(expectedField, problem.Errors.Keys);
    }

    [Theory]
    [InlineData("""{ "title": "x", "priority": "urgent" }""")] // unknown enum value
    [InlineData("""{ "title": "x", "priority": 2 }""")] // integers are not accepted
    [InlineData("""{ "title": "x", "priority": "low", "status": "done" }""")]
    public async Task Create_InvalidEnum_Returns400(string json)
    {
        var response = await _client.PostAsync(
            "/api/tickets", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("?status=pending")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    public async Task List_InvalidQuery_Returns400(string query)
    {
        var response = await _client.GetAsync($"/api/tickets{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.NotEmpty(problem.Errors);
    }

    // ---- Not found ----------------------------------------------------------------------

    [Fact]
    public async Task MissingTicket_Returns404ProblemDetails_ForGetUpdateAndDelete()
    {
        var get = await _client.GetAsync("/api/tickets/9999");
        var patch = await _client.PatchAsJsonAsync("/api/tickets/9999/status", new { status = "closed" });
        var delete = await _client.DeleteAsync("/api/tickets/9999");

        foreach (var response in new[] { get, patch, delete })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal("Ticket not found", problem?.Title);
        }
    }

    // ---- Filtering, search, paging -------------------------------------------------------

    [Fact]
    public async Task List_FiltersByStatus_AndSearchesTitleCaseInsensitively()
    {
        await CreateAsync("Login page broken", status: "open");
        await CreateAsync("Cannot LOGIN on mobile", status: "in_progress");
        await CreateAsync("Invoice total wrong", status: "in_progress");
        await CreateAsync("Password reset email missing", status: "closed");

        var inProgress = await ListAsync("?status=in_progress");
        Assert.Equal(2, inProgress.TotalCount);
        Assert.All(inProgress.Items, t => Assert.Equal("in_progress", t.Status));

        var login = await ListAsync("?search=login");
        Assert.Equal(2, login.TotalCount);

        var combined = await ListAsync("?status=in_progress&search=login");
        var only = Assert.Single(combined.Items);
        Assert.Equal("Cannot LOGIN on mobile", only.Title);
    }

    [Fact]
    public async Task List_Search_TreatsWildcardsLiterally()
    {
        await CreateAsync("Discount of 50% not applied");
        await CreateAsync("Discount of 500 dollars");

        var result = await ListAsync("?search=" + Uri.EscapeDataString("50%"));

        var only = Assert.Single(result.Items);
        Assert.Equal("Discount of 50% not applied", only.Title);
    }

    [Fact]
    public async Task List_Paginates_NewestFirst()
    {
        for (var i = 1; i <= 5; i++) await CreateAsync($"Ticket {i}");

        var page1 = await ListAsync("?page=1&pageSize=2");
        var page3 = await ListAsync("?page=3&pageSize=2");

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(["Ticket 5", "Ticket 4"], page1.Items.Select(t => t.Title));
        Assert.Equal(["Ticket 1"], page3.Items.Select(t => t.Title));
    }

    // ---- Update / delete ----------------------------------------------------------------

    [Fact]
    public async Task UpdateStatus_ChangesStatusAndBumpsUpdatedAt()
    {
        var created = await CreateAsync("Printer on fire");

        var response = await _client.PatchAsJsonAsync(
            $"/api/tickets/{created.Id}/status", new { status = "in_progress" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDto>(Json);
        Assert.Equal("in_progress", updated!.Status);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Put_ReplacesEditableFields()
    {
        var created = await CreateAsync("Old title", priority: "low");

        var response = await _client.PutAsJsonAsync($"/api/tickets/{created.Id}", new
        {
            title = "New title",
            description = "Now with details",
            status = "closed",
            priority = "high",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TicketDto>(Json);
        Assert.Equal(("New title", "Now with details", "closed", "high"),
            (updated!.Title, updated.Description, updated.Status, updated.Priority));
    }

    [Fact]
    public async Task Delete_RemovesTicket()
    {
        var created = await CreateAsync("Delete me");

        var delete = await _client.DeleteAsync($"/api/tickets/{created.Id}");
        var get = await _client.GetAsync($"/api/tickets/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ---- Summary ------------------------------------------------------------------------

    [Fact]
    public async Task Summary_CountsPerStatusAndPriority_IncludingZeroes()
    {
        await CreateAsync("a", priority: "high", status: "open");
        await CreateAsync("b", priority: "high", status: "open");
        await CreateAsync("c", priority: "low", status: "closed");

        var summary = await _client.GetFromJsonAsync<SummaryDto>("/api/tickets/summary", Json);

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Total);
        Assert.Equal(new Dictionary<string, int> { ["open"] = 2, ["in_progress"] = 0, ["closed"] = 1 }, summary.ByStatus);
        Assert.Equal(new Dictionary<string, int> { ["low"] = 1, ["medium"] = 0, ["high"] = 2 }, summary.ByPriority);
    }
}
