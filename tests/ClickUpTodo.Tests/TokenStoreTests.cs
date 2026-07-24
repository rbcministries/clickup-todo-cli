using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    // Pin these round-trip tests to the file-backed backend (no OS secret store) so they are
    // deterministic on any host regardless of whether `secret-tool`/`security` happen to be installed.
    private TokenStore NewFileStore() => new(_dir, exists: _ => false);

    [Fact]
    public void Load_WhenNoFile_ReturnsNull()
    {
        var store = NewFileStore();

        Assert.False(store.Exists());
        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsToken()
    {
        var store = NewFileStore();
        const string token = "pk_12345_ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        store.Save(token);

        Assert.True(store.Exists());
        Assert.Equal(token, store.Load());
    }

    [Fact]
    public void Save_TrimsWhitespace()
    {
        var store = NewFileStore();

        store.Save("  pk_padded_token  ");

        Assert.Equal("pk_padded_token", store.Load());
    }

    [Fact]
    public void Delete_RemovesStoredToken()
    {
        var store = NewFileStore();
        store.Save("pk_to_delete");

        store.Delete();

        Assert.False(store.Exists());
        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
