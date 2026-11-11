# Part 2 — Code Review

Severity scale used below:

| Level | Meaning |
|---|---|
| 🔴 **Critical** | Exploitable security hole or data exposure. Block the merge. |
| 🟠 **High** | Wrong results, silent data loss, or will fall over under production load. Block the merge. |
| 🟡 **Medium** | Reliability / performance / API-design problem that will hurt soon. Fix before or right after merge. |
| 🔵 **Low** | Maintainability and style. Fix when touching the code. |

---

## Sample 1 — `OrdersController` (C# / ASP.NET Core)

### Findings, ranked by severity

| # | Severity | Problem | Why it matters |
|---|---|---|---|
| 1 | 🔴 Critical | **SQL injection.** `"... WHERE CustomerName = '" + customer + "'"` puts raw user input into SQL. | `?customer=' OR 1=1 --` returns every order. Because the connection is `sa`, an attacker can also run `'; DROP TABLE Orders; --`, read any database, or use `xp_cmdshell` to run OS commands. This is the worst possible combination with #2. |
| 2 | 🔴 Critical | **Hard-coded production credentials, using `sa`.** The connection string with the password is in source code. | Anyone with repo access (and every clone, fork, CI log and backup) now has full admin rights on `prod-sql01`. The password has to be treated as **compromised**: rotate it now and remove it from git history. Using `sa` breaks least privilege: the app only needs read/write on a few tables. It also cannot be changed per environment without a redeploy. |
| 3 | 🟠 High | **`Create` swallows every exception and returns `200 OK`.** `catch (Exception) { return Ok(); }` | The client is told the order was saved when it wasn't, so orders are lost silently. Nothing is logged, so nobody will ever find out. Even on success it returns `200` with no body or id, so the client cannot find the new order. |
| 4 | 🟠 High | **Over-posting / mass assignment.** `Create(Order order)` binds the EF entity straight from the request body. | A client can set any property, including `Id`, `Total`, `Tax`, a status, or navigation properties, and EF will save it. There is no validation (negative totals, empty customer). The API contract is also tied to the database schema. |
| 5 | 🟠 High | **Leaked ADO.NET resources.** `SqlConnection`, `SqlCommand` and `SqlDataReader` are never disposed (no `using`). | Each request keeps a pooled connection until the GC finalizes it. Under load the pool (default 100) runs out and requests start failing with *"Timeout expired… max pool size was reached"*. |
| 6 | 🟠 High | **`new HttpClient()` per row, blocked with `.Result`, inside an open data reader (N+1 remote calls).** | (a) A new `HttpClient` per call leaves sockets in `TIME_WAIT`, which exhausts ports. (b) `.Result` blocks a thread-pool thread per call (sync-over-async), which starves the pool under load and wraps errors in `AggregateException`. (c) One HTTP round trip per order, done one after another: 500 orders × 200 ms = 100 s. (d) The DB connection and reader stay open the whole time. (e) No timeout or error handling, so one failed tax call or one non-numeric body fails the whole search with a 500. |
| 7 | 🟡 Medium | **Synchronous I/O and no cancellation.** `Open()`, `ExecuteReader()`, `SaveChanges()` | Each one blocks a thread while waiting on the network. Async versions with a `CancellationToken` free the thread and stop work when the client disconnects. |
| 8 | 🟡 Medium | **`DbContext` created with `new` in a field initializer.** | It is never disposed, it bypasses DI so the connection string/options must be hard-coded in `OnConfiguring`, and it can't be swapped out in tests. Use constructor injection with `AddDbContext`. |
| 9 | 🟡 Medium | **No authorization.** | Anyone can list orders by customer name (personal data and spending) and create orders. Add `[Authorize]` and scope results to what the caller may see. |
| 10 | 🟡 Medium | **Unbounded `SELECT *` and no paging.** | A big customer returns thousands of rows, plus thousands of tax calls (see #6). `SELECT *` reads every column but only 2 are used, which prevents covering indexes and breaks when columns change. |
| 11 | 🟡 Medium | **`decimal.Parse(rate)` is culture-sensitive and throws.** | On a server with `de-DE` culture, `"0.19"` parses as `19`, or fails, so tax is silently wrong by 100×. Any unexpected body throws `FormatException` → 500. Also, the value is called a *rate* but stored in `order.Tax`: is it a rate or an amount? |
| 12 | 🟡 Medium | **Returns EF entities and no proper status codes.** `List<Order>` / `Ok()` | Exposes internal fields and can cause serialization loops through navigation properties. `POST` should return `201 Created` with a `Location` header. Errors should return `ProblemDetails`, not `200`. |
| 13 | 🔵 Low | **Unsafe column casts.** `(int)reader["Id"]`, `(decimal)reader["Total"]` | `InvalidCastException` on `DBNull` or a schema type change (`bigint`, `money`). Lookup by name on every row is also slower than typed getters by ordinal. EF projection avoids all of this. |
| 14 | 🔵 Low | **Two data-access styles in one controller** (raw ADO.NET and EF), **hard-coded external URL**, **no logging**, **no input validation** on `customer` (null or empty becomes `WHERE CustomerName = ''`). | Inconsistent and hard to maintain. Config belongs in `appsettings`/Key Vault. The lack of logging makes all the failures above invisible. |
| 15 | 🔵 Low | **Business logic in the controller.** | Tax lookup and data access belong in a service or typed client so they can be unit tested and reused. Better still, tax should be **calculated once when the order is created and stored**, not fetched again on every search. |

### Rewritten version

Main changes: EF Core with parameterised queries instead of string SQL; DI for the `DbContext` and a typed `HttpClient`; request/response DTOs; async with `CancellationToken` everywhere; paging; outbound calls made **after** the DB query finishes, run in parallel with a limit, with timeout/retry, and allowed to fail without failing the whole request; `[Authorize]`; correct status codes; secrets kept out of code.

```csharp
// ---------- Contracts ----------
public sealed record OrderResponse(int Id, string CustomerName, decimal Total, decimal? Tax);

public sealed record CreateOrderRequest
{
    [Required, MaxLength(200)]
    public string CustomerName { get; init; } = "";

    [Range(typeof(decimal), "0.01", "1000000")]
    public decimal Total { get; init; }
}

// ---------- Controller ----------
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class OrdersController(ShopDbContext db, ITaxClient taxClient) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet("search")]
    [ProducesResponseType<IReadOnlyList<OrderResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<OrderResponse>>> Search(
        [FromQuery, Required, MaxLength(200)] string customer,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, MaxPageSize)] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Parameterised by EF Core: no injection. Only the needed columns, one page only.
        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerName == customer)
            .OrderByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new { o.Id, o.CustomerName, o.Total })
            .ToListAsync(ct);

        // The DB connection is already back in the pool before any outbound HTTP happens.
        var taxes = await taxClient.GetTaxesAsync(orders.Select(o => o.Id).ToList(), ct);

        return orders
            .Select(o => new OrderResponse(o.Id, o.CustomerName, o.Total, taxes.GetValueOrDefault(o.Id)))
            .ToList();
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderResponse>> GetById(int id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new OrderResponse(o.Id, o.CustomerName, o.Total, o.Tax))
            .FirstOrDefaultAsync(ct);

        return order is null ? Problem(statusCode: 404, title: "Order not found") : order;
    }

    [HttpPost]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        // [ApiController] has already returned 400 + ValidationProblemDetails if the DTO is invalid.
        // Only the fields we allow are copied, so no over-posting.
        var order = new Order { CustomerName = request.CustomerName.Trim(), Total = request.Total };

        db.Orders.Add(order);
        // No catch-all here. Unexpected failures go to the global exception handler
        // (app.UseExceptionHandler + AddProblemDetails), which logs them and returns a 500
        // ProblemDetails, so the client never gets a false "200 OK".
        // Only catch exceptions you can actually handle (e.g. a unique-key violation → 409).
        await db.SaveChangesAsync(ct);

        var body = new OrderResponse(order.Id, order.CustomerName, order.Total, order.Tax);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, body);
    }
}

// ---------- Typed HTTP client for the tax service ----------
public interface ITaxClient
{
    /// <summary>Returns the tax per order id. Orders whose lookup failed are missing from the result.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetTaxesAsync(IReadOnlyCollection<int> orderIds, CancellationToken ct);
}

public sealed class TaxClient(HttpClient http, ILogger<TaxClient> logger) : ITaxClient
{
    public async Task<IReadOnlyDictionary<int, decimal>> GetTaxesAsync(
        IReadOnlyCollection<int> orderIds, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<int, decimal>();

        // Limited parallelism: fast, but we don't flood the tax service.
        // (If the tax API has a batch endpoint, one call for all ids is better still.)
        await Parallel.ForEachAsync(
            orderIds,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    var raw = await http.GetStringAsync($"tax/{id}", token);
                    if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var tax))
                        result[id] = tax;
                    else
                        logger.LogWarning("Tax service returned a non-numeric value for order {OrderId}", id);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException ||
                    (ex is TaskCanceledException && !token.IsCancellationRequested)) // timeout, not client abort
                {
                    // Partial failure: return the order without tax instead of failing the whole search.
                    logger.LogWarning(ex, "Tax lookup failed for order {OrderId}", id);
                }
            });

        return result;
    }
}

// ---------- Program.cs ----------
// Connection string comes from configuration: user-secrets locally, Key Vault / App Service
// settings in Azure, ideally with Managed Identity (no password at all) and a least-privilege login.
builder.Services.AddDbContext<ShopDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Shop"),
                   sql => sql.EnableRetryOnFailure()));

builder.Services
    .AddHttpClient<ITaxClient, TaxClient>(c =>
        c.BaseAddress = new Uri(builder.Configuration["TaxApi:BaseUrl"]!))
    .AddStandardResilienceHandler(); // Microsoft.Extensions.Http.Resilience: timeouts, retry w/ jitter, circuit breaker

builder.Services.AddProblemDetails();
// ...
app.UseExceptionHandler();
```

Follow-up actions outside the code:

1. **Rotate the `sa` password now**, and remove it from git history (`git filter-repo` / BFG). Treat it as compromised.
2. Create a dedicated SQL login (or Entra ID managed identity) with only the permissions the app needs.
3. Check the SQL Server audit logs for signs that the injection was already exploited.
4. Longer term, calculate and store `Tax` when the order is created, so search makes **zero** outbound calls.

---

## Sample 2 — SQL Server report query

```sql
SELECT *
FROM Orders o, Customers c
WHERE o.CustomerId = c.Id
  AND YEAR(o.OrderDate) = 2025
  AND c.Name LIKE '%' + @name + '%'
  AND o.Id NOT IN (SELECT OrderId FROM Refunds)
ORDER BY o.OrderDate DESC;
```

### Findings, ranked by severity

| # | Severity | Problem | Why it matters |
|---|---|---|---|
| 1 | 🟠 High (correctness) | **`NOT IN` with a nullable subquery.** | If **one** row in `Refunds` has `OrderId = NULL`, then `o.Id NOT IN (…, NULL)` evaluates to `UNKNOWN` for every row and **the report returns nothing**, with no error. `NOT EXISTS` has no such trap, and SQL Server reliably turns it into an anti-semi-join. |
| 2 | 🟠 High (perf) | **`YEAR(o.OrderDate) = 2025` is non-sargable.** | Wrapping the column in a function stops SQL Server from seeking an index on `OrderDate`. It must read **all 20 M rows** and compute `YEAR()` for each. A half-open range (`>= '2025-01-01' AND < '2026-01-01'`) gives the same result and can use an index seek. This is probably the biggest single cause of the slowness. |
| 3 | 🟠 High (perf) | **`LIKE '%' + @name + '%'` (leading wildcard) is non-sargable.** | A B-tree index can't seek on a "contains" search, so `Customers` is always scanned. That's acceptable only if we scan a **narrow** index on `Name` and the customer table is much smaller than orders. If it isn't, use full-text search (word or prefix matches), or change the requirement to "starts with" (`@name + '%'`, which can seek). |
| 4 | 🟠 High (perf) | **No paging, and a sort over the whole result.** | All 2025 orders for a common term like `"a"` could be millions of rows. They all get sorted (memory grant, tempdb spills) and sent to the app. Use `OFFSET/FETCH`, or keyset pagination on `(OrderDate, Id)` for deep pages. |
| 5 | 🟡 Medium | **`SELECT *`** | Returns every column of both tables, including two columns both called `Id`, which is ambiguous for the client. It also prevents covering indexes, so every row needs a key lookup, and more data crosses the network. The query breaks silently when columns are added. |
| 6 | 🟡 Medium | **Old-style comma join (`FROM a, b WHERE …`).** | Same plan, but easy to turn into an accidental cartesian product when someone edits the `WHERE`. Explicit `INNER JOIN … ON` keeps join logic and filters apart. |
| 7 | 🟡 Medium | **Non-deterministic order.** `ORDER BY o.OrderDate DESC` | Ties (same timestamp) come back in any order, so rows repeat or go missing between pages. Add a unique tiebreaker: `, o.Id DESC`. |
| 8 | 🔵 Low | **Parameter type and sniffing.** | If `@name` is `nvarchar` and `Customers.Name` is `varchar` (or the other way round), an implicit conversion can stop index use. Declare the parameter with the column's exact type. Results for `@name = 'a'` and `@name = 'Acme Holdings Ltd'` differ hugely in size, so a cached plan can be bad for one of them. Consider `OPTION (RECOMPILE)` for this report, since compile cost is tiny next to execution. |
| 9 | 🔵 Low | **Hard-coded year, unescaped wildcards.** | Pass `@from`/`@to` so the query works for any period. Escape `%`, `_` and `[` in `@name` in the application, or a search for `"50%"` will match too much. |

### Rewritten query

```sql
-- @name  nvarchar(200)  (same type as Customers.Name; % _ [ escaped by the caller)
-- @from  date = '2025-01-01', @to date = '2026-01-01'  (half-open range)
-- @offset int, @pageSize int

SELECT
    o.Id          AS OrderId,
    o.OrderDate,
    o.Total,
    c.Id          AS CustomerId,
    c.Name        AS CustomerName
FROM dbo.Orders AS o
INNER JOIN dbo.Customers AS c
        ON c.Id = o.CustomerId
WHERE o.OrderDate >= @from
  AND o.OrderDate <  @to
  AND c.Name LIKE N'%' + @name + N'%'
  AND NOT EXISTS (SELECT 1 FROM dbo.Refunds AS r WHERE r.OrderId = o.Id)
ORDER BY o.OrderDate DESC, o.Id DESC
OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
OPTION (RECOMPILE);
```

If the optimizer misjudges how many customers match (which is likely, since it can't estimate a `%term%` well), split it into two steps so the second query has real row counts:

```sql
SELECT Id INTO #matchingCustomers
FROM dbo.Customers
WHERE Name LIKE N'%' + @name + N'%';          -- one scan of a narrow index

-- then join Orders to #matchingCustomers using the same WHERE / NOT EXISTS / ORDER BY as above
```

### Indexes I would consider

```sql
-- 1. Main access path: for each matching customer, seek straight to their orders in the
--    date range. Already sorted by date, and covering, so no key lookups.
CREATE INDEX IX_Orders_CustomerId_OrderDate
    ON dbo.Orders (CustomerId, OrderDate DESC, Id DESC)
    INCLUDE (Total);

-- 2. For broad search terms (many customers match): read only the 2025 range in output order
--    and stop after one page, with no sort.
CREATE INDEX IX_Orders_OrderDate
    ON dbo.Orders (OrderDate DESC, Id DESC)
    INCLUDE (CustomerId, Total);

-- 3. Makes the anti-join a seek per order instead of scanning Refunds. Foreign-key columns
--    are NOT indexed automatically in SQL Server. Use UNIQUE if an order can be refunded once.
CREATE INDEX IX_Refunds_OrderId ON dbo.Refunds (OrderId);

-- 4. A narrow index for the unavoidable '%term%' scan: much cheaper than scanning the
--    clustered index with every customer column.
CREATE INDEX IX_Customers_Name ON dbo.Customers (Name) INCLUDE (Id);
```

Other options, depending on how the report is used:

- **Full-text index on `Customers.Name`** with `CONTAINS(c.Name, '"term*"')`. Fast word and prefix matching, if "contains a word starting with" is acceptable instead of "contains any substring".
- **Make `Refunds.OrderId` `NOT NULL`** (and a FK) if the data model allows it. That removes the `NOT IN` trap at the root.
- For heavy reporting over a 20 M-row table, a **nonclustered columnstore index** on `Orders`, **partitioning by `OrderDate`**, or running reports on a **read replica** keeps them away from OLTP traffic.
- Check with the actual execution plan and `SET STATISTICS IO, TIME ON` before and after. Indexes 1 and 2 overlap, so keep only the one(s) the plans actually use. Each index slows down writes to `Orders`.


---

The optional AI/LLM bonus answer is in [BONUS.md](BONUS.md).
