namespace ClickUpTodo.Tui.E2E;

/// <summary>
/// One route in the E2E fake backend's dispatch table: an HTTP <paramref name="Method"/>, a
/// slash-delimited segment <paramref name="Pattern"/>, and a <paramref name="Handler"/> payload.
/// A pattern segment is either a literal (matched exactly) or a <c>{placeholder}</c> that matches
/// any single path segment — e.g. <c>list/{listId}/task/{taskId}</c>. Registration order carries
/// no meaning: <see cref="RouteTable{THandler}"/> resolves by specificity, so a specific route
/// always beats a generic one however the two were registered.
/// </summary>
public sealed record Route<THandler>(HttpMethod Method, string Pattern, THandler Handler);

/// <summary>
/// A specificity-ranked route table. Patterns match against the <b>trailing</b> segments of a
/// request path (so a base-URL prefix like <c>/api/v2</c> is ignored), and when several routes
/// match, the most specific wins: more literal segments first, then more segments. Registration
/// order is irrelevant — which is what makes the table safe to append to from many places without
/// the silent "a generic branch swallows a specific one" hazard the old ordered <c>if/else</c>
/// chain had (its trailing <c>path.Contains("/task/")</c> catch-all had to stay below every
/// specific <c>/task/…</c> branch, unenforced).
/// </summary>
/// <remarks>
/// Specificity ranking would only trade a <i>visible</i> ordering hazard for an <i>invisible</i>
/// one if ties went unnoticed, so the constructor <b>fails fast</b>: it throws when two routes for
/// the same method tie on specificity and could match a common path — an ambiguity no specificity
/// rule can resolve. Different-length or different-method routes never tie, so genuinely distinct
/// routes coexist; only true unresolvable overlaps are rejected.
/// </remarks>
public sealed class RouteTable<THandler>
{
    private readonly IReadOnlyList<Compiled> _routes;

    public RouteTable(IEnumerable<Route<THandler>> routes)
    {
        _routes = routes.Select(r => new Compiled(r)).ToList();
        AssertNoAmbiguity(_routes);
    }

    /// <summary>The handler of the most specific route matching <paramref name="method"/> and
    /// <paramref name="absolutePath"/>, or <c>default</c> (e.g. <c>null</c>) when none match.</summary>
    public THandler? Resolve(HttpMethod method, string absolutePath)
    {
        var segments = Split(absolutePath);
        Compiled? best = null;
        foreach (var route in _routes)
        {
            if (!route.Matches(method, segments))
                continue;
            if (best is null || route.IsMoreSpecificThan(best))
                best = route;
            else if (!best.IsMoreSpecificThan(route))
                // Two matching routes of equal specificity for one concrete path — impossible once the
                // constructor's ambiguity assertion has passed, so this only fires on a table built past
                // validation. Fail loudly rather than resolve nondeterministically.
                throw new InvalidOperationException(
                    $"Ambiguous match for {method} {absolutePath}: '{best.Route.Pattern}' and " +
                    $"'{route.Route.Pattern}' tie on specificity.");
        }
        return best is null ? default : best.Route.Handler;
    }

    private static void AssertNoAmbiguity(IReadOnlyList<Compiled> routes)
    {
        for (var i = 0; i < routes.Count; i++)
            for (var j = i + 1; j < routes.Count; j++)
            {
                var a = routes[i];
                var b = routes[j];
                if (a.Route.Method == b.Route.Method && a.TiesWith(b))
                    throw new InvalidOperationException(
                        $"Ambiguous E2E routes: {a.Route.Method} /{a.Route.Pattern} and " +
                        $"{b.Route.Method} /{b.Route.Pattern} have equal specificity.");
            }
    }

    private static string[] Split(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class Compiled
    {
        public Route<THandler> Route { get; }
        private readonly string[] _segments;
        private readonly bool[] _isLiteral;
        public int LiteralCount { get; }
        public int SegmentCount => _segments.Length;

        public Compiled(Route<THandler> route)
        {
            Route = route;
            _segments = Split(route.Pattern);
            if (_segments.Length == 0)
                throw new ArgumentException($"Route pattern must have at least one segment: '{route.Pattern}'.");
            _isLiteral = _segments.Select(s => !(s.StartsWith('{') && s.EndsWith('}'))).ToArray();
            LiteralCount = _isLiteral.Count(literal => literal);
        }

        /// <summary>True if the method agrees and the pattern matches the trailing segments of
        /// <paramref name="pathSegments"/> (right-aligned; literals must equal, placeholders match
        /// anything). Anchoring to the tail is what keeps <c>task/{id}</c> from matching a longer
        /// <c>/task/{id}/comment</c> path — the old catch-all's failure mode.</summary>
        public bool Matches(HttpMethod method, string[] pathSegments)
        {
            if (Route.Method != method || _segments.Length > pathSegments.Length)
                return false;
            for (var i = 0; i < _segments.Length; i++)
            {
                var p = _segments.Length - 1 - i;
                if (_isLiteral[p]
                    && !string.Equals(_segments[p], pathSegments[pathSegments.Length - 1 - i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>Specificity order: more literal segments wins; ties broken by more segments (a
        /// longer suffix pins more of the path, so it is the more precise match).</summary>
        public bool IsMoreSpecificThan(Compiled other) =>
            LiteralCount != other.LiteralCount
                ? LiteralCount > other.LiteralCount
                : SegmentCount > other.SegmentCount;

        /// <summary>True if this route and <paramref name="other"/> tie on specificity <b>and</b> could
        /// match a common path — the unresolvable case the table forbids. Equal specificity means equal
        /// literal- and segment-counts; a shared path exists when, position by position, the patterns are
        /// compatible (both placeholders, or equal literals). Two literals differing in the same position
        /// mean the patterns can never name the same path, so they do not tie.</summary>
        public bool TiesWith(Compiled other)
        {
            if (LiteralCount != other.LiteralCount || SegmentCount != other.SegmentCount)
                return false;
            for (var i = 0; i < _segments.Length; i++)
                if (_isLiteral[i] && other._isLiteral[i]
                    && !string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }
}
