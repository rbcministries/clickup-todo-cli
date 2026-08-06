using ClickUpTodo.Tui.E2E;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the E2E harness's specificity-ranked <see cref="RouteTable{THandler}"/> (#488). The
/// point of the type is that dispatch does not depend on registration order and that an unresolvable
/// ambiguity fails loudly at construction rather than silently picking a wrong route — so those two
/// properties are what these tests pin. Handlers are plain <c>string</c>s (a stand-in for the real
/// delegate payload) so the tests exercise resolution, not the fake backend's responses.
/// </summary>
public class RouteTableTests
{
    private static Route<string> R(HttpMethod method, string pattern, string handler) => new(method, pattern, handler);

    // ── Ambiguity assertion (the safety net specificity ranking needs) ────────

    [Fact]
    public void EqualSpecificity_SameMethod_OverlappingRoutes_ThrowAtConstruction()
    {
        // The issue's own example: GET /task/{id}/x and GET /task/{y}/x — same shape, equal specificity,
        // matching the same paths. No specificity rule can pick between them, so the table must reject them.
        var ex = Assert.Throws<InvalidOperationException>(() => new RouteTable<string>(new[]
        {
            R(HttpMethod.Get, "task/{id}/x", "a"),
            R(HttpMethod.Get, "task/{y}/x", "b"),
        }));

        // Fails loudly, naming both offenders.
        Assert.Contains("task/{id}/x", ex.Message);
        Assert.Contains("task/{y}/x", ex.Message);
    }

    [Fact]
    public void EqualSpecificity_ButLiteralDiffers_IsNotAmbiguous()
    {
        // Same method, same shape (literal, placeholder, literal), but the leading literals differ, so no
        // single path matches both — they are genuinely distinct routes and must coexist.
        var table = new RouteTable<string>(new[]
        {
            R(HttpMethod.Get, "task/{id}/comment", "comment"),
            R(HttpMethod.Get, "comment/{id}/reply", "reply"),
        });

        Assert.Equal("comment", table.Resolve(HttpMethod.Get, "/api/v2/task/t1/comment"));
        Assert.Equal("reply", table.Resolve(HttpMethod.Get, "/api/v2/comment/c1/reply"));
    }

    [Fact]
    public void SamePattern_DifferentMethods_IsNotAmbiguous()
    {
        // The method disambiguates, so a GET and a PUT on the same shape are not a tie.
        var table = new RouteTable<string>(new[]
        {
            R(HttpMethod.Get, "task/{id}", "get"),
            R(HttpMethod.Put, "task/{id}", "put"),
        });

        Assert.Equal("get", table.Resolve(HttpMethod.Get, "/api/v2/task/t1"));
        Assert.Equal("put", table.Resolve(HttpMethod.Put, "/api/v2/task/t1"));
    }

    // ── Specificity ranking is order-independent (the :372 hazard) ────────────

    [Theory]
    [InlineData(true)]  // generic registered first
    [InlineData(false)] // specific registered first
    public void SpecificRoute_BeatsGeneric_RegardlessOfOrder(bool genericFirst)
    {
        var generic = R(HttpMethod.Get, "task/{id}/{action}", "generic");
        var specific = R(HttpMethod.Get, "task/{id}/comment", "specific");
        var table = new RouteTable<string>(genericFirst ? new[] { generic, specific } : new[] { specific, generic });

        // The specific route (more literal segments) always wins on the shared path...
        Assert.Equal("specific", table.Resolve(HttpMethod.Get, "/api/v2/task/t1/comment"));
        // ...while the generic still serves the paths only it matches.
        Assert.Equal("generic", table.Resolve(HttpMethod.Get, "/api/v2/task/t1/attachment"));
    }

    [Fact]
    public void GenericTaskRoute_DoesNotSwallowLongerCommentPath()
    {
        // The original if/else chain's core failure mode: a generic path.Contains("/task/") catch-all
        // swallowing /task/{id}/comment. Suffix-anchored matching means bare task/{id} never matches the
        // longer path — even with no comment route registered at all, it resolves to nothing, not the
        // detail route.
        var table = new RouteTable<string>(new[] { R(HttpMethod.Get, "task/{id}", "detail") });

        Assert.Equal("detail", table.Resolve(HttpMethod.Get, "/api/v2/task/t1"));
        Assert.Null(table.Resolve(HttpMethod.Get, "/api/v2/task/t1/comment"));
    }

    [Fact]
    public void LongerSuffixWins_WhenLiteralCountsTie()
    {
        // `user` and `task/{id}` both carry exactly one literal segment, so the literal count ties. The
        // longer pattern pins more of the path, so it is the more specific match on a path both accept —
        // and, because their segment counts differ, they are not flagged as ambiguous at construction.
        var table = new RouteTable<string>(new[]
        {
            R(HttpMethod.Get, "user", "user"),
            R(HttpMethod.Get, "task/{id}", "task"),
        });

        // A contrived path both patterns accept (…/task/user): the longer pattern wins deterministically.
        Assert.Equal("task", table.Resolve(HttpMethod.Get, "/api/v2/task/user"));
        // And each still serves its own canonical path.
        Assert.Equal("user", table.Resolve(HttpMethod.Get, "/api/v2/user"));
        Assert.Equal("task", table.Resolve(HttpMethod.Get, "/api/v2/task/t1"));
    }

    // ── Matching mechanics ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/v2/user")]
    [InlineData("/v2/user")]
    [InlineData("/user")]
    public void Matching_IgnoresBaseUrlPrefix(string path)
    {
        var table = new RouteTable<string>(new[] { R(HttpMethod.Get, "user", "user") });
        Assert.Equal("user", table.Resolve(HttpMethod.Get, path));
    }

    [Fact]
    public void MethodMismatch_DoesNotMatch()
    {
        var table = new RouteTable<string>(new[] { R(HttpMethod.Post, "list/{id}/task", "create") });
        Assert.Null(table.Resolve(HttpMethod.Get, "/api/v2/list/l1/task"));
        Assert.Equal("create", table.Resolve(HttpMethod.Post, "/api/v2/list/l1/task"));
    }

    [Fact]
    public void NoMatchingRoute_ReturnsDefault()
    {
        var table = new RouteTable<string>(new[] { R(HttpMethod.Get, "team", "teams") });
        Assert.Null(table.Resolve(HttpMethod.Get, "/api/v2/space/s1"));
    }

    [Fact]
    public void MembershipAndCreateTask_BothPost_ResolveDistinctly()
    {
        // Both are POST under /list/…; the old chain relied on the membership branch being ordered first.
        // Suffix anchoring separates them by shape (4 segments vs 3) with no ordering dependence.
        var table = new RouteTable<string>(new[]
        {
            R(HttpMethod.Post, "list/{listId}/task/{taskId}", "membership"),
            R(HttpMethod.Post, "list/{id}/task", "create"),
        });

        Assert.Equal("membership", table.Resolve(HttpMethod.Post, "/api/v2/list/l1/task/t1"));
        Assert.Equal("create", table.Resolve(HttpMethod.Post, "/api/v2/list/l1/task"));
    }

    [Fact]
    public void EmptyPattern_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RouteTable<string>(new[] { R(HttpMethod.Get, "", "x") }));
    }
}
