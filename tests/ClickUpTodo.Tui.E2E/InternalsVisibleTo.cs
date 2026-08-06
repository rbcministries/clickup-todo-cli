using System.Runtime.CompilerServices;

// #488: the unit tests in ClickUpTodo.Tests construct FakeClickUp to assert its *concrete* route table
// registers without an ambiguity throw — the one property `dotnet test` can't otherwise see, since
// BuildRoutes runs only when the harness boots under a PTY. Kept narrow: the test assembly only.
[assembly: InternalsVisibleTo("ClickUpTodo.Tests")]
