using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clickup-todo-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WhenNoFile_ReturnsNull()
    {
        var store = new TokenStore(_dir);

        Assert.False(store.Exists());
        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsToken()
    {
        var store = new TokenStore(_dir);
        const string token = "pk_12345_ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        store.Save(token);

        Assert.True(store.Exists());
        Assert.Equal(token, store.Load());
    }

    [Fact]
    public void Save_TrimsWhitespace()
    {
        var store = new TokenStore(_dir);

        store.Save("  pk_padded_token  ");

        Assert.Equal("pk_padded_token", store.Load());
    }

    [Fact]
    public void Delete_RemovesStoredToken()
    {
        var store = new TokenStore(_dir);
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
