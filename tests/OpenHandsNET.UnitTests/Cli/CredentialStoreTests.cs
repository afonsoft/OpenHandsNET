using OpenHandsNET.Cli.Services;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>CredentialStore: persistência, mascaramento e permissões (SPEC-06).</summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ohn-creds-{Guid.NewGuid():N}");
    private readonly CredentialStore _store;

    public CredentialStoreTests() => _store = new CredentialStore(_dir);

    [Fact]
    public async Task Save_Then_Get_Returns_Credential()
    {
        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://localhost:8080",
            ApiKey = "sk-abc123def",
        });

        var got = await _store.GetAsync("http://localhost:8080");
        got.ShouldNotBeNull();
        got.ApiKey.ShouldBe("sk-abc123def");
    }

    [Fact]
    public async Task Save_Replaces_Entry_For_Same_Server()
    {
        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://x", ApiKey = "sk-one",
        });
        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://x", ApiKey = "sk-two",
        });

        (await _store.LoadAllAsync()).ShouldHaveSingleItem()
            .ApiKey.ShouldBe("sk-two");
    }

    [Fact]
    public async Task Delete_Removes_Only_Matching_Server()
    {
        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://a", ApiKey = "sk-a",
        });
        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://b", ApiKey = "sk-b",
        });

        (await _store.DeleteAsync("http://a")).ShouldBeTrue();
        (await _store.LoadAllAsync()).ShouldHaveSingleItem()
            .ServerUrl.ShouldBe("http://b");
        (await _store.DeleteAsync("http://a")).ShouldBeFalse();
    }

    [Fact]
    public void Masked_Never_Exposes_Full_Key()
    {
        var c = new CredentialStore.Credential
        {
            ServerUrl = "http://x", ApiKey = "sk-super-secret-value",
        };
        c.Masked.ShouldBe("sk-***alue");
        c.Masked.ShouldNotContain("super-secret");
    }

    [Fact]
    public async Task Saved_File_Has_0600_Permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // ACL de Windows verificada manualmente
        }

        await _store.SaveAsync(new CredentialStore.Credential
        {
            ServerUrl = "http://x", ApiKey = "sk-k",
        });

        var mode = File.GetUnixFileMode(
            Path.Combine(_dir, "credentials.json"));
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task Corrupt_File_Loads_As_Empty()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "credentials.json"), "{not json");

        (await _store.LoadAllAsync()).ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
